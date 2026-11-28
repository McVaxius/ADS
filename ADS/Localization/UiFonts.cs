using System.Collections;
using AethertekUI;
using AethertekUI.Dalamud;
using ADS.Windows;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.ManagedFontAtlas;

namespace ADS.Localization;

internal sealed class UiFonts : IDisposable
{
    private readonly IUiBuilder uiBuilder;
    private readonly string assetRoot;
    private IFontAtlas? atlas;
    private readonly List<IFontHandle> handles = [];
    private UiLanguage? applied;
    private IReadOnlyList<string> required = [];
    private int generation;
    private int checkedGeneration = -1;
    internal Exception? LoadException { get; private set; }
    internal bool HindiMenuAvailable { get; private set; } = true;
    internal bool Ready => handles.Count > 0 && handles.All(h => h.Available && h.LoadException is null);

    internal UiFonts(IUiBuilder uiBuilder, string assetRoot)
    {
        this.uiBuilder = uiBuilder;
        this.assetRoot = assetRoot;
    }

    internal bool Prepare(UiLanguage language, MaterialTextRenderer shapingRenderer)
    {
        if (applied != language)
        {
            Release();
            required = RequiredText(language);
            var ranges = required.SelectMany(MaterialText.NativeGlyphText)
                .Concat(Enumerable.Range(0x20, 0x500 - 0x20).Select(value => (char)value)).ToGlyphRange();
            atlas = uiBuilder.CreateFontAtlas(FontAtlasAutoRebuildMode.OnNewFrame, true, "ADS languages");
            atlas.BuildStepChange += MaterialFontAtlas.ReleaseRasterizedInputs;
            try
            {
                foreach (var role in Enum.GetValues<UiFontRole>())
                {
                    var handle = MaterialFontAtlas.CreateFont(atlas, AdsPresentation.FontWeights[(int)role],
                        AdsPresentation.AtlasHeight(role), ranges, Ui.CultureFor(language).Name, assetRoot);
                    handles.Add(handle);
                    handle.ImFontChanged += FontChanged;
                }
                applied = language;
            }
            catch
            {
                Release();
                throw;
            }
        }
        var nativeError = handles.FirstOrDefault(handle => handle.LoadException is not null)?.LoadException;
        if (!Ready || nativeError is not null) { LoadException = nativeError ?? LoadException; return false; }
        var fontGeneration = Volatile.Read(ref generation);
        if (checkedGeneration == fontGeneration) return LoadException is null;
        LoadException = null;
        try
        {
            foreach (var size in AdsPresentation.FontSizes)
                shapingRenderer.CheckGlyphs(required, size * Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale);
            CheckGlyphs();
            HindiMenuAvailable = true;
            foreach (var size in AdsPresentation.FontSizes)
                HindiMenuAvailable &= shapingRenderer.TryCheckGlyphs([Ui.LanguageLabels[(int)UiLanguage.Hindi]],
                    size * Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale, out _);
            checkedGeneration = fontGeneration; return true;
        }
        catch (Exception error) { LoadException = error; checkedGeneration = fontGeneration; return false; }
    }

    internal static string[] RequiredText(UiLanguage language)
        => Values(language).Concat(Values(UiLanguage.English)).Concat(Ui.LanguageLabels.Where((_, index) => (UiLanguage)index != UiLanguage.Hindi))
            .Append("♥♡●").Append(Ui.CultureFor(language).NumberFormat.NumberGroupSeparator).Distinct().ToArray();

    private static IEnumerable<string> Values(UiLanguage language)
        => Ui.Resources.GetResourceSet(Ui.CultureFor(language), true, false)!
            .Cast<DictionaryEntry>().Select(entry => (string)entry.Value!);

    private void FontChanged(IFontHandle handle, ILockedImFont font) => Interlocked.Increment(ref generation);

    private unsafe void CheckGlyphs()
    {
        for (var index = 0; index < handles.Count; index++)
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

    internal void Release()
    {
        if (atlas is null) return;
        atlas.BuildStepChange -= MaterialFontAtlas.ReleaseRasterizedInputs;
        foreach (var handle in handles)
        {
            handle.ImFontChanged -= FontChanged;
            handle.Dispose();
        }
        handles.Clear();
        atlas.Dispose();
        atlas = null;
        applied = null;
        checkedGeneration = -1;
        required = [];
        LoadException = null;
    }

    public void Dispose() => Release();
}
