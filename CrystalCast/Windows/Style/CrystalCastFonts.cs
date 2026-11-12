using Dalamud;
using Dalamud.Interface;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Plugin;

namespace CrystalCast.Windows.Style;

/// <summary>
/// The three type voices from the redesign: Marcellus for the one title per window, Hanken Grotesk for
/// everything functional, and Geist Mono for live numbers and diagnostic strings. Font files are loaded
/// from <c>Assets/Fonts</c> next to the plugin; when a file is missing the Dalamud default font (or
/// Inconsolata for mono) is used at the same size so the layout still holds.
/// </summary>
internal sealed class CrystalCastFonts : IDisposable
{
    private const string SerifFile = "Marcellus-Regular.ttf";
    private const string BodyFile = "HankenGrotesk-Regular.ttf";
    private const string MediumFile = "HankenGrotesk-Medium.ttf";
    private const string SemiBoldFile = "HankenGrotesk-SemiBold.ttf";
    private const string MonoFile = "GeistMono-Regular.ttf";

    // Latin, Latin-1, Latin Extended-A and general punctuation (dashes, quotes, ellipsis).
    private static readonly ushort[] GlyphRanges = [0x0020, 0x017F, 0x2000, 0x206F, 0];

    private readonly string fontDirectory;
    private readonly Stack<IDisposable> bodyScopes = new();

    public CrystalCastFonts(IDalamudPluginInterface pluginInterface)
    {
        fontDirectory = Path.Combine(pluginInterface.AssemblyLocation.DirectoryName ?? string.Empty, "Assets", "Fonts");
        var atlas = pluginInterface.UiBuilder.FontAtlas;

        Body = Create(atlas, BodyFile, 16.0f, mono: false);
        Small = Create(atlas, BodyFile, 14.5f, mono: false);
        Medium = Create(atlas, MediumFile, 16.0f, mono: false);
        SmallMedium = Create(atlas, MediumFile, 14.5f, mono: false);
        SemiBold = Create(atlas, SemiBoldFile, 16.0f, mono: false);
        SectionTitle = Create(atlas, SemiBoldFile, 19.0f, mono: false);
        PlayerTitle = Create(atlas, SerifFile, 32.0f, mono: false, mergeJapanese: true);
        PageTitle = Create(atlas, SerifFile, 30.0f, mono: false);
        Mono = Create(atlas, MonoFile, 14.5f, mono: true);
        MonoDetail = Create(atlas, MonoFile, 13.5f, mono: true);
        Icon = atlas.NewDelegateFontHandle(e => e.OnPreBuild(tk =>
            tk.AddFontAwesomeIconFont(new SafeFontConfig { SizePx = 15.0f, PixelSnapH = true, OversampleH = 1, OversampleV = 1 })));
    }

    /// <summary>Hanken Grotesk 16, 400. Field text, list items.</summary>
    public IFontHandle Body { get; }

    /// <summary>Hanken Grotesk 14.5, 400. Labels, meta, status line.</summary>
    public IFontHandle Small { get; }

    /// <summary>Hanken Grotesk 16, 500. Tabs, nav, item names.</summary>
    public IFontHandle Medium { get; }

    /// <summary>Hanken Grotesk 14.5, 500. Status words.</summary>
    public IFontHandle SmallMedium { get; }

    /// <summary>Hanken Grotesk 16, 600. Headings and buttons.</summary>
    public IFontHandle SemiBold { get; }

    /// <summary>Hanken Grotesk 19, 600. Large card headings in helper windows.</summary>
    public IFontHandle SectionTitle { get; }

    /// <summary>Marcellus 32. Video title over the header.</summary>
    public IFontHandle PlayerTitle { get; }

    /// <summary>Marcellus 30. Settings page titles.</summary>
    public IFontHandle PageTitle { get; }

    /// <summary>Geist Mono 14.5. Live numbers.</summary>
    public IFontHandle Mono { get; }

    /// <summary>Geist Mono 13.5. Error details and diagnostic strings.</summary>
    public IFontHandle MonoDetail { get; }

    /// <summary>FontAwesome at 15 px, drawn at its native size so icons stay sharp.</summary>
    public IFontHandle Icon { get; }

    public bool UsingBundledFonts => File.Exists(Path.Combine(fontDirectory, BodyFile));

    public void PushBody()
    {
        bodyScopes.Push(Body.Push());
    }

    public void PopBody()
    {
        if (bodyScopes.TryPop(out var scope))
            scope.Dispose();
    }

    public void Dispose()
    {
        while (bodyScopes.TryPop(out var scope))
            scope.Dispose();

        Body.Dispose();
        Small.Dispose();
        Medium.Dispose();
        SmallMedium.Dispose();
        SemiBold.Dispose();
        SectionTitle.Dispose();
        PlayerTitle.Dispose();
        PageTitle.Dispose();
        Mono.Dispose();
        MonoDetail.Dispose();
        Icon.Dispose();
    }

    private IFontHandle Create(IFontAtlas atlas, string fileName, float sizePx, bool mono, bool mergeJapanese = false)
    {
        var path = Path.Combine(fontDirectory, fileName);
        return atlas.NewDelegateFontHandle(e => e.OnPreBuild(tk =>
        {
            // Pixel-snapped glyphs keep small UI text crisp instead of resampling at sub-pixel offsets.
            var config = new SafeFontConfig
            {
                SizePx = sizePx,
                GlyphRanges = GlyphRanges,
                PixelSnapH = true,
                OversampleH = 1,
                OversampleV = 1,
            };

            if (!File.Exists(path))
            {
                if (mono)
                    tk.AddDalamudAssetFont(DalamudAsset.InconsolataRegular, config);
                else
                    tk.AddDalamudDefaultFont(sizePx, GlyphRanges);
                return;
            }

            var font = tk.AddFontFromFile(path, config);
            if (mergeJapanese)
            {
                // Video titles are often Japanese; borrow those glyphs from Dalamud's Noto Sans CJK.
                tk.AddDalamudAssetFont(DalamudAsset.NotoSansCjkMedium, config with
                {
                    MergeFont = font,
                    GlyphRanges = GlyphRangesJapanese.GlyphRanges,
                });
            }
        }));
    }
}
