using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using CrystalCast.Video;
using CrystalCast.Rendering;

namespace CrystalCast;

internal sealed record CrystalCastServices(
    IDalamudPluginInterface PluginInterface,
    ITextureProvider TextureProvider,
    IClientState ClientState,
    IObjectTable ObjectTable,
    IFramework Framework,
    IGameGui GameGui,
    IPluginLog Log,
    ScreenPlacementResolver PlacementResolver,
    IBrowserFrameSourceFactory BrowserFrameSourceFactory);
