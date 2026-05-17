using System.Globalization;
using System.Numerics;
using ADS.Models;
using Dalamud.Plugin.Services;

namespace ADS.Services;

public sealed unsafe class HigherLowerCardVfxSolverService
{
    private const int MaxProbeLogCandidates = 24;
    public const string HigherChoice = "Higher";
    public const string LowerChoice = "Lower";
    public const string OpenChestChoice = "OpenChest";
    public const string AvfxTexturePathSource = "avfx-texture-path";
    public const string VisualGraphicKeySource = "visual-graphic-key";
    public const string AddonAtkValueSource = "addon-atk-value[4]";
    private static readonly TimeSpan ActiveProbeTtl = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan SolverLogCooldown = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SuggestionLogCooldown = TimeSpan.FromSeconds(4);

    private readonly TreasureHighLowDiagnosticService diagnostics;
    private readonly HigherLowerVfxTraceService vfxTraceService;
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;
    private readonly object gate = new();
    private readonly Dictionary<string, AvfxMetadataCacheEntry> avfxMetadataCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CardProbeRow> recentProbes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> loggedProbeKeys = new(StringComparer.Ordinal);
    private DateTime lastSolverLogUtc = DateTime.MinValue;
    private DateTime lastSuggestionLogUtc = DateTime.MinValue;
    private string lastSolverLogKey = string.Empty;
    private string lastSuggestionLogKey = string.Empty;
    private SolverState currentState = SolverState.Inactive;

    public HigherLowerCardVfxSolverService(
        TreasureHighLowDiagnosticService diagnostics,
        HigherLowerVfxTraceService vfxTraceService,
        IDataManager dataManager,
        IPluginLog log)
    {
        this.diagnostics = diagnostics;
        this.vfxTraceService = vfxTraceService;
        this.dataManager = dataManager;
        this.log = log;
    }

    public SolverState CurrentState
    {
        get
        {
            lock (gate)
                return currentState;
        }
    }

    public CardProbeRow BuildProbe(
        string path,
        nint pointer,
        Vector3 position,
        DateTime timestampUtc,
        DutyContextSnapshot context)
    {
        var normalizedPath = HigherLowerCardVfxCatalog.NormalizePath(path);
        var slots = diagnostics.CaptureBoardSlots();
        var slot = ResolveSlot(position, slots);
        var slotCandidates = FormatSlotCandidates(position, slots);
        var source = AvfxTexturePathSource;
        var territoryId = context.TerritoryTypeId != 0 ? context.TerritoryTypeId : 0;

        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return CardProbeRow.Blocked(
                timestampUtc,
                path,
                pointer,
                slot,
                position,
                territoryId,
                source,
                slotCandidates,
                "no-card-catalog");
        }

        if (HigherLowerCardVfxCatalog.IsEffectOnly(normalizedPath))
        {
            return CardProbeRow.Blocked(
                timestampUtc,
                path,
                pointer,
                slot,
                position,
                territoryId,
                source,
                slotCandidates,
                "effect-only-card-vfx");
        }

        if (!HigherLowerCardVfxCatalog.TryGetCatalog(normalizedPath, out var catalog))
        {
            return CardProbeRow.Blocked(
                timestampUtc,
                path,
                pointer,
                slot,
                position,
                territoryId,
                source,
                slotCandidates,
                "no-card-catalog");
        }

        var inputs = ReadAvfxDecodeInputs(catalog, out var avfxFailureReason);

        if (slot is not ("left" or "right"))
        {
            return new CardProbeRow(
                TimestampUtc: timestampUtc,
                Path: path,
                NormalizedPath: catalog.Path,
                Pointer: pointer,
                Slot: slot,
                Position: position,
                TerritoryId: territoryId,
                CardSource: source,
                TextureIndex: null,
                TextureIndexCandidates: inputs.TextureIndexCandidates,
                CardTexturePaths: inputs.CardTexturePaths,
                SlotCandidates: slotCandidates,
                Pair: string.Empty,
                DecodedCard: null,
                Confidence: SolverConfidence.Blocked,
                Reason: "no-card-slot");
        }

        var txNoCardMatches = BuildTxNoCardTextureMatches(catalog, inputs.TextureIndexCandidates)
            .GroupBy(static x => x.CardTexturePath.TexturePath, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.OrderBy(static x => x.Candidate.Value).First())
            .OrderBy(static x => x.CardTexturePath.TextureIndex)
            .ToList();

        if (txNoCardMatches.Count == 1
            && HigherLowerCardVfxCatalog.TryDecode(catalog.Path, txNoCardMatches[0].CardTexturePath.TexturePath, slot, out var decodedCard, out var pairText))
        {
            return new CardProbeRow(
                TimestampUtc: timestampUtc,
                Path: path,
                NormalizedPath: catalog.Path,
                Pointer: pointer,
                Slot: slot,
                Position: position,
                TerritoryId: territoryId,
                CardSource: source,
                TextureIndex: txNoCardMatches[0].Candidate.Value,
                TextureIndexCandidates: inputs.TextureIndexCandidates,
                CardTexturePaths: inputs.CardTexturePaths,
                SlotCandidates: slotCandidates,
                Pair: pairText,
                DecodedCard: decodedCard,
                Confidence: SolverConfidence.High,
                Reason: "texture-path-decoded");
        }

        if (txNoCardMatches.Count > 1)
        {
            return new CardProbeRow(
                TimestampUtc: timestampUtc,
                Path: path,
                NormalizedPath: catalog.Path,
                Pointer: pointer,
                Slot: slot,
                Position: position,
                TerritoryId: territoryId,
                CardSource: source,
                TextureIndex: null,
                TextureIndexCandidates: inputs.TextureIndexCandidates,
                CardTexturePaths: inputs.CardTexturePaths,
                SlotCandidates: slotCandidates,
                Pair: string.Empty,
                DecodedCard: null,
                Confidence: SolverConfidence.Blocked,
                Reason: "ambiguous-card-texture-paths");
        }

        var distinctCardTexturePaths = DeduplicateCardTexturePaths(inputs.CardTexturePaths);
        if (distinctCardTexturePaths.Count == 1
            && HigherLowerCardVfxCatalog.TryDecode(catalog.Path, distinctCardTexturePaths[0].TexturePath, slot, out decodedCard, out pairText))
        {
            return new CardProbeRow(
                TimestampUtc: timestampUtc,
                Path: path,
                NormalizedPath: catalog.Path,
                Pointer: pointer,
                Slot: slot,
                Position: position,
                TerritoryId: territoryId,
                CardSource: source,
                TextureIndex: distinctCardTexturePaths[0].TextureIndex,
                TextureIndexCandidates: inputs.TextureIndexCandidates,
                CardTexturePaths: inputs.CardTexturePaths,
                SlotCandidates: slotCandidates,
                Pair: pairText,
                DecodedCard: decodedCard,
                Confidence: SolverConfidence.High,
                Reason: "single-card-texture-path-decoded");
        }

        return new CardProbeRow(
            TimestampUtc: timestampUtc,
            Path: path,
            NormalizedPath: catalog.Path,
            Pointer: pointer,
            Slot: slot,
            Position: position,
            TerritoryId: territoryId,
            CardSource: source,
            TextureIndex: null,
            TextureIndexCandidates: inputs.TextureIndexCandidates,
            CardTexturePaths: inputs.CardTexturePaths,
            SlotCandidates: slotCandidates,
            Pair: string.Empty,
            DecodedCard: null,
            Confidence: SolverConfidence.Blocked,
            Reason: !string.IsNullOrWhiteSpace(avfxFailureReason)
                ? avfxFailureReason
                : distinctCardTexturePaths.Count == 0
                    ? "no-card-texture-path"
                    : "ambiguous-card-texture-paths");
    }

    public CardProbeRow? FindTrackedProbe(string path, long vfxId, Vector3 position)
    {
        lock (gate)
        {
            PruneRecentProbes(DateTime.UtcNow);
            var pointerKey = BuildPointerKey((nint)vfxId);
            if (recentProbes.TryGetValue(pointerKey, out var pointerProbe)
                && PathsMatch(pointerProbe.Path, path))
            {
                return pointerProbe;
            }

            return recentProbes.Values
                .Where(x => PathsMatch(x.Path, path))
                .Where(x => position == Vector3.Zero || x.Position == Vector3.Zero || Vector3.Distance(x.Position, position) <= 1.5f)
                .OrderByDescending(x => x.DecodedCard.HasValue)
                .ThenBy(x => position == Vector3.Zero || x.Position == Vector3.Zero ? 0f : Vector3.Distance(x.Position, position))
                .FirstOrDefault();
        }
    }

    public void RecordProbe(CardProbeRow probe)
    {
        if (string.IsNullOrWhiteSpace(probe.Path))
            return;

        var knownBoard = diagnostics.PeekKnownBoardForSolver();
        var now = DateTime.UtcNow;
        lock (gate)
        {
            if (probe.Pointer != nint.Zero)
                recentProbes[BuildPointerKey(probe.Pointer)] = probe;

            if (!string.IsNullOrWhiteSpace(probe.NormalizedPath))
                recentProbes[BuildPositionKey(probe.NormalizedPath, probe.Position)] = probe;

            PruneRecentProbes(now);
        }

        diagnostics.RecordDatamineCardProbe(probe);
        LogProbe(probe, knownBoard);
    }

    public void Update(DutyContextSnapshot context)
    {
        var runtime = diagnostics.CaptureRuntimeState();
        var now = DateTime.UtcNow;
        IReadOnlyList<HigherLowerVfxTraceService.TrackedVfxRow> trackedRows = [];
        if (runtime.Active)
        {
            try
            {
                trackedRows = vfxTraceService.GetTrackedSnapshot(context);
            }
            catch (Exception ex)
            {
                log.Warning(ex, "[ADS][HLSOLVER] Failed to snapshot tracked VFX rows.");
            }
        }

        SolverState next;

        lock (gate)
        {
            PruneRecentProbes(now);
            if (!runtime.Active)
            {
                next = SolverState.Inactive;
            }
            else
            {
                var trackedCurrent = trackedRows
                    .Where(static x => x.Slot == "left")
                    .Where(static x => x.DecodedCard.HasValue)
                    .OrderBy(static x => x.AgeSeconds)
                    .FirstOrDefault();
                if (trackedCurrent != null)
                {
                    next = BuildState(runtime, trackedCurrent);
                }
                else
                {
                    var currentProbe = recentProbes.Values
                        .Where(static x => x.Slot == "left")
                        .Where(x => now - x.TimestampUtc <= ActiveProbeTtl)
                        .OrderByDescending(static x => x.Confidence == SolverConfidence.High)
                        .ThenByDescending(static x => x.TimestampUtc)
                        .FirstOrDefault();

                    next = BuildState(runtime, currentProbe);
                }

                if (next.Confidence != SolverConfidence.High
                    && runtime.CardSourceSafe
                    && runtime.CurrentCard is >= 1 and <= 9)
                {
                    next = BuildVisualState(runtime);
                }

                next = ApplyAddonAgreement(runtime, next);
            }

            currentState = next;
        }

        LogSolverState(next, context, runtime);
        diagnostics.RecordDatamineSolverState(context.TerritoryTypeId, runtime, next);
    }

    public string DumpState()
    {
        lock (gate)
        {
            return $"Higher/Lower solver active={currentState.Active} card={currentState.CurrentCard?.ToString(CultureInfo.InvariantCulture) ?? "unknown"} choice={currentState.RecommendedChoice} confidence={currentState.Confidence} reason='{currentState.Reason}' source='{currentState.CardSource}' textureIndexSource={currentState.TextureIndexSource} cachedAvfx={avfxMetadataCache.Count} recentProbes={recentProbes.Count}";
        }
    }

    public static string FormatCandidates(IReadOnlyList<TextureIndexCandidate> candidates)
    {
        if (candidates.Count == 0)
            return "none";

        var visible = candidates.Take(MaxProbeLogCandidates).Select(static x => x.ToString()).ToList();
        if (candidates.Count > MaxProbeLogCandidates)
            visible.Add($"more={candidates.Count - MaxProbeLogCandidates}");

        return string.Join(",", visible);
    }

    public static string FormatPosition(Vector3 value)
        => value == Vector3.Zero
            ? "-"
            : string.Create(CultureInfo.InvariantCulture, $"{value.X:0.00},{value.Y:0.00},{value.Z:0.00}");

    public static string Escape(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal);

    private static SolverState BuildVisualState(TreasureHighLowDiagnosticService.HigherLowerRuntimeState runtime)
    {
        var card = runtime.CurrentCard!.Value;
        var decision = BuildDecision(card);
        return new SolverState(
            Active: runtime.Active,
            CurrentCard: card,
            RecommendedChoice: decision.Choice,
            Confidence: SolverConfidence.High,
            Reason: decision.Reason,
            CardSource: VisualGraphicKeySource,
            Slot: "visual-current",
            TextureIndex: null,
            TextureIndexSource: "none");
    }

    private SolverState BuildState(
        TreasureHighLowDiagnosticService.HigherLowerRuntimeState runtime,
        CardProbeRow? currentProbe)
    {
        if (currentProbe == null)
        {
            return new SolverState(
                Active: runtime.Active,
                CurrentCard: null,
                RecommendedChoice: "Blocked",
                Confidence: SolverConfidence.Blocked,
                Reason: "no-current-card-vfx",
                CardSource: "none",
                Slot: "left",
                TextureIndex: null,
                TextureIndexSource: "none");
        }

        if (currentProbe.Confidence != SolverConfidence.High || !currentProbe.DecodedCard.HasValue)
        {
            return new SolverState(
                Active: runtime.Active,
                CurrentCard: null,
                RecommendedChoice: "Blocked",
                Confidence: SolverConfidence.Blocked,
                Reason: currentProbe.Reason,
                CardSource: currentProbe.CardSource,
                Slot: currentProbe.Slot,
                TextureIndex: currentProbe.TextureIndex,
                TextureIndexSource: NormalizeSource(currentProbe.CardSource));
        }

        var card = currentProbe.DecodedCard.Value;
        var decision = BuildDecision(card);
        return new SolverState(
            Active: runtime.Active,
            CurrentCard: card,
            RecommendedChoice: decision.Choice,
            Confidence: SolverConfidence.High,
            Reason: decision.Reason,
            CardSource: currentProbe.CardSource,
            Slot: currentProbe.Slot,
            TextureIndex: currentProbe.TextureIndex,
            TextureIndexSource: NormalizeSource(currentProbe.CardSource));
    }

    private SolverState BuildState(
        TreasureHighLowDiagnosticService.HigherLowerRuntimeState runtime,
        HigherLowerVfxTraceService.TrackedVfxRow row)
    {
        if (!row.DecodedCard.HasValue)
        {
            return new SolverState(
                Active: runtime.Active,
                CurrentCard: null,
                RecommendedChoice: "Blocked",
                Confidence: SolverConfidence.Blocked,
                Reason: row.SolverReason,
                CardSource: row.CardSource,
                Slot: row.Slot,
                TextureIndex: row.TextureIndex,
                TextureIndexSource: NormalizeSource(row.CardSource));
        }

        var card = row.DecodedCard.Value;
        var decision = BuildDecision(card);
        return new SolverState(
            Active: runtime.Active,
            CurrentCard: card,
            RecommendedChoice: decision.Choice,
            Confidence: SolverConfidence.High,
            Reason: decision.Reason,
            CardSource: row.CardSource,
            Slot: row.Slot,
            TextureIndex: row.TextureIndex,
            TextureIndexSource: NormalizeSource(row.CardSource));
    }

    private static SolverState ApplyAddonAgreement(
        TreasureHighLowDiagnosticService.HigherLowerRuntimeState runtime,
        SolverState state)
    {
        if (state.Confidence != SolverConfidence.High
            || !state.CurrentCard.HasValue
            || state.CurrentCard.Value is < 1 or > 9
            || !runtime.AddonCurrentCard.HasValue
            || runtime.AddonCurrentCard.Value is < 1 or > 9)
        {
            return state;
        }

        var addonCard = runtime.AddonCurrentCard.Value;
        var decodedCard = state.CurrentCard.Value;
        if (addonCard != decodedCard)
        {
            return state with
            {
                CurrentCard = null,
                RecommendedChoice = "Blocked",
                Confidence = SolverConfidence.Blocked,
                Reason = $"addon-card-mismatch addonCard={addonCard} decodedCard={decodedCard} decodedSource='{Escape(state.CardSource)}'",
                CardSource = $"{NormalizeSource(state.CardSource)}+addon-mismatch",
            };
        }

        if (state.CardSource.Contains("addon-atk-match", StringComparison.OrdinalIgnoreCase))
            return state;

        return state with
        {
            CardSource = $"{NormalizeSource(state.CardSource)}+addon-atk-match",
            Reason = $"{state.Reason}; addon atk-value[4] matched",
        };
    }

    private static (string Choice, string Reason) BuildDecision(int card)
    {
        if (card <= 4)
            return (HigherChoice, "card <= 4");
        if (card == 5)
            return (OpenChestChoice, "card = 5; open chest");
        return (LowerChoice, "card >= 6");
    }

    private void LogProbe(CardProbeRow probe, TreasureHighLowDiagnosticService.KnownBoardSnapshot? knownBoard)
    {
        if (!probe.IsCatalogCard)
            return;

        var knownText = BuildKnownBoardText(probe, knownBoard);
        var line = probe.ToHldbgLogLine(knownText);
        var logKey = $"{probe.Pointer:X}:{probe.NormalizedPath}:{probe.Slot}:{probe.TextureIndex?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}:{probe.Reason}:{FormatCandidates(probe.TextureIndexCandidates)}:{FormatCardTexturePaths(probe.CardTexturePaths)}:{probe.SlotCandidates}";
        var now = DateTime.UtcNow;
        lock (gate)
        {
            if (loggedProbeKeys.TryGetValue(logKey, out var last) && now - last < SolverLogCooldown)
                return;

            loggedProbeKeys[logKey] = now;
        }

        diagnostics.RecordSolverLine(probe.TerritoryId, line);
    }

    private static string BuildKnownBoardText(CardProbeRow probe, TreasureHighLowDiagnosticService.KnownBoardSnapshot? knownBoard)
    {
        if (knownBoard == null)
            return "knownLeftCard=none knownRightCard=none boardTagSeq=none boardValidation=none";

        var validation = "unsolved";
        if (probe.DecodedCard.HasValue && probe.Slot is "left" or "right")
        {
            var knownCardText = probe.Slot == "left" ? knownBoard.LeftCard : knownBoard.RightCard;
            validation = int.TryParse(knownCardText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var knownCard)
                ? knownCard == probe.DecodedCard.Value ? "match" : "mismatch"
                : "unknown";
        }

        return $"knownLeftCard={knownBoard.LeftCard} knownRightCard={knownBoard.RightCard} boardTagSeq={knownBoard.Sequence} boardValidation={validation}";
    }

    private void LogSolverState(
        SolverState state,
        DutyContextSnapshot context,
        TreasureHighLowDiagnosticService.HigherLowerRuntimeState runtime)
    {
        if (!state.Active)
            return;

        var addonCurrentCard = runtime.AddonCurrentCard?.ToString(CultureInfo.InvariantCulture) ?? runtime.AddonCurrentCardText;
        var addonOtherCard = runtime.AddonOtherCard?.ToString(CultureInfo.InvariantCulture) ?? runtime.AddonOtherCardText;
        var decodedCard = state.CurrentCard?.ToString(CultureInfo.InvariantCulture) ?? "unknown";
        var logKey = $"{addonCurrentCard}:{addonOtherCard}:{decodedCard}:{state.RecommendedChoice}:{state.Confidence}:{state.Reason}:{state.CardSource}:{state.TextureIndex?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}";
        var now = DateTime.UtcNow;
        var shouldLog = logKey != lastSolverLogKey || now - lastSolverLogUtc >= SolverLogCooldown;
        if (!shouldLog)
            return;

        lastSolverLogKey = logKey;
        lastSolverLogUtc = now;
        var action = string.Equals(state.RecommendedChoice, HigherChoice, StringComparison.OrdinalIgnoreCase)
                     || string.Equals(state.RecommendedChoice, LowerChoice, StringComparison.OrdinalIgnoreCase)
            ? state.RecommendedChoice.ToLowerInvariant()
            : string.Equals(state.RecommendedChoice, OpenChestChoice, StringComparison.OrdinalIgnoreCase)
                ? "open-chest"
                : "blocked";
        var line = $"higher-lower-solver addonCurrentCard={addonCurrentCard} addonOtherCard={addonOtherCard} decodedCard={decodedCard} action={action} confidence={state.Confidence.ToString().ToLowerInvariant()} reason='{Escape(state.Reason)}' source='{Escape(state.CardSource)}' slot={state.Slot} textureIndex={state.TextureIndex?.ToString(CultureInfo.InvariantCulture) ?? "unknown"} textureIndexSource={Escape(state.TextureIndexSource)}";
        diagnostics.RecordSolverLine(context.TerritoryTypeId, line);
    }

    public void LogSuggestionIfNeeded(SolverState state)
    {
        if (!state.Active || state.Confidence != SolverConfidence.High || !state.CurrentCard.HasValue)
            return;

        var key = $"{state.CurrentCard}:{state.RecommendedChoice}:{state.CardSource}";
        var now = DateTime.UtcNow;
        if (key == lastSuggestionLogKey && now - lastSuggestionLogUtc < SuggestionLogCooldown)
            return;

        lastSuggestionLogKey = key;
        lastSuggestionLogUtc = now;
        log.Information($"[ADS][HLSOLVER] card={state.CurrentCard} -> {state.RecommendedChoice}; confidence=high; source={state.CardSource}; reason={state.Reason}.");
    }

    private AvfxDecodeInputs ReadAvfxDecodeInputs(HigherLowerCardVfxCatalog.CardCatalog catalog, out string failureReason)
    {
        failureReason = string.Empty;
        var entry = GetAvfxMetadata(catalog.Path);
        if (entry.Metadata == null)
        {
            failureReason = entry.FailureReason;
            return AvfxDecodeInputs.Empty;
        }

        var textureIndexCandidates = entry.Metadata.TextureIndexes
            .Select(static x => new TextureIndexCandidate(x.Offset, 4, x.Value, x.Source, x.TexturePath))
            .ToList();
        var cardTexturePaths = BuildCardTexturePathMatches(catalog, entry.Metadata.TexturePaths);
        return new AvfxDecodeInputs(textureIndexCandidates, cardTexturePaths);
    }

    private static IReadOnlyList<CardTexturePathMatch> BuildCardTexturePathMatches(
        HigherLowerCardVfxCatalog.CardCatalog catalog,
        IReadOnlyList<string> texturePaths)
    {
        var result = new List<CardTexturePathMatch>();
        for (var i = 0; i < texturePaths.Count; i++)
        {
            if (!HigherLowerCardVfxCatalog.TryGetTexturePathEntry(catalog, texturePaths[i], out var entry))
                continue;

            result.Add(new CardTexturePathMatch(i, entry.TexturePath, FormatPair(entry.Pair)));
        }

        return result;
    }

    private static IReadOnlyList<TxNoCardTextureMatch> BuildTxNoCardTextureMatches(
        HigherLowerCardVfxCatalog.CardCatalog catalog,
        IReadOnlyList<TextureIndexCandidate> candidates)
    {
        var result = new List<TxNoCardTextureMatch>();
        foreach (var candidate in candidates)
        {
            if (!HigherLowerCardVfxCatalog.TryGetTexturePathEntry(catalog, candidate.TexturePath, out var entry))
                continue;

            result.Add(new TxNoCardTextureMatch(
                candidate,
                new CardTexturePathMatch(candidate.Value, entry.TexturePath, FormatPair(entry.Pair))));
        }

        return result;
    }

    private static IReadOnlyList<CardTexturePathMatch> DeduplicateCardTexturePaths(IReadOnlyList<CardTexturePathMatch> matches)
        => matches
            .GroupBy(static x => x.TexturePath, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.OrderBy(static x => x.TextureIndex).First())
            .OrderBy(static x => x.TextureIndex)
            .ToList();

    private static string FormatPair(HigherLowerCardVfxCatalog.CardPair pair)
        => pair.Left == pair.Right
            ? pair.Left.ToString(CultureInfo.InvariantCulture)
            : $"{pair.Left},{pair.Right}";

    private static string NormalizeSource(string source)
        => string.IsNullOrWhiteSpace(source) ? AvfxTexturePathSource : source;

    private static string FormatCardTexturePaths(IReadOnlyList<CardTexturePathMatch> cardTexturePaths)
    {
        if (cardTexturePaths.Count == 0)
            return "none";

        return string.Join(
            ";",
            cardTexturePaths.Select(static x => $"Tex[{x.TextureIndex}]={x.TexturePath}:{x.Pair}"));
    }

    private AvfxMetadataCacheEntry GetAvfxMetadata(string normalizedPath)
    {
        lock (gate)
        {
            if (avfxMetadataCache.TryGetValue(normalizedPath, out var cached))
                return cached;
        }

        AvfxMetadataCacheEntry entry;
        try
        {
            if (!dataManager.FileExists(normalizedPath))
            {
                entry = AvfxMetadataCacheEntry.Failed("avfx-file-not-found", "file not found in game data");
            }
            else
            {
                var file = dataManager.GetFile(normalizedPath);
                if (file == null)
                {
                    entry = AvfxMetadataCacheEntry.Failed("avfx-load-failed", "IDataManager.GetFile returned null");
                }
                else if (!HigherLowerAvfxParser.TryParse(file.Data, out var metadata, out var error))
                {
                    entry = AvfxMetadataCacheEntry.Failed("avfx-parse-failed", error);
                }
                else
                {
                    entry = AvfxMetadataCacheEntry.Success(metadata);
                }
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"[ADS][HLSOLVER] Failed to load AVFX metadata for {normalizedPath}.");
            entry = AvfxMetadataCacheEntry.Failed("avfx-load-failed", ex.Message);
        }

        if (!string.IsNullOrWhiteSpace(entry.Detail) && entry.Metadata == null)
            log.Warning($"[ADS][HLSOLVER] AVFX metadata unavailable for {normalizedPath}: {entry.FailureReason}: {entry.Detail}");

        lock (gate)
        {
            avfxMetadataCache[normalizedPath] = entry;
        }

        return entry;
    }

    private static string ResolveSlot(Vector3 position, IReadOnlyList<TreasureHighLowDiagnosticService.BoardSlotSnapshot> slots)
    {
        if (position == Vector3.Zero || slots.Count == 0)
            return "unknown";

        var nearest = slots
            .Select(slot => new
            {
                Slot = slot,
                Distance = Vector3.Distance(slot.Position, position),
            })
            .OrderBy(static x => x.Distance)
            .FirstOrDefault();

        if (nearest == null || nearest.Distance > 8f)
            return "unknown";

        var partner = slots
            .Where(slot => slot.GameObjectId != nearest.Slot.GameObjectId)
            .Select(slot => new
            {
                Slot = slot,
                Distance = Vector3.Distance(slot.Position, nearest.Slot.Position),
                YDelta = Math.Abs(slot.Position.Y - nearest.Slot.Position.Y),
                ZDelta = Math.Abs(slot.Position.Z - nearest.Slot.Position.Z),
            })
            .Where(static x => x.Distance <= 18f && x.YDelta <= 8f && x.ZDelta <= 12f)
            .OrderBy(static x => x.Distance)
            .FirstOrDefault();

        if (partner == null || Math.Abs(nearest.Slot.Position.X - partner.Slot.Position.X) < 0.05f)
            return "unknown";

        return nearest.Slot.Position.X < partner.Slot.Position.X ? "left" : "right";
    }

    private static string FormatSlotCandidates(
        Vector3 probePosition,
        IReadOnlyList<TreasureHighLowDiagnosticService.BoardSlotSnapshot> slots)
    {
        if (slots.Count == 0)
            return "none";

        return string.Join(
            ";",
            slots.Take(12).Select((slot, index) =>
            {
                var distance = probePosition == Vector3.Zero
                    ? "n/a"
                    : Vector3.Distance(slot.Position, probePosition).ToString("0.00", CultureInfo.InvariantCulture);
                return $"#{index}:go=0x{slot.GameObjectId:X},base={slot.BaseId},layout={slot.LayoutId},pos={FormatPosition(slot.Position)},dist={distance}";
            }));
    }

    private static string BuildPointerKey(nint pointer)
        => $"ptr:{pointer:X}";

    private static string BuildPositionKey(string normalizedPath, Vector3 position)
        => string.Create(CultureInfo.InvariantCulture, $"pos:{normalizedPath}:{position.X:0.0}:{position.Y:0.0}:{position.Z:0.0}");

    private static bool PathsMatch(string left, string right)
        => string.Equals(HigherLowerCardVfxCatalog.NormalizePath(left), HigherLowerCardVfxCatalog.NormalizePath(right), StringComparison.OrdinalIgnoreCase);

    private void PruneRecentProbes(DateTime nowUtc)
    {
        foreach (var key in recentProbes
                     .Where(x => nowUtc - x.Value.TimestampUtc > ActiveProbeTtl)
                     .Select(static x => x.Key)
                     .ToList())
        {
            recentProbes.Remove(key);
        }

        foreach (var key in loggedProbeKeys
                     .Where(x => nowUtc - x.Value > TimeSpan.FromSeconds(30))
                     .Select(static x => x.Key)
                     .ToList())
        {
            loggedProbeKeys.Remove(key);
        }
    }

    private sealed record AvfxMetadataCacheEntry(
        HigherLowerAvfxMetadata? Metadata,
        string FailureReason,
        string Detail)
    {
        public static AvfxMetadataCacheEntry Success(HigherLowerAvfxMetadata metadata)
            => new(metadata, string.Empty, string.Empty);

        public static AvfxMetadataCacheEntry Failed(string failureReason, string detail)
            => new(null, failureReason, detail);
    }

    private sealed record AvfxDecodeInputs(
        IReadOnlyList<TextureIndexCandidate> TextureIndexCandidates,
        IReadOnlyList<CardTexturePathMatch> CardTexturePaths)
    {
        public static AvfxDecodeInputs Empty { get; } = new([], []);
    }

    private readonly record struct TxNoCardTextureMatch(
        TextureIndexCandidate Candidate,
        CardTexturePathMatch CardTexturePath);

    public enum SolverConfidence
    {
        Blocked,
        Low,
        High,
    }

    public sealed record SolverState(
        bool Active,
        int? CurrentCard,
        string RecommendedChoice,
        SolverConfidence Confidence,
        string Reason,
        string CardSource,
        string Slot,
        int? TextureIndex,
        string TextureIndexSource)
    {
        public static SolverState Inactive { get; } = new(
            Active: false,
            CurrentCard: null,
            RecommendedChoice: "Blocked",
            Confidence: SolverConfidence.Blocked,
            Reason: "inactive",
            CardSource: "none",
            Slot: "unknown",
            TextureIndex: null,
            TextureIndexSource: "none");
    }

    public sealed record CardProbeRow(
        DateTime TimestampUtc,
        string Path,
        string NormalizedPath,
        nint Pointer,
        string Slot,
        Vector3 Position,
        uint TerritoryId,
        string CardSource,
        int? TextureIndex,
        IReadOnlyList<TextureIndexCandidate> TextureIndexCandidates,
        IReadOnlyList<CardTexturePathMatch> CardTexturePaths,
        string SlotCandidates,
        string Pair,
        int? DecodedCard,
        SolverConfidence Confidence,
        string Reason)
    {
        public bool IsCatalogCard
            => !string.IsNullOrWhiteSpace(NormalizedPath)
               && !string.Equals(Reason, "no-card-catalog", StringComparison.OrdinalIgnoreCase);

        public static CardProbeRow Blocked(
            DateTime timestampUtc,
            string path,
            nint pointer,
            string slot,
            Vector3 position,
            uint territoryId,
            string cardSource,
            string slotCandidates,
            string reason)
        {
            var normalizedPath = HigherLowerCardVfxCatalog.NormalizePath(path);
            return new CardProbeRow(
                timestampUtc,
                path,
                normalizedPath,
                pointer,
                slot,
                position,
                territoryId,
                cardSource,
                null,
                [],
                [],
                slotCandidates,
                string.Empty,
                null,
                SolverConfidence.Blocked,
                reason);
        }

        public string ToHldbgLogLine(string knownBoardFields)
        {
            var cardTexturePaths = Escape(FormatCardTexturePaths(CardTexturePaths));
            var slotCandidatesText = string.Equals(Reason, "no-card-slot", StringComparison.OrdinalIgnoreCase)
                ? $" slotCandidates=[{Escape(SlotCandidates)}]"
                : string.Empty;

            if (Confidence == SolverConfidence.High && DecodedCard.HasValue && TextureIndex.HasValue)
            {
                return $"vfx-card-probe path='{Escape(Path)}' ptr=0x{Pointer:X} slot={Slot} textureIndex={TextureIndex.Value} textureIndexSource={Escape(CardSource)} textureCandidates=[{Escape(FormatCandidates(TextureIndexCandidates))}] cardTexturePaths=[{cardTexturePaths}] pair='{Escape(Pair)}' decodedCard={DecodedCard.Value} pos={FormatPosition(Position)} confidence=high source='{Escape(CardSource)}' {knownBoardFields}";
            }

            return $"vfx-card-probe blocked reason='{Escape(Reason)}' path='{Escape(Path)}' ptr=0x{Pointer:X} slot={Slot} textureIndex=unknown textureIndexSource={Escape(CardSource)} textureCandidates=[{Escape(FormatCandidates(TextureIndexCandidates))}] cardTexturePaths=[{cardTexturePaths}]{slotCandidatesText} pos={FormatPosition(Position)} source='{Escape(CardSource)}' {knownBoardFields}";
        }
    }

    public readonly record struct CardTexturePathMatch(int TextureIndex, string TexturePath, string Pair);

    public readonly record struct TextureIndexCandidate(int Offset, int Width, int Value, string Source = "", string TexturePath = "")
    {
        public string Key
            => $"{Offset}:{Width}";

        public string OffsetText
            => $"0x{Offset:X}";

        public override string ToString()
        {
            var source = string.IsNullOrWhiteSpace(Source) ? $"u{Width * 8}+0x{Offset:X}" : Source;
            return string.IsNullOrWhiteSpace(TexturePath)
                ? $"{source}={Value}"
                : $"{source}={Value}:{TexturePath}";
        }
    }
}

public static class HigherLowerCardVfxCatalog
{
    private static readonly Dictionary<string, CardCatalog> Catalogs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bgcommon/world/common/vfx_for_bg/eff/b1231card1_o.avfx"] = BuildCatalog(
            "bgcommon/world/common/vfx_for_bg/eff/b1231card1_o.avfx",
            new CardTextureEntry(1, "bgcommon/world/common/vfx_for_bg/texture/card002_o.atex", new CardPair(1, 2)),
            new CardTextureEntry(7, "bgcommon/world/common/vfx_for_bg/texture/card003_o.atex", new CardPair(3, 4)),
            new CardTextureEntry(8, "bgcommon/world/common/vfx_for_bg/texture/card004_o.atex", new CardPair(5, 6)),
            new CardTextureEntry(9, "bgcommon/world/common/vfx_for_bg/texture/card005_o.atex", new CardPair(7, 8)),
            new CardTextureEntry(10, "bgcommon/world/common/vfx_for_bg/texture/card006_o.atex", new CardPair(9, 9))),
        ["bgcommon/world/common/vfx_for_bg/eff/b1659card1_o.avfx"] = BuildLinearCatalog(
            "bgcommon/world/common/vfx_for_bg/eff/b1659card1_o.avfx",
            200),
        ["bgcommon/world/common/vfx_for_bg/eff/b2502card1_o.avfx"] = BuildLinearCatalog(
            "bgcommon/world/common/vfx_for_bg/eff/b2502card1_o.avfx",
            300),
        ["bgcommon/world/common/vfx_for_bg/eff/b2919card1_o.avfx"] = BuildLinearCatalog(
            "bgcommon/world/common/vfx_for_bg/eff/b2919card1_o.avfx",
            400),
    };

    public static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        return path.Replace('\\', '/').Trim().Trim('\'', '"').ToLowerInvariant();
    }

    public static bool IsEffectOnly(string normalizedPath)
    {
        normalizedPath = NormalizePath(normalizedPath);
        var fileName = Path.GetFileName(normalizedPath);
        return fileName.Contains("card2", StringComparison.OrdinalIgnoreCase)
               || fileName.Contains("card3", StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryGetCatalog(string normalizedPath, out CardCatalog catalog)
    {
        normalizedPath = NormalizePath(normalizedPath);
        return Catalogs.TryGetValue(normalizedPath, out catalog!);
    }

    public static bool TryDecode(string normalizedPath, int textureIndex, string slot, out int card, out string pairText)
    {
        card = 0;
        pairText = string.Empty;
        if (!TryGetCatalog(normalizedPath, out var catalog)
            || !catalog.TryGetByTextureIndex(textureIndex, out var entry))
        {
            return false;
        }

        return TryDecode(entry, slot, out card, out pairText);
    }

    public static bool TryDecode(string normalizedPath, string texturePath, string slot, out int card, out string pairText)
    {
        card = 0;
        pairText = string.Empty;
        if (!TryGetCatalog(normalizedPath, out var catalog)
            || !catalog.TryGetByTexturePath(texturePath, out var entry))
        {
            return false;
        }

        return TryDecode(entry, slot, out card, out pairText);
    }

    public static bool TryGetTexturePathEntry(CardCatalog catalog, string texturePath, out CardTextureEntry entry)
        => catalog.TryGetByTexturePath(texturePath, out entry);

    private static bool TryDecode(CardTextureEntry entry, string slot, out int card, out string pairText)
    {
        pairText = entry.Pair.Left == entry.Pair.Right
            ? entry.Pair.Left.ToString(CultureInfo.InvariantCulture)
            : $"{entry.Pair.Left},{entry.Pair.Right}";
        card = string.Equals(slot, "right", StringComparison.OrdinalIgnoreCase) ? entry.Pair.Right : entry.Pair.Left;
        return true;
    }

    private static CardCatalog BuildLinearCatalog(string path, int texturePrefix)
        => BuildCatalog(
            path,
            new CardTextureEntry(1, $"bgcommon/world/common/vfx_for_bg/texture/card{texturePrefix + 2:000}_o.atex", new CardPair(1, 2)),
            new CardTextureEntry(2, $"bgcommon/world/common/vfx_for_bg/texture/card{texturePrefix + 3:000}_o.atex", new CardPair(3, 4)),
            new CardTextureEntry(3, $"bgcommon/world/common/vfx_for_bg/texture/card{texturePrefix + 4:000}_o.atex", new CardPair(5, 6)),
            new CardTextureEntry(4, $"bgcommon/world/common/vfx_for_bg/texture/card{texturePrefix + 5:000}_o.atex", new CardPair(7, 8)),
            new CardTextureEntry(5, $"bgcommon/world/common/vfx_for_bg/texture/card{texturePrefix + 6:000}_o.atex", new CardPair(9, 9)));

    private static CardCatalog BuildCatalog(string path, params CardTextureEntry[] entries)
        => new(path, entries);

    public sealed class CardCatalog
    {
        private readonly Dictionary<int, CardTextureEntry> entriesByTextureIndex;
        private readonly Dictionary<string, CardTextureEntry> entriesByTexturePath;

        public CardCatalog(string path, IEnumerable<CardTextureEntry> entries)
        {
            Path = NormalizePath(path);
            Entries = entries
                .Select(static x => new CardTextureEntry(x.TextureIndex, NormalizePath(x.TexturePath), x.Pair))
                .ToList();
            entriesByTextureIndex = Entries.ToDictionary(static x => x.TextureIndex);
            entriesByTexturePath = Entries.ToDictionary(static x => x.TexturePath, StringComparer.OrdinalIgnoreCase);
        }

        public string Path { get; }

        public IReadOnlyList<CardTextureEntry> Entries { get; }

        public bool TryGetByTextureIndex(int textureIndex, out CardTextureEntry entry)
            => entriesByTextureIndex.TryGetValue(textureIndex, out entry);

        public bool TryGetByTexturePath(string texturePath, out CardTextureEntry entry)
            => entriesByTexturePath.TryGetValue(NormalizePath(texturePath), out entry);
    }

    public readonly record struct CardTextureEntry(int TextureIndex, string TexturePath, CardPair Pair);

    public readonly record struct CardPair(int Left, int Right);
}
