using ADS.Models;
using ADS.Windows;
using ADS.Localization;
using System.Numerics;
using System.Reflection;
using Dalamud.Bindings.ImGui;
using AethertekUI;

namespace ADS.Tests;

[CollectionDefinition("ADS guide native UI", DisableParallelization = true)]
public sealed class GuideNativeUiCollection;

[Collection("ADS guide native UI")]
public sealed class WizardCatalogTests
{
    [Fact]
    public void ColourPreferencesPreserveLegacyThemeLanguageOrdinalsAndUnknownSettings()
    {
        var configuration = Newtonsoft.Json.JsonConvert.DeserializeObject<Configuration>(
            """{"Version":25,"UiAccentRgb":1193046,"UiLanguage":5,"UnrelatedFixture":17}""")!;
        Assert.Equal(MaterialColorMode.Auto, configuration.UiColors.Mode);
        Assert.True(configuration.UiLcarsSoundsMuted);
        Assert.Equal(UiLanguage.TraditionalChinese, configuration.UiLanguage);
        Assert.Null(new Configuration().UiLanguage);
        Assert.Equal(Enumerable.Range(0, 16), Enum.GetValues<UiLanguage>().Select(language => (int)language));
        Assert.Equal(5, (int)UiLanguage.TraditionalChinese);
        Assert.Equal(15, (int)UiLanguage.Hindi);
        var automatic = AdsPresentation.Theme(AdsPresentation.ReferenceAccent).Colors;
        Assert.Equal(AdsPresentation.Rgb(0x0C1A26), automatic.Background);
        Assert.Equal(AdsPresentation.Rgb(0xF2F4F9), automatic.OnSurface);
        Assert.Equal(AdsPresentation.Rgb(0x0067FF), automatic.Primary);
        Assert.Equal(AdsPresentation.Rgb(0x203344), automatic.SecondaryContainer);
        configuration.UiColors = new() { Mode = MaterialColorMode.Picker,
            BackgroundRgb = 0x010203, ControlsRgb = 0x040506, TextRgb = 0x070809 };
        var storage = typeof(Dalamud.Plugin.IDalamudPluginInterface).Assembly.GetType("Dalamud.Configuration.PluginConfigurations", true)!;
        const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        var saved = (string)storage.GetMethod("SerializeConfig", flags)!.Invoke(null, [configuration])!;
        var restored = (Configuration)storage.GetMethod("DeserializeConfig", flags)!.Invoke(null, [saved])!;
        Assert.Equal(configuration.UiColors, restored.UiColors);
        Assert.Equal(25, restored.Version);
        Assert.Equal(0x123456u, restored.UiAccentRgb);
        Assert.Equal(UiLanguage.TraditionalChinese, restored.UiLanguage);
        Assert.Equal(17, (int)restored.AdditionalSettings!["UnrelatedFixture"]);
        var manual = AdsPresentation.Theme(restored.UiAccentRgb, restored.UiColors).Colors;
        Assert.Equal(AdsPresentation.Rgb(0x010203), manual.Background);
        Assert.Equal(AdsPresentation.Rgb(0x040506), manual.Primary);
        Assert.Equal(AdsPresentation.Rgb(0x070809), manual.OnSurface);
        Assert.Equal(automatic.Error, manual.Error);
        restored.UiColors = restored.UiColors with { Mode = MaterialColorMode.Lcars };
        restored.UiLcarsSoundsMuted = false;
        saved = (string)storage.GetMethod("SerializeConfig", flags)!.Invoke(null, [restored])!;
        var lcars = (Configuration)storage.GetMethod("DeserializeConfig", flags)!.Invoke(null, [saved])!;
        Assert.Equal(restored.UiColors, lcars.UiColors);
        Assert.False(lcars.UiLcarsSoundsMuted);
        Assert.Equal(17, (int)lcars.AdditionalSettings!["UnrelatedFixture"]);
        lcars.UiColors = lcars.UiColors with { Mode = MaterialColorMode.Auto };
        Assert.Equal(restored.UiColors.BackgroundRgb, lcars.UiColors.BackgroundRgb);
        Assert.Equal(restored.UiColors.ControlsRgb, lcars.UiColors.ControlsRgb);
        Assert.Equal(restored.UiColors.TextRgb, lcars.UiColors.TextRgb);
    }

    [Fact]
    public void AllSixteenCompiledCatalogsContainAppearanceAndNoticeLabels()
    {
        string[] keys = ["Auto", "Picker", "Default", "Background, controls and text follow your theme.", "Theme colour",
            "Choose each colour independently.", "Background", "Controls", "Text", "Aa  Preview text", "Control",
            "Switch to Picker to set each colour yourself.", "Switch back to Auto to use the automatic theme.",
            "LCARS", "LCARS panels, bars and controls", "LCARS UI sounds", "UI sounds", "Preview sound",
            "Custom UI tones. The titlebar speaker toggles the same setting.", "Select LCARS to use UI sounds.",
            "Turn on UI sounds to preview.", "Audio output is unavailable.", "UI sounds muted", "UI sounds on",
            "ADS: Use the default Strafe Left/Right keybinds in treasure dungeons to avoid getting stuck. This is a stopgap until better navigation is available."];
        var previous = Ui.Language;
        try
        {
            foreach (var language in Enum.GetValues<UiLanguage>())
            {
                Ui.SetLanguage(language);
                var resources = Ui.Resources.GetResourceSet(Ui.CultureFor(language), true, false)!;
                foreach (var key in keys)
                {
                    var translated = resources.GetString(key == "Default" ? "Caption.DefaultTitle" : key);
                    Assert.False(string.IsNullOrWhiteSpace(translated), language + ": " + key);
                    Assert.Equal(translated, Ui.T(key));
                }
            }
        }
        finally { Ui.SetLanguage(previous); }
    }

    [Theory]
    [InlineData(false, 1f)]
    [InlineData(true, 1.5f)]
    public unsafe void NativeColourCallbackCommitsIndependentFieldsAndRetainsThemWhenReturningToAuto(bool compact, float scale)
    {
        var interfaceProperty = typeof(Plugin).GetProperty("PluginInterface", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
        var previousInterface = interfaceProperty.GetValue(null);
        var previousContext = ImGui.GetCurrentContext();
        var language = Ui.Language;
        var context = ImGui.CreateContext();
        try
        {
            var pluginInterface = DispatchProxy.Create<Dalamud.Plugin.IDalamudPluginInterface, AdsEnableCompatibilityTests.IpcProxy>();
            var proxy = (AdsEnableCompatibilityTests.IpcProxy)(object)pluginInterface;
            interfaceProperty.SetValue(null, pluginInterface);
            Ui.SetLanguage(UiLanguage.English);
            var configuration = new Configuration { UiCompact = compact, UiLanguage = UiLanguage.TraditionalChinese };
            var plugin = (Plugin)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Plugin));
            typeof(Plugin).GetField("<Configuration>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(plugin, configuration);
            using var appearance = new UiAppearance(plugin);
            var sounds = typeof(UiAppearance).GetMethod("DrawLcarsSounds", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var titleBars = typeof(UiAppearance).GetMethod("PrepareAppearanceTitleBars", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var owner = new MainWindow(plugin);
            var windows = new Dalamud.Interface.Windowing.WindowSystem("ADS LCARS callback");
            windows.AddWindow(owner);
            var feedback = new List<MaterialUiSoundCue>();
            typeof(UiAppearance).GetField("accentDraft", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(appearance, MaterialColorSettings.Rgb(configuration.UiAccentRgb));
            var draw = typeof(UiAppearance).GetMethod("DrawTitleBarColour", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var io = ImGui.GetIO(); io.IniFilename = null; io.LogFilename = null;
            io.DisplaySize = new(1200 * scale, 900 * scale); io.DeltaTime = 1f / 60; io.FontGlobalScale = scale;
            io.Fonts.AddFontDefault();
            byte* pixels = null; int width = 0, height = 0;
            io.Fonts.GetTexDataAsRGBA32(0, &pixels, &width, &height);
            io.Fonts.SetTexID(0, new ImTextureID((nint)1));
            ImDrawListPtr list = default;
            void Frame()
            {
                ImGui.NewFrame();
                var theme = AdsPresentation.Theme(configuration.UiAccentRgb, configuration.UiColors);
                theme.Density = compact ? MaterialDensity.Compact : MaterialDensity.Standard;
                theme.Feedback = feedback.Add;
                using var palette = MaterialTheme.Push(theme, scale, MaterialStyleMode.ColorsOnly);
                ImGui.SetNextWindowPos(new(20, 20)); ImGui.SetNextWindowSize(new(600 * scale, 800 * scale));
                ImGui.Begin("ADS Colour callback", ImGuiWindowFlags.NoSavedSettings);
                try
                {
                    titleBars.Invoke(appearance, [windows]);
                    draw.Invoke(appearance, null);
                    sounds.Invoke(appearance, null);
                    list = ImGui.GetWindowDrawList();
                }
                finally { ImGui.End(); }
                ImGui.Render();
            }
            void Click(string caption)
            {
                var point = Center(caption);
                io.AddMousePosEvent(point.X, point.Y); Frame();
                io.AddMouseButtonEvent(0, true); Frame();
                io.AddMouseButtonEvent(0, false); Frame();
            }
            Vector2 Center(string caption)
            {
                var lastMatch = caption == "UI sounds";
                Vector2? point = null;
                caption = caption.Replace(" ", string.Empty);
                for (var index = 0; index + caption.Length * 4 <= list.VtxBuffer.Size; index++)
                {
                    var matched = true;
                    for (var character = 0; character < caption.Length; character++)
                    {
                        var glyph = ImGui.FindGlyphNoFallback(ImGui.GetFont(), caption[character]);
                        if (list.VtxBuffer[index + character * 4].Uv != new Vector2(glyph.Handle->U0, glyph.Handle->V0)
                            || list.VtxBuffer[index + character * 4 + 2].Uv != new Vector2(glyph.Handle->U1, glyph.Handle->V1))
                        { matched = false; break; }
                    }
                    if (matched)
                    {
                        point = (list.VtxBuffer[index].Pos + list.VtxBuffer[index + caption.Length * 4 - 2].Pos) / 2;
                        if (!lastMatch) return point.Value;
                    }
                }
                return point ?? throw new InvalidOperationException("Actual colour callback did not show " + caption);
            }
            Frame(); Frame(); Click("Picker");
            Assert.Equal(1, proxy.Saves);
            Assert.Equal(MaterialColorMode.Picker, configuration.UiColors.Mode);
            var replacements = new[] { "#193149", "#3579BD", "#EDF5FD" };
            for (var field = 0; field < 3; field++)
            {
                var before = configuration.UiColors;
                var current = field == 0 ? before.BackgroundRgb : field == 1 ? before.ControlsRgb : before.TextRgb;
                Click("#" + current.ToString("X6"));
                io.AddKeyEvent(ImGuiKey.ModCtrl, true); io.AddKeyEvent(ImGuiKey.A, true); Frame();
                io.AddKeyEvent(ImGuiKey.A, false); io.AddKeyEvent(ImGuiKey.ModCtrl, false); Frame();
                foreach (var character in replacements[field]) io.AddInputCharacter(character);
                Frame();
                Assert.Equal(1 + field, proxy.Saves);
                Assert.Equal(before, configuration.UiColors);
                io.AddKeyEvent(ImGuiKey.Enter, true); Frame();
                io.AddKeyEvent(ImGuiKey.Enter, false); Frame();
                Assert.Equal(2 + field, proxy.Saves);
                var expected = Convert.ToUInt32(replacements[field][1..], 16);
                Assert.Equal(field == 0 ? expected : before.BackgroundRgb, configuration.UiColors.BackgroundRgb);
                Assert.Equal(field == 1 ? expected : before.ControlsRgb, configuration.UiColors.ControlsRgb);
                Assert.Equal(field == 2 ? expected : before.TextRgb, configuration.UiColors.TextRgb);
                Assert.Equal(UiLanguage.TraditionalChinese, configuration.UiLanguage);
            }
            var manual = configuration.UiColors;
            var nativeTitleButtons = owner.TitleBarButtons.Take(6).ToArray();
            Click("LCARS"); Frame();
            Assert.Equal(5, proxy.Saves);
            Assert.Equal(manual with { Mode = MaterialColorMode.Lcars }, configuration.UiColors);
            Assert.True(configuration.UiLcarsSoundsMuted);
            Assert.Equal(9, owner.TitleBarButtons.Count);
            Assert.Equal(nativeTitleButtons, owner.TitleBarButtons.Take(6));
            Assert.Contains(owner.TitleBarButtons, button => button.Icon == Dalamud.Interface.FontAwesomeIcon.Globe);
            var speaker = owner.TitleBarButtons.Single(button => button.Icon == Dalamud.Interface.FontAwesomeIcon.VolumeMute);
            speaker.Click(ImGuiMouseButton.Right);
            Assert.True(configuration.UiLcarsSoundsMuted);
            Assert.Equal(5, proxy.Saves);
            speaker.Click(ImGuiMouseButton.Left); Frame();
            Assert.False(configuration.UiLcarsSoundsMuted);
            Assert.Equal(6, proxy.Saves);
            feedback.Clear();
            Click("Preview sound");
            Assert.Equal(new[] { MaterialUiSoundCue.Preview }, feedback);
            Assert.Equal(6, proxy.Saves);
            Click("UI sounds");
            Assert.True(configuration.UiLcarsSoundsMuted);
            Assert.Equal(7, proxy.Saves);
            Click("Auto"); Frame();
            Assert.Equal(8, proxy.Saves);
            Assert.DoesNotContain(speaker, owner.TitleBarButtons);
            Assert.Equal(8, owner.TitleBarButtons.Count);
            Assert.Equal(manual with { Mode = MaterialColorMode.Auto }, configuration.UiColors);
            Assert.Equal(AdsPresentation.ReferenceAccent, configuration.UiAccentRgb);
        }
        finally
        {
            interfaceProperty.SetValue(null, previousInterface);
            Ui.SetLanguage(language);
            ImGui.DestroyContext(context); ImGui.SetCurrentContext(previousContext);
        }
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    public unsafe void NativeToolGuideOffersKeepParentInteractiveAndPreserveEveryClosePath(int toolIndex, bool compact)
    {
        var previous = ImGui.GetCurrentContext();
        var language = Ui.Language;
        var context = ImGui.CreateContext();
        try
        {
            Ui.SetLanguage(UiLanguage.English);
            var io = ImGui.GetIO();
            io.IniFilename = null; io.LogFilename = null;
            io.DisplaySize = new(1280, 900); io.DeltaTime = 1f / 60;
            io.Fonts.AddFontDefault();
            byte* pixels = null; int width = 0, height = 0;
            io.Fonts.GetTexDataAsRGBA32(0, &pixels, &width, &height);
            io.Fonts.SetTexID(0, new ImTextureID((nint)1));
            var theme = AdsPresentation.Theme(AdsPresentation.ReferenceAccent);
            theme.Density = compact ? MaterialDensity.Compact : MaterialDensity.Standard;
            using var themeScope = MaterialTheme.Push(theme, 1, MaterialStyleMode.ColorsOnly);
            var draw = typeof(PositionedWindow).GetMethod("DrawToolGuideOffer", BindingFlags.Static | BindingFlags.NonPublic)!;
            var close = typeof(PositionedWindow).GetMethod("CloseToolGuideOffer", BindingFlags.Static | BindingFlags.NonPublic)!;
            var tool = (ToolGuide)toolIndex;
            var title = tool == ToolGuide.Rules ? "Rules guide###ADSRuleGuideOffer" : "Object Explorer guide###ADSExplorerGuideOffer";
            const string owner = "ADS native offer owner";
            foreach (var action in new[] { "Dismiss", "Open", "Close", "Escape", "OwnerClose", "ToolbarGuide" })
            {
                var configuration = new Configuration();
                var saves = 0; var launches = 0; var parentPresses = 0;
                Vector2 parentButton = default;
                object[] args = [configuration, (Action)(() => saves++), tool, true, false, (Action)(() =>
                {
                    launches++;
                    if (WizardCatalog.MarkToolGuideVisited(configuration, tool)) saves++;
                })];
                void Frame()
                {
                    ImGui.NewFrame();
                    ImGui.SetNextWindowPos(new(20, 20)); ImGui.SetNextWindowSize(new(380, 200));
                    ImGui.Begin(owner, ImGuiWindowFlags.NoSavedSettings);
                    try
                    {
                        if (ImGui.Button("Parent action")) parentPresses++;
                        parentButton = (ImGui.GetItemRectMin() + ImGui.GetItemRectMax()) * .5f;
                        if ((bool)args[3] || (bool)args[4]) ImGui.SetNextWindowPos(new(500, 120));
                        draw.Invoke(null, args);
                        Assert.False(ImGui.IsPopupOpen(title));
                    }
                    finally { ImGui.End(); }
                    ImGui.Render();
                }
                void Click(Vector2 point)
                {
                    io.AddMousePosEvent(point.X, point.Y); Frame();
                    io.AddMouseButtonEvent(0, true); Frame();
                    io.AddMouseButtonEvent(0, false); Frame();
                }
                Vector2 CaptionCenter(string caption)
                {
                    var list = ImGuiP.FindWindowByName(title).DrawList;
                    Vector2? found = null;
                    for (var index = 0; index + caption.Length * 4 <= list.VtxBuffer.Size; index++)
                    {
                        var matches = true;
                        for (var character = 0; character < caption.Length; character++)
                        {
                            var glyph = ImGui.FindGlyphNoFallback(ImGui.GetFont(), caption[character]);
                            if (list.VtxBuffer[index + character * 4].Uv != new Vector2(glyph.Handle->U0, glyph.Handle->V0)
                                || list.VtxBuffer[index + character * 4 + 2].Uv != new Vector2(glyph.Handle->U1, glyph.Handle->V1))
                            { matches = false; break; }
                        }
                        if (matches) found = (list.VtxBuffer[index].Pos + list.VtxBuffer[index + caption.Length * 4 - 2].Pos) * .5f;
                    }
                    return found ?? throw new InvalidOperationException("Offer did not render " + caption);
                }
                Frame(); ImGui.SetWindowCollapsed(title, false); Frame(); Frame();
                var native = ImGuiP.FindWindowByName(title);
                Assert.Equal(ImGuiWindowFlags.None, native.Flags & (ImGuiWindowFlags.Popup | ImGuiWindowFlags.Modal | ImGuiWindowFlags.NoTitleBar));
                var dimColour = ImGui.GetColorU32(ImGuiCol.ModalWindowDimBg) & 0x00FFFFFF;
                var data = ImGui.GetDrawData();
                for (var listIndex = 0; listIndex < data.CmdListsCount; listIndex++)
                {
                    var vertices = new ImDrawListPtr(data.CmdLists[listIndex]).VtxBuffer;
                    for (var index = 0; index + 3 < vertices.Size; index++)
                    {
                        if ((vertices[index].Col & 0x00FFFFFF) != dimColour || (vertices[index].Col >> 24) == 0) continue;
                        var first = vertices[index].Pos; var opposite = vertices[index + 2].Pos;
                        Assert.False(first.X <= 0 && first.Y <= 0 && opposite.X >= io.DisplaySize.X && opposite.Y >= io.DisplaySize.Y,
                            "Guide offer emitted a viewport-sized dimming quad.");
                    }
                }
                Click(parentButton);
                Assert.Equal(1, parentPresses);
                io.AddKeyEvent(ImGuiKey.Escape, true); Frame();
                io.AddKeyEvent(ImGuiKey.Escape, false); Frame();
                Assert.True((bool)args[4]); // Escape in the parent must not dismiss another window.
                Assert.Equal(0, saves);
                if (action is "Dismiss" or "Open") Click(CaptionCenter(action));
                else if (action == "Close")
                {
                    var titleRect = ImGuiP.TitleBarRect(native);
                    Click(new(titleRect.Max.X - ImGui.GetStyle().FramePadding.X - ImGui.GetFontSize() * .5f,
                        titleRect.Min.Y + ImGuiP.TitleBarHeight(native) * .5f));
                }
                else if (action == "Escape")
                {
                    ImGui.SetWindowCollapsed(title, true); Frame();
                    Assert.True(native.Collapsed); // Begin(false) still requires End and retains the offer.
                    Assert.True((bool)args[4]);
                    ImGui.SetWindowFocus(title);
                    io.AddKeyEvent(ImGuiKey.Escape, true); Frame();
                    io.AddKeyEvent(ImGuiKey.Escape, false); Frame();
                }
                else if (action == "ToolbarGuide")
                {
                    Assert.True(WizardCatalog.MarkToolGuideVisited(configuration, tool));
                    saves++;
                }
                else
                {
                    object[] closeArgs = [configuration, args[1], tool, args[3], args[4]];
                    close.Invoke(null, closeArgs); args[3] = closeArgs[3]; args[4] = closeArgs[4];
                }
                Frame(); Frame();
                Assert.False((bool)args[3]); Assert.False((bool)args[4]);
                Assert.False(WizardCatalog.ShouldOfferToolGuide(configuration, tool));
                Assert.True(WizardCatalog.ShouldOfferToolGuide(configuration, tool == ToolGuide.Rules ? ToolGuide.ObjectExplorer : ToolGuide.Rules));
                Assert.Equal(action == "Open" ? 2 : 1, saves);
                Assert.Equal(action == "Open" ? 1 : 0, launches);
                Assert.Equal(action is "Open" or "ToolbarGuide" && tool == ToolGuide.Rules, configuration.RulesGuideVisited);
                Assert.Equal(action is "Open" or "ToolbarGuide" && tool == ToolGuide.ObjectExplorer, configuration.ObjectExplorerGuideVisited);
                Assert.False(configuration.RulesDataWizardCompleted);
            }
        }
        finally
        {
            Ui.SetLanguage(language); ImGui.DestroyContext(context); ImGui.SetCurrentContext(previous);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public unsafe void NativeGuidesAndDebugSettingsRenderAtBothDensitiesAndGuideOffersDismissWithEscape(bool compact, bool lcars)
    {
        var previous = ImGui.GetCurrentContext();
        var language = Ui.Language;
        var context = ImGui.CreateContext();
        try
        {
            Ui.SetLanguage(UiLanguage.English);
            var io = ImGui.GetIO();
            io.IniFilename = null;
            io.LogFilename = null;
            io.DisplaySize = new(1280, 900);
            io.DeltaTime = 1f / 60;
            io.Fonts.AddFontDefault();
            byte* pixels = null;
            int width = 0, height = 0;
            io.Fonts.GetTexDataAsRGBA32(0, &pixels, &width, &height);
            io.Fonts.SetTexID(0, new ImTextureID((nint)1));
            var theme = AdsPresentation.Theme(AdsPresentation.ReferenceAccent,
                new() { Mode = lcars ? MaterialColorMode.Lcars : MaterialColorMode.Auto });
            theme.Density = compact ? MaterialDensity.Compact : MaterialDensity.Standard;
            using var themeScope = MaterialTheme.Push(theme, 1, MaterialStyleMode.ColorsOnly);
            var drawOffer = typeof(PositionedWindow).GetMethod("DrawToolGuideOffer", BindingFlags.Static | BindingFlags.NonPublic)!;
            foreach (var tool in new[] { ToolGuide.Rules, ToolGuide.ObjectExplorer })
            {
                var configuration = new Configuration();
                var saves = 0;
                var guideOpens = 0;
                object[] args = [configuration, (Action)(() => ++saves), tool, true, false, (Action)(() => ++guideOpens)];
                for (var frame = 0; frame < 5; frame++)
                {
                    if (frame == 2) io.AddKeyEvent(ImGuiKey.Escape, true);
                    if (frame == 3) io.AddKeyEvent(ImGuiKey.Escape, false);
                    ImGui.NewFrame();
                    ImGui.SetNextWindowSize(compact ? new(760, 560) : new(1100, 800));
                    ImGui.Begin("ADS actual guide offer test", ImGuiWindowFlags.NoSavedSettings);
                    try { drawOffer.Invoke(null, args); }
                    finally { ImGui.End(); }
                    ImGui.Render();
                }
                Assert.False(WizardCatalog.ShouldOfferToolGuide(configuration, tool));
                Assert.Equal(1, saves);
                Assert.Equal(0, guideOpens);
                Assert.False(configuration.RulesGuideVisited);
                Assert.False(configuration.ObjectExplorerGuideVisited);
                Assert.False(configuration.RulesDataWizardCompleted);
            }

            using var guide = new RuleGuideWindow(compact);
            var task = typeof(RuleGuideWindow).GetField("rulesTask", BindingFlags.Instance | BindingFlags.NonPublic)!;
            foreach (var tool in new[] { ToolGuide.Rules, ToolGuide.ObjectExplorer })
            {
                guide.OpenForTool(tool);
                for (var selectedTask = 0; selectedTask < (tool == ToolGuide.Rules ? 6 : 1); selectedTask++)
                {
                    task.SetValue(guide, selectedTask);
                    for (var frame = 0; frame < 2; frame++)
                    {
                        ImGui.NewFrame();
                        ImGui.SetNextWindowSize(compact ? new(760, 560) : new(1100, 800));
                        ImGui.Begin(guide.WindowName, ImGuiWindowFlags.NoSavedSettings);
                        try { guide.Draw(); }
                        finally { ImGui.End(); }
                        ImGui.Render();
                        Assert.True(ImGui.GetDrawData().CmdListsCount > 0);
                    }
                }
            }
            foreach (var enabled in new[] { false, true })
            {
                var configuration = new Configuration
                {
                    DebugModeEnabled = enabled,
                    DebugReturnPolicy = DebugReturnPolicy.Resume,
                    ShowDebugSections = true,
                    FrameworkHitchProfilerEnabled = true,
                };
                for (var frame = 0; frame < 3; frame++)
                {
                    ImGui.NewFrame();
                    ImGui.SetNextWindowSize(compact ? new(640, 740) : new(760, 860));
                    ImGui.Begin("ADS actual Debug settings test", ImGuiWindowFlags.NoSavedSettings);
                    var changed = false;
                    try { ConfigWindow.DrawDebugSettings(configuration, enabled, _ => Assert.Fail("Unexpected user toggle"), ref changed); }
                    finally { ImGui.End(); }
                    ImGui.Render();
                    Assert.False(changed);
                    Assert.Equal(DebugReturnPolicy.Resume, configuration.DebugReturnPolicy);
                    Assert.Equal(enabled, configuration.DebugModeEnabled);
                    Assert.True(configuration.ShowDebugSections);
                    Assert.True(configuration.FrameworkHitchProfilerEnabled);
                    Assert.True(ImGui.GetDrawData().CmdListsCount > 0);
                }
            }
        }
        finally
        {
            Ui.SetLanguage(language);
            ImGui.DestroyContext(context);
            ImGui.SetCurrentContext(previous);
        }
    }

    [Fact]
    public void DebugReturnDefaultsAndNativeStoragePreserveExplicitPolicyAndIndependentDiagnostics()
    {
        var configuration = Newtonsoft.Json.JsonConvert.DeserializeObject<Configuration>("""{"Version":25}""")!;
        Assert.False(configuration.DebugModeEnabled);
        Assert.Equal(DebugReturnPolicy.RestartFromEntrance, configuration.DebugReturnPolicy);
        configuration.DebugReturnPolicy = DebugReturnPolicy.Resume;
        configuration.FrameworkHitchProfilerEnabled = true;
        configuration.ShowDebugSections = true;
        var restored = Newtonsoft.Json.JsonConvert.DeserializeObject<Configuration>(
            Newtonsoft.Json.JsonConvert.SerializeObject(configuration))!;
        Assert.False(restored.DebugModeEnabled);
        Assert.Equal(DebugReturnPolicy.Resume, restored.DebugReturnPolicy);
        Assert.True(restored.FrameworkHitchProfilerEnabled);
        Assert.True(restored.ShowDebugSections);
        Assert.Equal(25, restored.Version);
        restored.DebugReturnPolicy = (DebugReturnPolicy)99;
        Assert.Equal(DebugReturnPolicy.RestartFromEntrance, restored.DebugReturnPolicy);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ToolGuideOfferDismissalAndActualVisitsRemainIndependent(int toolIndex)
    {
        var tool = (ToolGuide)toolIndex;
        var other = tool == ToolGuide.Rules ? ToolGuide.ObjectExplorer : ToolGuide.Rules;
        var configuration = new Configuration { WizardHubSeen = true, RulesDataWizardCompleted = true };
        Assert.True(WizardCatalog.ShouldOfferToolGuide(configuration, tool));
        Assert.True(WizardCatalog.ShouldOfferToolGuide(configuration, other));
        Assert.True(WizardCatalog.ConsumeToolGuideOffer(configuration, tool)); // Dismiss, close or Escape.
        Assert.False(WizardCatalog.ConsumeToolGuideOffer(configuration, tool));
        Assert.False(WizardCatalog.ShouldOfferToolGuide(configuration, tool));
        Assert.True(WizardCatalog.ShouldOfferToolGuide(configuration, other));
        Assert.False(configuration.RulesGuideVisited);
        Assert.False(configuration.ObjectExplorerGuideVisited);
        Assert.True(WizardCatalog.MarkToolGuideVisited(configuration, tool));
        Assert.False(WizardCatalog.MarkToolGuideVisited(configuration, tool));
        Assert.True(tool == ToolGuide.Rules ? configuration.RulesGuideVisited : configuration.ObjectExplorerGuideVisited);
        Assert.True(configuration.RulesDataWizardCompleted);
        Assert.False(configuration.DutyOperationsWizardCompleted);
        Assert.False(configuration.UtilitiesWizardCompleted);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ManualToolGuideVisitSuppressesTheOfferAndPersistsWithoutCompletingAWizard(int toolIndex)
    {
        var tool = (ToolGuide)toolIndex;
        var configuration = Newtonsoft.Json.JsonConvert.DeserializeObject<Configuration>(
            """{"Version":25,"WizardHubSeen":true,"UiWindowOpacityPercent":73}""")!;
        Assert.True(WizardCatalog.ShouldOfferToolGuide(configuration, tool));
        Assert.True(WizardCatalog.MarkToolGuideVisited(configuration, tool));
        var restored = Newtonsoft.Json.JsonConvert.DeserializeObject<Configuration>(
            Newtonsoft.Json.JsonConvert.SerializeObject(configuration))!;
        Assert.False(WizardCatalog.ShouldOfferToolGuide(restored, tool));
        Assert.True(WizardCatalog.ShouldOfferToolGuide(restored, tool == ToolGuide.Rules ? ToolGuide.ObjectExplorer : ToolGuide.Rules));
        Assert.False(restored.RulesDataWizardCompleted);
        Assert.False(restored.DiagnosticsRecoveryWizardCompleted);
        Assert.Equal(73, restored.UiWindowOpacityPercent);
        Assert.Equal(25, restored.Version);
    }

    [Fact]
    public void CompactDefaultsMigrateOnceThroughNativeJsonAndPreserveLaterOverrides()
    {
        var storage = typeof(Dalamud.Plugin.IDalamudPluginInterface).Assembly.GetType("Dalamud.Configuration.PluginConfigurations", true)!;
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
        var serialize = storage.GetMethod("SerializeConfig", flags)!;
        var deserialize = storage.GetMethod("DeserializeConfig", flags)!;
        var config = Newtonsoft.Json.JsonConvert.DeserializeObject<Configuration>(
            """{"Version":24,"UiCompact":false,"UiCompactVisibleOnMainWindow":true,"UiTransparencyVisibleOnMainWindow":true,"UiWindowOpacityPercent":73,"EnableBmraiVbmInRegularDuties":false,"FutureSetting":{"value":[1,"unchanged"]}}""")!;
        Assert.True(config.UiShowTitlebarStatus);
        Assert.True(config.UiDetailedTitlebarStatus);
        Assert.True(Plugin.ApplyConfigurationMigrations(config));
        Assert.True(config.UiCompact);
        Assert.False(config.UiCompactVisibleOnMainWindow);
        Assert.False(config.UiTransparencyVisibleOnMainWindow);
        Assert.True(config.UiCompactDefaultsApplied);
        config.UiCompact = false;
        config.UiShowTitlebarStatus = config.UiDetailedTitlebarStatus = false;
        config.UiCompactVisibleOnMainWindow = config.UiTransparencyVisibleOnMainWindow = true;
        for (var reload = 0; reload < 2; reload++)
        {
            var saved = (string)serialize.Invoke(null, [config])!;
            config = (Configuration)deserialize.Invoke(null, [saved])!;
            Assert.False(config.ApplyCompactDefaults());
            Plugin.ApplyConfigurationMigrations(config); // Other existing normalizers are independent of the appearance marker.
            Assert.False(config.UiCompact);
            Assert.False(config.UiShowTitlebarStatus);
            Assert.False(config.UiDetailedTitlebarStatus);
            Assert.True(config.UiCompactVisibleOnMainWindow);
            Assert.True(config.UiTransparencyVisibleOnMainWindow);
            Assert.Equal(73, config.UiWindowOpacityPercent);
            Assert.False(config.EnableBmraiVbmInRegularDuties);
            Assert.Equal("unchanged", config.AdditionalSettings!["FutureSetting"]["value"]![1]!.ToObject<string>());
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SavedDisabledStateRecoversAndSerializesEnabledWithoutChangingFollow(bool follow)
    {
        var configuration = System.Text.Json.JsonSerializer.Deserialize<Configuration>(
            """{"Version":24,"PluginEnabled":false}""")!;
        configuration.EnableBmraiVbmInRegularDuties = follow;

        Assert.True(configuration.PluginEnabled);
        Assert.True(Plugin.ApplyConfigurationMigrations(configuration));
        Assert.False(Plugin.ApplyConfigurationMigrations(configuration));
        configuration.PluginEnabled = false; // The retained compatibility setter cannot disable ADS.
        Assert.True(configuration.PluginEnabled);
        Assert.Equal(follow, configuration.EnableBmraiVbmInRegularDuties);
        using var saved = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(configuration));
        Assert.True(saved.RootElement.GetProperty("PluginEnabled").GetBoolean());
    }

    [Fact]
    public void CatalogHasStableIdsOrderAndThreeRequiredPages()
    {
        Assert.Equal(
            [
                WizardCatalog.DutyOperationsId,
                WizardCatalog.RulesDataId,
                WizardCatalog.UtilitiesId,
                WizardCatalog.TreasureFollowId,
                WizardCatalog.DiagnosticsRecoveryId,
            ],
            WizardCatalog.All.Select(wizard => wizard.Id));
        Assert.All(WizardCatalog.All, wizard =>
        {
            Assert.Equal(["overview", "safety", "steps"], wizard.Pages.Select(page => page.Id));
            Assert.All(wizard.Pages, page => Assert.False(string.IsNullOrWhiteSpace(page.Body)));
        });
        var rules = WizardCatalog.All.Single(wizard => wizard.Id == WizardCatalog.RulesDataId);
        var rulesText = string.Join(' ', rules.Pages.SelectMany(page => page.Steps.Prepend(page.Body).Prepend(page.Title)));
        Assert.Contains("combined effective", rulesText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("complete replacement", rulesText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("multi-context promotion", rulesText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("checkout", rulesText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CompletionFlagsAreIndependentAndCompletedFlowsRemainCatalogued()
    {
        var configuration = new Configuration();

        WizardCatalog.SetCompleted(configuration, WizardCatalog.UtilitiesId);

        Assert.True(WizardCatalog.IsCompleted(configuration, WizardCatalog.UtilitiesId));
        Assert.False(WizardCatalog.IsCompleted(configuration, WizardCatalog.DutyOperationsId));
        Assert.False(WizardCatalog.IsCompleted(configuration, WizardCatalog.RulesDataId));
        Assert.False(WizardCatalog.IsCompleted(configuration, WizardCatalog.TreasureFollowId));
        Assert.False(WizardCatalog.IsCompleted(configuration, WizardCatalog.DiagnosticsRecoveryId));
        Assert.Contains(WizardCatalog.All, wizard => wizard.Id == WizardCatalog.UtilitiesId);
    }

    [Fact]
    public void NewInstallAutoOpensOnceAfterBeingMarkedSeen()
    {
        var configuration = new Configuration();

        Assert.True(WizardCatalog.ShouldAutoOpen(loadedExistingConfiguration: false, configuration));
        configuration.WizardHubSeen = true;
        Assert.False(WizardCatalog.ShouldAutoOpen(loadedExistingConfiguration: false, configuration));
    }

    [Fact]
    public void VersionTwentyMigrationMarksHubSeenWithoutCompletingFlows()
    {
        var configuration = new Configuration
        {
            Version = 20,
            WizardHubSeen = false,
            DutyOperationsWizardCompleted = true,
            RulesDataWizardCompleted = true,
            UtilitiesWizardCompleted = true,
            TreasureFollowWizardCompleted = true,
            DiagnosticsRecoveryWizardCompleted = true,
        };

        Assert.True(Plugin.ApplyConfigurationMigrations(configuration));

        Assert.Equal(25, configuration.Version);
        Assert.True(configuration.WizardHubSeen);
        Assert.False(configuration.DutyOperationsWizardCompleted);
        Assert.False(configuration.RulesDataWizardCompleted);
        Assert.False(configuration.UtilitiesWizardCompleted);
        Assert.False(configuration.TreasureFollowWizardCompleted);
        Assert.False(configuration.DiagnosticsRecoveryWizardCompleted);
        Assert.False(WizardCatalog.ShouldAutoOpen(loadedExistingConfiguration: true, configuration));
    }

    [Fact]
    public void CurrentSchemaContextDefaultsNormalizeWithoutVersionBump()
    {
        var configuration = new Configuration
        {
            Version = 25,
            ObjectRuleSelectedContextFileNames = ["bad.json", "1037_rule_objects.json"],
            ObjectRuleEditorCompactMode = true,
        };

        Assert.True(Plugin.ApplyConfigurationMigrations(configuration));

        Assert.Equal(25, configuration.Version);
        Assert.Equal(["1037_rule_objects.json"], configuration.ObjectRuleSelectedContextFileNames);
        Assert.True(configuration.ObjectRuleEditorCompactMode);
    }

    [Fact]
    public void HigherLowerDiagnosticsResetOffOnceAndPreserveLaterSavedChoice()
    {
        var configuration = new Configuration
        {
            Version = 24,
            HigherLowerDiagnosticsEnabled = true,
            HigherLowerAutomationEnabled = true,
            HigherLowerVfxDataminingEnabled = true,
        };

        Assert.True(Plugin.ApplyConfigurationMigrations(configuration));
        Assert.Equal(25, configuration.Version);
        Assert.False(configuration.HigherLowerDiagnosticsEnabled);
        Assert.True(configuration.HigherLowerAutomationEnabled);
        Assert.True(configuration.HigherLowerVfxDataminingEnabled);

        configuration.HigherLowerDiagnosticsEnabled = true;
        Assert.False(Plugin.ApplyConfigurationMigrations(configuration));
        Assert.True(configuration.HigherLowerDiagnosticsEnabled);

        var storage = typeof(Dalamud.Plugin.IDalamudPluginInterface).Assembly.GetType("Dalamud.Configuration.PluginConfigurations", true)!;
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
        var saved = (string)storage.GetMethod("SerializeConfig", flags)!.Invoke(null, [configuration])!;
        var reloaded = (Configuration)storage.GetMethod("DeserializeConfig", flags)!.Invoke(null, [saved])!;

        Plugin.ApplyConfigurationMigrations(reloaded); // Other normalizers are independent of this migration.
        Assert.Equal(25, reloaded.Version);
        Assert.True(reloaded.HigherLowerDiagnosticsEnabled);
        Assert.True(reloaded.HigherLowerAutomationEnabled);
        Assert.True(reloaded.HigherLowerVfxDataminingEnabled);
    }
}
