using System.Collections;
using AethertekUI;
using ADS.Windows;
using Dalamud;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.ManagedFontAtlas;

namespace ADS.Localization;

internal sealed class UiFonts : IDisposable
{
    private readonly IFontAtlas atlas;
    private IFontHandle[] handles = [];
    private UiLanguage? applied;
    private IReadOnlyList<string> required = [];
    private int generation;
    private int checkedGeneration = -1;
    internal Exception? LoadException { get; private set; }
    internal bool Ready => handles.Length > 0 && handles.All(h => h.Available && h.LoadException is null);

    internal UiFonts(IUiBuilder uiBuilder)
    {
        atlas = uiBuilder.CreateFontAtlas(FontAtlasAutoRebuildMode.Async, true, "ADS languages");
    }

    internal bool Prepare(UiLanguage language, MaterialTextRenderer shapingRenderer)
    {
        if (applied != language)
        {
            foreach (var handle in handles) { handle.ImFontChanged -= FontChanged; handle.Dispose(); }
            applied = language;
            LoadException = null;
            checkedGeneration = -1;
            required = RequiredText(language);
            var ranges = required.SelectMany(MaterialText.NativeGlyphText)
                .Concat(Enumerable.Range(0x20, 0x500 - 0x20).Select(value => (char)value)).ToGlyphRange();
            using (atlas.SuppressAutoRebuild())
                handles = AdsPresentation.FontSizes.Select((_, index) => Create((UiFontRole)index, language, ranges)).ToArray();
            foreach (var handle in handles) handle.ImFontChanged += FontChanged;
        }
        LoadException ??= handles.FirstOrDefault(handle => handle.LoadException is not null)?.LoadException;
        if (!Ready || LoadException is not null) return false;
        if (checkedGeneration == generation) return true;
        try
        {
            foreach (var size in AdsPresentation.FontSizes)
                shapingRenderer.CheckGlyphs(required, size * Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale);
            CheckGlyphs(); checkedGeneration = generation; return true;
        }
        catch (Exception error) { LoadException = error; return false; }
    }

    internal static string[] RequiredText(UiLanguage language)
        => Values(language).Concat(Values(UiLanguage.English)).Concat(Ui.LanguageLabels)
            .Append("♥♡●").Append(Ui.CultureFor(language).NumberFormat.NumberGroupSeparator).Distinct().ToArray();

    private static IEnumerable<string> Values(UiLanguage language)
        => Ui.Resources.GetResourceSet(Ui.CultureFor(language), true, false)!
            .Cast<DictionaryEntry>().Select(entry => (string)entry.Value!);

    private IFontHandle Create(UiFontRole role, UiLanguage language, ushort[] ranges)
        => atlas.NewDelegateFontHandle(step => step.OnPreBuild(toolkit =>
        {
            var size = AdsPresentation.AtlasHeight(role);
            var fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            toolkit.Font = toolkit.AddFontFromFile(Path.Combine(fonts, AdsPresentation.FontFiles[(int)role]),
                new SafeFontConfig { SizePx = size, GlyphRanges = ranges });
            toolkit.AddFontFromFile(Path.Combine(fonts, "seguisym.ttf"),
                new SafeFontConfig { SizePx = size, GlyphRanges = ranges, MergeFont = toolkit.Font });
            foreach (var locale in new[] { UiLanguage.Japanese, UiLanguage.Korean, UiLanguage.SimplifiedChinese, UiLanguage.TraditionalChinese }
                .OrderBy(locale => locale == language ? 0 : 1))
                toolkit.AddDalamudAssetFont(DalamudAsset.NotoSansCjkRegular, new SafeFontConfig
                {
                    SizePx = size, GlyphRanges = ranges, MergeFont = toolkit.Font,
                    FontNo = locale switch { UiLanguage.Korean => 1, UiLanguage.SimplifiedChinese => 2, UiLanguage.TraditionalChinese => 3, _ => 0 },
                });
            toolkit.AttachExtraGlyphsForDalamudLanguage(new SafeFontConfig { SizePx = size, MergeFont = toolkit.Font });
            toolkit.AddGameSymbol(new SafeFontConfig { SizePx = size, MergeFont = toolkit.Font });
        }));

    private void FontChanged(IFontHandle handle, ILockedImFont font) => Interlocked.Increment(ref generation);

    private unsafe void CheckGlyphs()
    {
        for (var index = 0; index < handles.Length; index++)
        {
            using var font = handles[index].Lock();
            foreach (var character in required.SelectMany(MaterialText.NativeGlyphText).Where(c => !char.IsControl(c)).Distinct())
                if (ImGui.FindGlyphNoFallback(font.ImFont, character).Handle == null)
                    throw new InvalidOperationException($"Required UI glyph missing: U+{(int)character:X4} in {(UiFontRole)index}");
        }
    }

    internal IDisposable Push(UiLanguage language) => Push(UiFontRole.Body);
    internal IDisposable PushPicker(UiLanguage language) => Push(UiFontRole.Body);
    internal IDisposable Push(UiFontRole role) => handles[(int)role].Push();

    public void Dispose()
    {
        foreach (var handle in handles) { handle.ImFontChanged -= FontChanged; handle.Dispose(); }
        atlas.Dispose();
    }
}
