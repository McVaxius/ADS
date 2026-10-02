using System.Collections;
using Dalamud;
using Dalamud.Interface;
using Dalamud.Interface.ManagedFontAtlas;

namespace ADS.Localization;

internal sealed class UiFonts : IDisposable
{
    private readonly IFontAtlas atlas;
    private readonly IUiBuilder uiBuilder;
    private IFontHandle? japanese;
    private IFontHandle? simplifiedChinese;
    private IFontHandle? traditionalChinese;
    private IFontHandle? korean;

    internal UiFonts(IUiBuilder uiBuilder)
    {
        this.uiBuilder = uiBuilder;
        atlas = uiBuilder.CreateFontAtlas(FontAtlasAutoRebuildMode.Async, true, "ADS languages");
        try
        {
            var glyphs = GlyphRanges();
            using (atlas.SuppressAutoRebuild())
            {
                japanese = Create(0, glyphs);
                simplifiedChinese = Create(2, glyphs);
                traditionalChinese = Create(1, glyphs);
                korean = Create(3, glyphs);
            }
        }
        catch { Dispose(); throw; }
    }

    private IFontHandle Create(int face, ushort[] glyphs)
        => atlas.NewDelegateFontHandle(step => step.OnPreBuild(toolkit =>
        {
            var defaultFont = uiBuilder.DefaultFontSpec;
            var config = new SafeFontConfig { FontNo = face, SizePx = defaultFont.SizePx, GlyphRanges = glyphs };
            var font = toolkit.AddDalamudAssetFont(DalamudAsset.NotoSansCjkRegular, config);
            defaultFont.AddToBuildToolkit(toolkit, font);
            config.MergeFont = font;
            toolkit.AddGameSymbol(config);
            toolkit.Font = font;
        }));

    // Resource glyphs cover all picker choices; Latin Extended supports accents
    // and default-font merging preserves dynamic names from the game client.
    internal static ushort[] GlyphRanges()
        => Enum.GetValues<UiLanguage>().SelectMany(language =>
                Ui.Resources.GetResourceSet(Ui.CultureFor(language), true, false)!
                    .Cast<DictionaryEntry>().SelectMany(entry => (string)entry.Value!))
            .Concat(string.Concat(Ui.LanguageLabels))
            .Concat(Enumerable.Range(0x20, 0x250 - 0x20).Select(value => (char)value))
            .ToGlyphRange();

    internal IDisposable? Push(UiLanguage language) => Handle(language)?.Push();
    internal IDisposable? PushPicker(UiLanguage language) => Handle(language)?.Push();

    private IFontHandle? Handle(UiLanguage language) => language switch
    {
        UiLanguage.SimplifiedChinese => simplifiedChinese,
        UiLanguage.TraditionalChinese => traditionalChinese,
        UiLanguage.Korean => korean,
        _ => japanese,
    };

    public void Dispose()
    {
        japanese?.Dispose();
        simplifiedChinese?.Dispose();
        traditionalChinese?.Dispose();
        korean?.Dispose();
        atlas.Dispose();
    }
}
