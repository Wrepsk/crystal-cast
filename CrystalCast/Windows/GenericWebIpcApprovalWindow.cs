using System.Numerics;
using CrystalCast.Sync;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using static CrystalCast.Windows.CrystalCastUiTheme;
using Ui = CrystalCast.Windows.CrystalCastUiWidgets;

namespace CrystalCast.Windows;

internal sealed class GenericWebIpcApprovalWindow : CrystalCastWindow, IDisposable
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
        Size = new Vector2(620.0f, 470.0f);
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

        Ui.Hero(
            "Security check",
            request.IsRedirect ? "This IPC screen wants to open another website" : "An IPC screen wants to open a website",
            "Generic Web pages can run scripts, track browser activity, and contact other services. CrystalCast has not loaded this destination yet.");

        DrawDetail("Screen", string.IsNullOrWhiteSpace(request.ScreenName) ? "Unnamed IPC screen" : request.ScreenName);
        DrawDetail("Reported source", string.IsNullOrWhiteSpace(request.ReportedOwnerId) ? "Unknown IPC caller" : request.ReportedOwnerId);
        DrawDetail("Destination", request.Origin);

        Ui.RowLabel("Full address", DetailLabelWidth, DetailLabelGap);
        var pos = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        float textHeight;
        using (Ui.Font(f => f.MonoDetail))
            textHeight = ImGui.CalcTextSize(request.Url, false, width - S(20.0f)).Y;
        var boxSize = new Vector2(width, Math.Max(Ui.FrameHeight, textHeight + S(14.0f)));
        var dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(pos, pos + boxSize, U32(Inset), S(4.0f));
        dl.AddRect(pos, pos + boxSize, U32(Hairline), S(4.0f), ImDrawFlags.None, 1.0f);
        ImGui.SetCursorScreenPos(pos + S(10.0f, 7.0f));
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width - S(20.0f));
        Ui.Text(request.Url, CrystalCastUiTheme.Text, f => f.MonoDetail);
        ImGui.PopTextWrapPos();
        ImGui.SetCursorScreenPos(pos);
        ImGui.Dummy(boxSize);

        Ui.VerticalSpace(16.0f);
        Ui.SwitchLabeled("##RememberDomain", "Remember my choice for this domain for this session", ref rememberDomainChoice);
        Ui.Meta("Applies to this exact scheme, host, and port. It is cleared when CrystalCast reloads.");

        var footerY = ImGui.GetWindowHeight() - ImGui.GetStyle().WindowPadding.Y - Ui.FrameHeight;
        ImGui.SetCursorPosY(Math.Max(ImGui.GetCursorPosY(), footerY));
        var rejectLabel = rememberDomainChoice ? "Block domain" : "Reject";
        if (Ui.Button(rejectLabel))
        {
            approvals.Reject(request.RequestId, blockOriginForSession: rememberDomainChoice);
            IsOpen = false;
            return;
        }

        var playLabel = rememberDomainChoice ? "Play and trust domain" : "Play once";
        ImGui.SameLine(Math.Max(0.0f, ImGui.GetContentRegionMax().X - Ui.ButtonWidth(playLabel)));
        if (Ui.Button(playLabel, ButtonKind.Primary))
        {
            approvals.Approve(request.RequestId, trustForSession: rememberDomainChoice);
            IsOpen = false;
        }
    }

    public void Dispose()
    {
    }
}
