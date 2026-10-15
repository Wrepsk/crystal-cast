using Dalamud.Plugin.Services;

namespace CrystalRenderer.Integration;

internal sealed class RendererServices
{
    // Dalamud injects properties before invoking the constructor. Constructor injection
    // avoids field initializers overwriting services that were injected into an instance.
    public RendererServices(IGameGui gameGui, ICondition condition, IPluginLog log,
        IFramework framework, IGameInteropProvider hookProvider)
    {
        GameGui = gameGui ?? throw new ArgumentNullException(nameof(gameGui));
        Condition = condition ?? throw new ArgumentNullException(nameof(condition));
        Log = log ?? throw new ArgumentNullException(nameof(log));
        Framework = framework ?? throw new ArgumentNullException(nameof(framework));
        HookProvider = hookProvider ?? throw new ArgumentNullException(nameof(hookProvider));
    }

    public IGameGui GameGui { get; }
    public ICondition Condition { get; }
    public IPluginLog Log { get; }
    public IFramework Framework { get; }
    public IGameInteropProvider HookProvider { get; }
}
