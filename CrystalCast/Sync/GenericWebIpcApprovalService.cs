using CrystalCast.Video;

namespace CrystalCast.Sync;

internal enum GenericWebIpcAccess
{
    Allowed,
    Pending,
    Denied,
}

internal sealed record GenericWebIpcApprovalRequest(
    long RequestId,
    string ScreenId,
    string ScreenName,
    string ReportedOwnerId,
    string Url,
    string Origin,
    bool IsRedirect);

internal sealed class GenericWebIpcApprovalService : IDisposable
{
    private const int MaximumPendingRequests = Configuration.MaxIpcBrowserScreens;

    private readonly object sync = new();
    private readonly HashSet<GenericWebOrigin> trustedOrigins = [];
    private readonly HashSet<GenericWebOrigin> deniedOrigins = [];
    private readonly HashSet<ScreenOrigin> grantedScreenOrigins = [];
    private readonly HashSet<ScreenOrigin> deniedScreenOrigins = [];
    private readonly Dictionary<string, string> configuredDocuments = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> activeDocuments = new(StringComparer.Ordinal);
    private readonly Dictionary<ScreenOrigin, long> pendingByScreenOrigin = [];
    private readonly SortedDictionary<long, PendingRequest> pendingById = [];
    private readonly Dictionary<string, long> screenRevisions = new(StringComparer.Ordinal);
    private long nextRequestId;

    public GenericWebIpcAccess EvaluateInitial(BrowserScreenProfile screen)
    {
        if (!RequiresApproval(screen))
            return GenericWebIpcAccess.Allowed;

        if (!TryParseOrigin(screen.GenericWebUrl, out var uri, out var origin))
            return GenericWebIpcAccess.Denied;

        lock (sync)
        {
            SynchronizeConfiguredDocument(screen.ScreenId, uri.AbsoluteUri);
            return EvaluateLocked(screen, uri, origin, isRedirect: false);
        }
    }

    public bool IsNavigationAllowed(BrowserScreenProfile screen, string candidate)
    {
        if (!RequiresApproval(screen))
            return BrowserNavigationPolicy.IsAllowedGenericDocument(candidate);

        if (!TryParseOrigin(candidate, out var uri, out var origin))
            return false;

        lock (sync)
        {
            var access = EvaluateLocked(screen, uri, origin, isRedirect: true);
            if (access == GenericWebIpcAccess.Allowed)
                activeDocuments[screen.ScreenId] = uri.AbsoluteUri;

            return access == GenericWebIpcAccess.Allowed;
        }
    }

    public string GetApprovedStartUrl(BrowserScreenProfile screen)
    {
        lock (sync)
            return activeDocuments.GetValueOrDefault(screen.ScreenId, screen.GenericWebUrl);
    }

    public void RemoveScreen(string screenId)
    {
        lock (sync)
        {
            configuredDocuments.Remove(screenId);
            activeDocuments.Remove(screenId);
            screenRevisions.Remove(screenId);
            grantedScreenOrigins.RemoveWhere(value => string.Equals(value.ScreenId, screenId, StringComparison.Ordinal));
            deniedScreenOrigins.RemoveWhere(value => string.Equals(value.ScreenId, screenId, StringComparison.Ordinal));

            var staleRequests = pendingById.Values
                .Where(value => string.Equals(value.Request.ScreenId, screenId, StringComparison.Ordinal))
                .ToArray();
            foreach (var stale in staleRequests)
                RemovePending(stale);
        }
    }

    public void ResetSessionDecisions()
    {
        lock (sync)
        {
            trustedOrigins.Clear();
            deniedOrigins.Clear();
            grantedScreenOrigins.Clear();
            deniedScreenOrigins.Clear();
            activeDocuments.Clear();

            foreach (var (screenId, document) in configuredDocuments)
            {
                activeDocuments[screenId] = document;
                IncrementRevision(screenId);
            }
        }
    }

    public long GetScreenRevision(string screenId)
    {
        lock (sync)
            return screenRevisions.GetValueOrDefault(screenId);
    }

    public bool TryGetCurrent(out GenericWebIpcApprovalRequest request)
    {
        lock (sync)
        {
            if (pendingById.Count == 0)
            {
                request = null!;
                return false;
            }

            request = pendingById.First().Value.Request;
            return true;
        }
    }

    public bool Approve(long requestId, bool trustForSession)
    {
        lock (sync)
        {
            if (!pendingById.TryGetValue(requestId, out var pending))
                return false;

            if (trustForSession)
            {
                trustedOrigins.Add(pending.Origin);
                var matchingRequests = pendingById.Values
                    .Where(candidate => candidate.Origin == pending.Origin)
                    .ToArray();
                foreach (var matching in matchingRequests)
                {
                    grantedScreenOrigins.Add(matching.ScreenOrigin);
                    activeDocuments[matching.Request.ScreenId] = matching.Request.Url;
                    IncrementRevision(matching.Request.ScreenId);
                    RemovePending(matching);
                }
            }
            else
            {
                grantedScreenOrigins.Add(pending.ScreenOrigin);
                deniedScreenOrigins.Remove(pending.ScreenOrigin);
                activeDocuments[pending.Request.ScreenId] = pending.Request.Url;
                IncrementRevision(pending.Request.ScreenId);
                RemovePending(pending);
            }

            return true;
        }
    }

    public bool Reject(long requestId, bool blockOriginForSession = false)
    {
        lock (sync)
        {
            if (!pendingById.TryGetValue(requestId, out var pending))
                return false;

            if (blockOriginForSession)
            {
                deniedOrigins.Add(pending.Origin);
                var matchingRequests = pendingById.Values
                    .Where(candidate => candidate.Origin == pending.Origin)
                    .ToArray();
                foreach (var matching in matchingRequests)
                    RemovePending(matching);

                return true;
            }

            deniedScreenOrigins.Add(pending.ScreenOrigin);
            RemovePending(pending);
            return true;
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            trustedOrigins.Clear();
            deniedOrigins.Clear();
            grantedScreenOrigins.Clear();
            deniedScreenOrigins.Clear();
            configuredDocuments.Clear();
            activeDocuments.Clear();
            pendingByScreenOrigin.Clear();
            pendingById.Clear();
            screenRevisions.Clear();
        }
    }

    private static bool RequiresApproval(BrowserScreenProfile screen)
    {
        return screen.CreatedByIpc && screen.ProviderKind == BrowserSourceProviderKind.GenericWeb;
    }

    private GenericWebIpcAccess EvaluateLocked(
        BrowserScreenProfile screen,
        Uri uri,
        GenericWebOrigin origin,
        bool isRedirect)
    {
        var screenOrigin = new ScreenOrigin(screen.ScreenId, origin);
        if (trustedOrigins.Contains(origin))
            return GenericWebIpcAccess.Allowed;

        if (deniedOrigins.Contains(origin))
            return GenericWebIpcAccess.Denied;

        if (grantedScreenOrigins.Contains(screenOrigin))
            return GenericWebIpcAccess.Allowed;

        if (deniedScreenOrigins.Contains(screenOrigin))
            return GenericWebIpcAccess.Denied;

        if (pendingByScreenOrigin.ContainsKey(screenOrigin))
            return GenericWebIpcAccess.Pending;

        if (pendingById.Count >= MaximumPendingRequests)
            return GenericWebIpcAccess.Denied;

        var requestId = ++nextRequestId;
        var request = new GenericWebIpcApprovalRequest(
            requestId,
            screen.ScreenId,
            screen.Name,
            screen.IpcOwnerId,
            uri.AbsoluteUri,
            origin.DisplayName,
            isRedirect);
        var pending = new PendingRequest(request, origin, screenOrigin);
        pendingByScreenOrigin.Add(screenOrigin, requestId);
        pendingById.Add(requestId, pending);
        return GenericWebIpcAccess.Pending;
    }

    private void SynchronizeConfiguredDocument(string screenId, string document)
    {
        if (configuredDocuments.TryGetValue(screenId, out var current)
            && string.Equals(current, document, StringComparison.Ordinal))
            return;

        configuredDocuments[screenId] = document;
        activeDocuments[screenId] = document;
        grantedScreenOrigins.RemoveWhere(value => string.Equals(value.ScreenId, screenId, StringComparison.Ordinal));
        deniedScreenOrigins.RemoveWhere(value => string.Equals(value.ScreenId, screenId, StringComparison.Ordinal));

        var staleRequests = pendingById.Values
            .Where(value => string.Equals(value.Request.ScreenId, screenId, StringComparison.Ordinal))
            .ToArray();
        foreach (var stale in staleRequests)
            RemovePending(stale);

        IncrementRevision(screenId);
    }

    private void IncrementRevision(string screenId)
    {
        screenRevisions[screenId] = screenRevisions.GetValueOrDefault(screenId) + 1;
    }

    private void RemovePending(PendingRequest pending)
    {
        pendingById.Remove(pending.Request.RequestId);
        pendingByScreenOrigin.Remove(pending.ScreenOrigin);
    }

    private static bool TryParseOrigin(string candidate, out Uri uri, out GenericWebOrigin origin)
    {
        if (!BrowserUriPolicy.TryCreateHttpUri(candidate, out uri))
        {
            origin = default;
            return false;
        }

        origin = new GenericWebOrigin(
            uri.Scheme.ToLowerInvariant(),
            uri.IdnHost.TrimEnd('.').ToLowerInvariant(),
            uri.IsDefaultPort ? -1 : uri.Port);
        return true;
    }

    private readonly record struct GenericWebOrigin(string Scheme, string Host, int Port)
    {
        public string DisplayName
        {
            get
            {
                var displayHost = Host.Contains(':', StringComparison.Ordinal) ? $"[{Host}]" : Host;
                return Port < 0 ? $"{Scheme}://{displayHost}" : $"{Scheme}://{displayHost}:{Port}";
            }
        }
    }
    private readonly record struct ScreenOrigin(string ScreenId, GenericWebOrigin Origin);
    private sealed record PendingRequest(
        GenericWebIpcApprovalRequest Request,
        GenericWebOrigin Origin,
        ScreenOrigin ScreenOrigin);
}
