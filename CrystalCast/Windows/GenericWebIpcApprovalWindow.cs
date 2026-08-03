using System.Numerics;
using CrystalCast.Sync;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace CrystalCast.Windows;

internal sealed class GenericWebIpcApprovalWindow : Window, IDisposable
{
    private readonly GenericWebIpcApprovalService approvals;
    private long displayedRequestId;
    private bool rememberDomainChoice;

    public GenericWebIpcApprovalWindow(GenericWebIpcApprovalService approvals)
        : base(
            "Allow IPC web content?###CrystalCastGenericWebIpcApproval",
            ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoResize)
    {
        this.approvals = approvals;
        Size = new Vector2(620.0f, 430.0f);
        SizeCondition = ImGuiCond.Always;
        IsOpen = false;
        ShowCloseButton = false;
        RespectCloseHotkey = false;
    }

    public void RefreshState()
    {
        if (!approvals.TryGetCurrent(out var request))
        {
            IsOpen = false;
            displayedRequestId = 0;
            rememberDomainChoice = false;
            return;
        }

        if (displayedRequestId != request.RequestId)
        {
            displayedRequestId = request.RequestId;
            rememberDomainChoice = false;
        }

        IsOpen = true;
    }

    public void Suspend()
    {
        IsOpen = false;
    }

    public override void Draw()
    {
        if (!approvals.TryGetCurrent(out var request))
        {
            IsOpen = false;
            return;
        }

        var accent = CrystalCastUiTheme.Accent;
        DrawHeader(accent, request.IsRedirect);
        ImGui.Spacing();

        DrawDetail("SCREEN", string.IsNullOrWhiteSpace(request.ScreenName) ? "Unnamed IPC screen" : request.ScreenName);
        DrawDetail(
            "REPORTED SOURCE",
            string.IsNullOrWhiteSpace(request.ReportedOwnerId) ? "Unknown IPC caller" : request.ReportedOwnerId);
        DrawDetail("DESTINATION", request.Origin);

        ImGui.Spacing();
        ImGui.TextColored(CrystalCastUiTheme.AccentText, "FULL ADDRESS");
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 7.0f);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.08f, 0.08f, 0.10f, 0.82f));
        if (ImGui.BeginChild("CrystalCastIpcWebAddress", new Vector2(0.0f, 64.0f), true, ImGuiWindowFlags.None))
        {
            ImGui.PushTextWrapPos();
            ImGui.TextUnformatted(request.Url);
            ImGui.PopTextWrapPos();
        }
        ImGui.EndChild();
        ImGui.PopStyleColor();
        ImGui.PopStyleVar();

        ImGui.Spacing();
        ImGui.Checkbox("Remember my choice for this domain for this session", ref rememberDomainChoice);
        ImGui.TextDisabled("Applies to this exact scheme, host, and port. It is cleared when CrystalCast reloads.");

        var footerY = ImGui.GetWindowHeight() - ImGui.GetFrameHeightWithSpacing() - 18.0f;
        ImGui.SetCursorPosY(Math.Max(ImGui.GetCursorPosY(), footerY));
        var rejectLabel = rememberDomainChoice ? "Block domain" : "Reject";
        if (ImGui.Button(rejectLabel, new Vector2(130.0f, 0.0f)))
        {
            approvals.Reject(request.RequestId, blockOriginForSession: rememberDomainChoice);
            IsOpen = false;
            return;
        }

        ImGui.SameLine();
        var playLabel = rememberDomainChoice ? "Play and trust domain" : "Play once";
        var playWidth = 180.0f;
        ImGui.SetCursorPosX(ImGui.GetWindowWidth() - playWidth - ImGui.GetStyle().WindowPadding.X);
        if (ImGui.Button(playLabel, new Vector2(playWidth, 0.0f)))
        {
            approvals.Approve(request.RequestId, trustForSession: rememberDomainChoice);
            IsOpen = false;
        }
    }

    public void Dispose()
    {
    }

    private static void DrawHeader(Vector4 accent, bool isRedirect)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 10.0f);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildBorderSize, 1.0f);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, CrystalCastUiTheme.WithAlpha(accent, 0.12f));
        ImGui.PushStyleColor(ImGuiCol.Border, CrystalCastUiTheme.WithAlpha(accent, 0.42f));
        if (ImGui.BeginChild("CrystalCastIpcWebApprovalHeader", new Vector2(0.0f, 102.0f), true, ImGuiWindowFlags.None))
        {
            ImGui.TextColored(CrystalCastUiTheme.AccentText, "SECURITY CHECK");
            ImGui.TextUnformatted(isRedirect
                ? "This IPC screen wants to open another website"
                : "An IPC screen wants to open a website");
            ImGui.Spacing();
            ImGui.PushTextWrapPos();
            ImGui.TextDisabled("Generic Web pages can run scripts, track browser activity, and contact other services. CrystalCast has not loaded this destination yet.");
            ImGui.PopTextWrapPos();
        }
        ImGui.EndChild();
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar(2);
    }

    private static void DrawDetail(string label, string value)
    {
        ImGui.TextColored(CrystalCastUiTheme.AccentText, label);
        ImGui.SameLine(165.0f);
        ImGui.TextUnformatted(value);
    }
}
