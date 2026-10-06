using System.Collections;
using System.Globalization;
using System.Resources;
using System.Text;
using System.Text.RegularExpressions;
using ADS.Models;
using Dalamud.Game;
using Lumina.Excel.Sheets;

namespace ADS.Localization;

public enum UiLanguage
{
    English = 0, French = 1, German = 2, Japanese = 3,
    SimplifiedChinese = 4, TraditionalChinese = 5, Spanish = 6, BrazilianPortuguese = 7, Korean = 8,
    Italian = 9, Russian = 10,
    Vietnamese = 11, Indonesian = 12, Polish = 13, Turkish = 14, Hindi = 15,
}

// Localization belongs to display code; services, command tokens and IPC stay unchanged.
public static class Ui
{
    internal static readonly ResourceManager Resources = new EmbeddedResourceManager();
    public static UiLanguage Language { get; private set; }
    public static readonly string[] LanguageLabels =
        ["English", "Français", "Deutsch", "日本語", "简体中文", "繁體中文", "Español", "Português (Brasil)", "한국어", "Italiano", "Русский", "Tiếng Việt", "Bahasa Indonesia", "Polski", "Türkçe", "हिन्दी"];
    public static CultureInfo Culture => CultureFor(Language);

    public static void SetLanguage(UiLanguage language)
        => Language = Enum.IsDefined(language) ? language : UiLanguage.English;

    public static UiLanguage ResolveLanguage(UiLanguage? saved, ClientLanguage client)
        => saved.HasValue && Enum.IsDefined(saved.Value) ? saved.Value : FromClient(client);

    public static UiLanguage FromClient(ClientLanguage client) => client switch
    {
        ClientLanguage.French => UiLanguage.French,
        ClientLanguage.German => UiLanguage.German,
        ClientLanguage.Japanese => UiLanguage.Japanese,
        _ => UiLanguage.English,
    };

    public static UiLanguage NativeLanguage(UiLanguage language, ClientLanguage client)
        => language >= UiLanguage.SimplifiedChinese ? FromClient(client) : language;

    public static ClientLanguage SheetLanguage => SheetLanguageFor(Language, Plugin.ClientState.ClientLanguage);

    public static ClientLanguage SheetLanguageFor(UiLanguage language, ClientLanguage client)
        => NativeLanguage(language, client) switch
        {
            UiLanguage.French => ClientLanguage.French,
            UiLanguage.German => ClientLanguage.German,
            UiLanguage.Japanese => ClientLanguage.Japanese,
            _ => ClientLanguage.English,
        };

    public static CultureInfo CultureFor(UiLanguage language) => CultureInfo.GetCultureInfo(language switch
    {
        UiLanguage.French => "fr", UiLanguage.German => "de", UiLanguage.Japanese => "ja",
        UiLanguage.SimplifiedChinese => "zh-Hans", UiLanguage.TraditionalChinese => "zh-Hant",
        UiLanguage.Spanish => "es", UiLanguage.BrazilianPortuguese => "pt-BR", UiLanguage.Korean => "ko",
        UiLanguage.Italian => "it", UiLanguage.Russian => "ru",
        UiLanguage.Vietnamese => "vi", UiLanguage.Indonesian => "id", UiLanguage.Polish => "pl", UiLanguage.Turkish => "tr", UiLanguage.Hindi => "hi",
        _ => "en",
    });

    // ResX compilation compares names without case; preserve distinct caption variants.
    private static string ResourceKey(string text) => text switch
    {
        "(any)" => "Caption.AnyLower",
        "Current duty" => "Caption.CurrentDutySentence",
        "Duty" => "Caption.DutyTitle",
        "duty" => "Caption.DutyLower",
        "enabled" => "Enabled",
        "deep dungeon" => "Caption.DeepDungeonLower",
        "Dialog rules" => "Caption.DialogRulesSentence",
        "Execution Phase" => "Caption.ExecutionPhaseTitle",
        "Flag" => "Caption.FlagTitle",
        "failed" => "Caption.FailedLower",
        "Global" => "Caption.GlobalTitle",
        "guild hest" => "Caption.GuildHestLower",
        "item {0}" => "Caption.ItemLower",
        "local" => "Caption.LocalLower",
        "map XZ destination" => "Caption.MapDestinationLower",
        "MapXz" => "Caption.MapXz",
        "No" => "Caption.NoTitle",
        "New preset" => "Caption.NewPresetSentence",
        "none" => "Caption.NoneLower",
        "Off" => "Caption.OffTitle",
        "On" => "Caption.OnTitle",
        "Ownership" => "Caption.OwnershipTitle",
        "Object rules" => "Caption.ObjectRulesSentence",
        "Pass" => "Caption.PassTitle",
        "PR-ready checkout" => "Caption.PrCheckoutSentence",
        "pending" => "Caption.PendingLower",
        "position" => "Caption.PositionLower",
        "Rule" => "Caption.RuleTitle",
        "ready" => "Caption.ReadyLower",
        "remote" => "Caption.RemoteLower",
        "saved" => "Caption.SavedLower",
        "selected rows" => "Caption.SelectedRowsLower",
        "territory {0}" => "Caption.TerritoryLower",
        "treasure coffer" => "Caption.TreasureCofferLower",
        "unavailable" => "Caption.UnavailableLower",
        "unknown" => "Caption.UnknownLower",
        "unsafe transition" => "Caption.UnsafeTransitionLower",
        "Xyz" => "Caption.XyzTitle",
        "Yes" => "Caption.YesTitle",
        "force-march map XZ destination" => "Caption.ForceMarchDestinationLower",
        _ => text,
    };

    public static string T(string text, params object?[] args) => T(Language, text, args);

    public static string T(UiLanguage language, string text, params object?[] args)
    {
        var culture = CultureFor(language);
        var translated = Resources.GetString(ResourceKey(text), culture) ?? text;
        return args.Length == 0 ? translated : string.Format(culture, translated, args);
    }

    // Preserve existing ### IDs; original-text IDs keep other labels stable across languages.
    public static string L(string text)
    {
        var id = text.IndexOf("##", StringComparison.Ordinal);
        if (id == 0) return text;
        return Display(id < 0 ? text : text[..id]) + "###" + text;
    }

    public static string L(string template, params object?[] args)
    {
        var original = string.Format(CultureInfo.InvariantCulture, template, args);
        var id = template.IndexOf("##", StringComparison.Ordinal);
        if (id == 0) return original;
        return T(id < 0 ? template : template[..id], args) + "###" + original;
    }

    public static string Value(string text) => Display(text);

    public static string DutyName(DutyCatalogEntry duty)
    {
        if (duty.ContentFinderConditionId == 0)
            return TerritoryName(duty.TerritoryTypeId, duty.Name);
        var row = Plugin.DataManager.GetExcelSheet<ContentFinderCondition>(SheetLanguage)
            .GetRowOrDefault(duty.ContentFinderConditionId);
        var name = row?.Name.ExtractText();
        if (string.IsNullOrWhiteSpace(name) && row is { } condition
            && condition.Content.TryGetValue<QuestBattle>(out var battle)
            && battle.Quest.Is<Quest>())
        {
            name = Plugin.DataManager.GetExcelSheet<Quest>(SheetLanguage)
                .GetRowOrDefault(battle.Quest.RowId)?.Name.ExtractText();
        }
        return string.IsNullOrWhiteSpace(name)
            ? SheetLanguage == ClientLanguage.English && !string.IsNullOrWhiteSpace(duty.EnglishName)
                ? duty.EnglishName : duty.Name
            : DutyMaturityCatalog.NormalizeText(name);
    }

    public static string ItemName(uint itemId, string original)
    {
        var name = Plugin.DataManager.GetExcelSheet<Item>(SheetLanguage).GetRowOrDefault(itemId)?.Name.ExtractText();
        return string.IsNullOrWhiteSpace(name) ? original : name;
    }

    public static string TerritoryName(uint territoryId, string original)
    {
        var name = Plugin.DataManager.GetExcelSheet<TerritoryType>(SheetLanguage)
            .GetRowOrDefault(territoryId)?.PlaceName.ValueNullable?.Name.ExtractText();
        return string.IsNullOrWhiteSpace(name) ? original : name;
    }

    public static string ExpansionName(uint expansionId, string original)
    {
        var name = Plugin.DataManager.GetExcelSheet<ExVersion>(SheetLanguage)
            .GetRowOrDefault(expansionId)?.Name.ExtractText();
        return string.IsNullOrWhiteSpace(name) ? original : name;
    }

    public static string NpcName(uint npcId, string original)
    {
        var name = Plugin.DataManager.GetExcelSheet<ENpcResident>(SheetLanguage)
            .GetRowOrDefault(npcId)?.Singular.ExtractText();
        return string.IsNullOrWhiteSpace(name) ? original : name;
    }

    public static string ShopName(string kind, uint shopId, string original)
    {
        var name = kind switch
        {
            nameof(ShopOfferKind.GilShop) => Plugin.DataManager.GetExcelSheet<GilShop>(SheetLanguage).GetRowOrDefault(shopId)?.Name.ExtractText(),
            nameof(ShopOfferKind.FreeCompanyShop) => Plugin.DataManager.GetExcelSheet<FccShop>(SheetLanguage).GetRowOrDefault(shopId)?.Name.ExtractText(),
            _ when kind.StartsWith("SpecialShop", StringComparison.Ordinal) => Plugin.DataManager.GetExcelSheet<SpecialShop>(SheetLanguage).GetRowOrDefault(shopId)?.Name.ExtractText(),
            _ => null,
        };
        return string.IsNullOrWhiteSpace(name) ? original : name;
    }

    private static readonly Regex Placeholder = new(@"(?<!\{)\{(?<index>\d+)(?:,-?\d+)?(?::[^{}]*)?\}(?!\})",
        RegexOptions.CultureInvariant);

    // Legacy service statuses remain English internally. Only complete registered
    // templates are recognized, and captured values retain their original content.
    // Unit/version formats such as "{0} v{1}" are not status prose.
    private static readonly Lazy<(string Key, Lazy<Regex> Pattern, string Prefix)[]> Templates = new(() =>
        Resources.GetResourceSet(CultureInfo.GetCultureInfo("en"), true, true)!
            .Cast<DictionaryEntry>().Select(entry => (string)entry.Value!)
            .Where(key => Placeholder.IsMatch(key) && Regex.IsMatch(Placeholder.Replace(key, ""), @"\p{L}{4,}", RegexOptions.CultureInvariant))
            .OrderBy(key => Placeholder.Match(key).Index == 0 &&
                // The lazy factory runs after Ui's static fields have initialized.
                (!NestedStatusArguments!.TryGetValue(key, out var nested) || !nested.Contains(0)) ? 1 : 0)
            .ThenByDescending(key => Placeholder.Replace(key, "").Length)
            .Select(key => (key, new Lazy<Regex>(() => BuildPattern(key)), UnescapeBraces(key[..Placeholder.Match(key).Index]).Replace("\r\n", "\n").Split('\n')[0])).ToArray());

    private static Regex BuildPattern(string key)
    {
        var pattern = new StringBuilder(@"\A");
        var offset = 0;
        foreach (Match token in Placeholder.Matches(key))
        {
            pattern.Append(LiteralPattern(UnescapeBraces(key[offset..token.Index])));
            pattern.Append("(?<p").Append(token.Groups["index"].Value).Append(">.*?)");
            offset = token.Index + token.Length;
        }
        pattern.Append(LiteralPattern(UnescapeBraces(key[offset..]))).Append(@"\z");
        return new Regex(pattern.ToString(), RegexOptions.CultureInvariant | RegexOptions.Singleline,
            TimeSpan.FromMilliseconds(20));
    }

    private static string UnescapeBraces(string text) => text.Replace("{{", "{").Replace("}}", "}");
    private static string LiteralPattern(string text)
        => string.Join(@"(?:\r\n|\n)", text.Replace("\r\n", "\n").Split('\n').Select(Regex.Escape));

    // Only these captured fields are generated statuses. Names, paths and diagnostics stay raw.
    private static readonly Dictionary<string, int[]> NestedStatusArguments = new(StringComparer.Ordinal)
    {
        ["Another ADS utility is active: {0}"] = [0],
        ["Cancelled {0}: {1}"] = [0, 1],
        ["Shop-list preset {0}: {1}"] = [0, 1],
        [" Cleanup result: {0}"] = [0],
        ["Shop-list batch cancelled after {0} completed row(s); current row acquired {1} of {2}.{3}"] = [3],
        ["Shop-list batch stopped on {0}: {1} Acquired {2} of {3} before stopping."] = [1],
        ["Timed out validating live shop UI: {0}"] = [0],
        ["Shop purchase cancelled: {0}"] = [0],
        ["Cancelling shop-list batch: {0}"] = [0],
        ["{0} could not start: {1}"] = [1],
        ["NPC repair could not enter the inn: {0}"] = [0],
        ["NPC repair did not enter the inn room: {0}"] = [0],
        ["Returning to the inn room: {0}"] = [0],
        ["{0} Returning to the inn room."] = [0],
        ["no trusted runtime card decode; addonCurrentCard={0}; solverCard={1}; solverConfidence={2}; solverReason='{3}'"] = [3],
        ["no trusted runtime card decode; addon atk ignored; addonCard={0}; currentGraphicKey='{1}'; solverCard={2}; solverConfidence={3}; solverReason='{4}'"] = [4],
        ["Higher/Lower automation {0}; surface={1} dutyKey={2} step={3} playsCompleted={4} card={5} action={6} source='{7}' directionSource={8} directionTarget={9}@{10} pendingTarget={11} pendingPhase={12} pendingAge={13} pendingPhaseAge={14} {15} pendingBaselineServerRowSeq={16} terminalProof={17} callbackAction={18} callbackPhase={19} callbackAge={20} blocksDutyExit={21} exitGrace={22} lastActivity={23} blockedReason='{24}' active={25} addonCurrentCard={26} addonOtherCard={27} knownCards={28}."] = [0, 15, 24],
        ["Higher/Lower solver {0}; surface={1} dutyKey={2} step={3} playsCompleted={4} directionSource={5} directionTarget={6}@{7} pendingTarget={8} pendingPhase={9} pendingAge={10} pendingPhaseAge={11} {12} pendingBaselineServerRowSeq={13} terminalProof={14} callbackAction={15} callbackPhase={16} callbackAge={17} blocksDutyExit={18} exitGrace={19} lastActivity={20} blockedReason='{21}' active={22} addonCurrentCard={23} addonOtherCard={24} decodedCard={25} solverChoice={26} confidence={27} reason='{28}' source='{29}' slot={30} textureIndex={31}."] = [0, 12, 21, 28],
        ["Owned in {0}. {1}"] = [1],
        ["Owned after outside start in {0}. {1}"] = [1],
        ["Leave requested. Higher/Lower activity is blocking duty exit; ADS is holding leave UI and will re-run final treasure sweep after the quiet grace. {0}"] = [0],
        ["Resolve the preview error for {0} before running: {1}"] = [1],
        ["All {0} identical-cost candidates failed before purchase. Last failure: {1}"] = [1],
        ["{0} Final navigation cleanup reported that the owned path was still running."] = [0],
        ["{0} Final navigation cleanup could not verify that the owned path stopped."] = [0],
        ["Added a new global rule row. {0}"] = [0],
        ["Added a new rule row scoped to current area and label '{0}'. {1}"] = [1],
        ["Added a new rule row scoped to current area. {0}"] = [0],
        ["Added a new rule row scoped to current area. {0} Active label unavailable; Layer left blank."] = [0],
        ["Could not switch from {0} to {1}; the current preset and in-memory draft were kept unchanged. {2}"] = [2],
        ["Disk conflict: preset {0} changed on disk but could not be loaded. The current in-memory draft was kept. {1}"] = [1],
        ["Disk conflict: preset {0} could not be loaded. The current in-memory draft was kept. {1}"] = [1],
        ["Disk conflict: {0} Future saves require explicit overwrite confirmation."] = [0],
        ["Disk conflict: {0} The current in-memory draft was kept."] = [0],
        ["Exported overrides for {0} to {1}. {2}"] = [2],
        ["Imported full dialog manifest from clipboard into preset {0} draft. {1}"] = [1],
        ["Imported full dialog manifest from disk into preset {0} draft. {1}"] = [1],
        ["Loaded the current DEFAULT cache rules into the draft. {0} Press Save to write them live."] = [0],
        ["Loaded the current DEFAULT dialog cache into the draft. {0} Press Save to write it live."] = [0],
        ["Prepared {0} import for destination preset {1}. {2}"] = [2],
        ["Preset {0} was not loaded; the current in-memory draft was kept unchanged. {1}"] = [1],
        ["Test {0}: {1} No travel or purchase was started."] = [0, 1],
        ["{0} Deleted: {1}. Skipped: {2}."] = [0],
    };

    public static string Display(string text) => Display(Language, text);

    public static string Display(UiLanguage language, string text)
    {
        if (language == UiLanguage.English || string.IsNullOrEmpty(text)) return text;
        if (Resources.GetString(ResourceKey(text), CultureFor(language)) is { } exact) return exact;
        foreach (var (key, pattern, prefix) in Templates.Value)
        {
            if (!text.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var regex = pattern.Value;
            var match = regex.Match(text);
            if (!match.Success) continue;
            var indices = regex.GetGroupNames().Where(name => name.StartsWith('p'))
                .Select(name => int.Parse(name[1..], CultureInfo.InvariantCulture)).ToArray();
            var args = new object?[indices.Max() + 1];
            foreach (var index in indices) args[index] = match.Groups["p" + index].Value;
            if (NestedStatusArguments.TryGetValue(key, out var nested))
                foreach (var index in nested)
                    args[index] = Display(language, (string?)args[index] ?? string.Empty);
            return T(language, key, args);
        }
        return text;
    }
}
