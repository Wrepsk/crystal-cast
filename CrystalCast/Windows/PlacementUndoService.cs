using Dalamud.Bindings.ImGui;

namespace CrystalCast.Windows;

internal sealed class PlacementUndoService
{
    private const int HistoryLimit = 32;
    private const long CoalesceMilliseconds = 350;

    private readonly Dictionary<string, PlacementUndoHistory> histories = new(StringComparer.Ordinal);
    private string appliedKey = string.Empty;

    public void BeginFrame()
    {
        appliedKey = string.Empty;
    }

    public void Remove(string undoKey)
    {
        histories.Remove(undoKey);
        if (appliedKey == undoKey)
            appliedKey = string.Empty;
    }

    public bool DrawUndoButton(string undoKey, ScreenPlacementSettings placement)
    {
        var history = GetHistory(undoKey);
        var canUndo = history.Snapshots.Count > 0;
        if (!canUndo)
            ImGui.BeginDisabled();

        var changed = false;
        if (ImGui.Button("Undo placement") && canUndo)
        {
            var snapshot = history.Snapshots[^1];
            placement.CopyFrom(snapshot);
            history.Snapshots.RemoveAt(history.Snapshots.Count - 1);
            history.LastChangeUnixMs = 0;
            appliedKey = undoKey;
            changed = true;
        }

        if (!canUndo)
            ImGui.EndDisabled();

        return changed;
    }

    public void Capture(string undoKey, ScreenPlacementSettings before, ScreenPlacementSettings after)
    {
        if (appliedKey == undoKey || !PlacementDiffers(before, after))
            return;

        var history = GetHistory(undoKey);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var shouldPush = history.Snapshots.Count == 0
            || history.LastChangeUnixMs == 0
            || now - history.LastChangeUnixMs > CoalesceMilliseconds;

        if (shouldPush)
            Push(history, before);

        history.LastChangeUnixMs = now;
    }

    private PlacementUndoHistory GetHistory(string undoKey)
    {
        if (histories.TryGetValue(undoKey, out var history))
            return history;

        history = new PlacementUndoHistory();
        histories[undoKey] = history;
        return history;
    }

    private static void Push(PlacementUndoHistory history, ScreenPlacementSettings placement)
    {
        if (history.Snapshots.Count > 0 && !PlacementDiffers(history.Snapshots[^1], placement))
            return;

        history.Snapshots.Add(placement.Clone());
        if (history.Snapshots.Count > HistoryLimit)
            history.Snapshots.RemoveRange(0, history.Snapshots.Count - HistoryLimit);
    }

    private static bool PlacementDiffers(ScreenPlacementSettings left, ScreenPlacementSettings right)
    {
        const float epsilon = 0.0001f;
        return left.Mode != right.Mode
            || MathF.Abs(left.PositionX - right.PositionX) > epsilon
            || MathF.Abs(left.PositionY - right.PositionY) > epsilon
            || MathF.Abs(left.PositionZ - right.PositionZ) > epsilon
            || MathF.Abs(left.YawRadians - right.YawRadians) > epsilon
            || MathF.Abs(left.PitchRadians - right.PitchRadians) > epsilon
            || MathF.Abs(left.RollRadians - right.RollRadians) > epsilon
            || MathF.Abs(left.WidthMeters - right.WidthMeters) > epsilon
            || MathF.Abs(left.HeightMeters - right.HeightMeters) > epsilon
            || MathF.Abs(left.ScreenCurveAmountMeters - right.ScreenCurveAmountMeters) > epsilon
            || MathF.Abs(left.OccludedAlpha - right.OccludedAlpha) > epsilon
            || MathF.Abs(left.OcclusionTolerance - right.OcclusionTolerance) > epsilon
            || left.EnableDistanceFade != right.EnableDistanceFade
            || MathF.Abs(left.FadeStartMeters - right.FadeStartMeters) > epsilon
            || MathF.Abs(left.FadeStopMeters - right.FadeStopMeters) > epsilon;
    }

    private sealed class PlacementUndoHistory
    {
        public List<ScreenPlacementSettings> Snapshots { get; } = [];
        public long LastChangeUnixMs { get; set; }
    }
}
