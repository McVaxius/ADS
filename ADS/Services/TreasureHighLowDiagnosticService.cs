using System.Globalization;
using System.Diagnostics;
using System.Numerics;
using System.Text;
using System.Text.Json;
using ADS.Models;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace ADS.Services;

public sealed class TreasureHighLowDiagnosticService : IDisposable
{
    public const double DefaultTraceSeconds = 8;
    public const double MaxTraceSeconds = 60;

    private static string Prefix => LogPrefix;
    public static string LogPrefix
    {
        get
        {
            var now = DateTimeOffset.UtcNow;
            return string.Create(
                CultureInfo.InvariantCulture,
                $"[ADS][HLDBG] tsUtc={now.UtcDateTime:O} unixMs={now.ToUnixTimeMilliseconds()}");
        }
    }

    private const string AddonName = "TreasureHighLow";
    private const int MaxTreeNodes = 260;
    private const int MaxTreeDepth = 14;
    private const int MaxAtkValues = 512;
    private const int MaxGetNodeById = 51100;
    private const long MaxLogBytes = 64L * 1024L * 1024L;
    private const int MaxLogFiles = 10;
    private const float NearbyRadius = 80f;
    private const int MaxExportImageCandidates = 160;
    private const int MaxStateProbeBytes = 4096;
    private const int MaxStateNeedleTokens = 48;
    private const int MaxTraceVfxRows = 600;
    private const string TextureExportScriptName = "hldbg_tex_export.py";
    private static readonly TimeSpan TraceSampleInterval = TimeSpan.FromMilliseconds(100);

    private readonly IGameGui gameGui;
    private readonly IObjectTable objectTable;
    private readonly IClientState clientState;
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;
    private readonly Configuration configuration;
    private readonly string diagnosticDirectory;
    private readonly string datamineDirectory;
    private readonly string cardMapPath;

    private string lastSignature = string.Empty;
    private string lastAddonSignature = string.Empty;
    private string lastWorldSignature = string.Empty;
    private bool lastAddonVisible;
    private bool lastHighLowTargetable;
    private DateTime lastSnapshotUtc = DateTime.MinValue;
    private bool forceNextSnapshot;
    private bool forceNextStateProbe;
    private StreamWriter? writer;
    private StreamWriter? datamineLogWriter;
    private StreamWriter? datamineJsonlWriter;
    private string? currentLogPath;
    private string? currentDatamineSessionDirectory;
    private string? currentDatamineLogPath;
    private string? currentDatamineJsonlPath;
    private uint currentDatamineTerritoryId;
    private ulong datamineSequence;
    private KnownCardTag? knownCardTag;
    private KnownBoardTag? knownBoardTag;
    private int knownCardSequence;
    private int knownBoardSequence;
    private bool fileErrorLogged;
    private bool datamineFileErrorLogged;
    private string lastDatamineSurfaceKey = string.Empty;
    private DateTime lastDatamineSurfaceUtc = DateTime.MinValue;
    private DateTime datamineSessionLastSignalUtc = DateTime.MinValue;
    private DateTime datamineSessionGraceUntilUtc = DateTime.MinValue;
    private string datamineSessionLastSignalSource = "none";
    private string lastDatamineSignalLogKey = string.Empty;
    private DateTime lastDatamineSignalLogUtc = DateTime.MinValue;
    private readonly Dictionary<string, DateTime> datamineTrackedVfxLogUtc = new(StringComparer.Ordinal);
    private HigherLowerCardMap cardMap = new();
    private bool cardMapLoaded;
    private bool cardMapDirty;
    private DateTime traceStartedUtc = DateTime.MinValue;
    private DateTime traceUntilUtc = DateTime.MinValue;
    private DateTime lastTraceSampleUtc = DateTime.MinValue;
    private int traceSequence;
    private int activeTraceSequence;
    private int traceSampleSequence;
    private bool traceNeedsBaseline;
    private int traceVfxRowsWritten;
    private int traceVfxRowsDropped;
    private bool traceVfxTruncationLogged;
    private DateTime lastHigherLowerSignalUtc = DateTime.MinValue;
    private string lastHigherLowerSignalSource = "none";
    private static readonly TimeSpan DatamineSessionGrace = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DatamineSignalLogCooldown = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DatamineSurfaceCooldown = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan DatamineTrackedVfxCooldown = TimeSpan.FromSeconds(1);
    private const int KnownCardTagMaxSnapshots = 6;
    private static readonly TimeSpan KnownCardTagTtl = TimeSpan.FromSeconds(15);
    private const int KnownBoardTagMaxSnapshots = 12;
    private static readonly TimeSpan KnownBoardTagTtl = TimeSpan.FromSeconds(30);

    public TreasureHighLowDiagnosticService(
        IGameGui gameGui,
        IObjectTable objectTable,
        IClientState clientState,
        IDataManager dataManager,
        IPluginLog log,
        Configuration configuration,
        string configDirectory)
    {
        this.gameGui = gameGui;
        this.objectTable = objectTable;
        this.clientState = clientState;
        this.dataManager = dataManager;
        this.log = log;
        this.configuration = configuration;
        diagnosticDirectory = Path.Combine(configDirectory, "HigherLowerDiagnostics");
        datamineDirectory = Path.Combine(diagnosticDirectory, "Datamine");
        cardMapPath = Path.Combine(configDirectory, "higher-lower-card-map.json");
    }

    public bool Enabled
        => configuration.HigherLowerDiagnosticsEnabled;

    public bool VfxDataminingEnabled
        => configuration.HigherLowerVfxDataminingEnabled;

    public string DiagnosticDirectory
        => diagnosticDirectory;

    public string DatamineDirectory
        => datamineDirectory;

    public string CurrentLogPath
        => currentLogPath ?? string.Empty;

    public string CurrentDatamineSessionDirectory
        => currentDatamineSessionDirectory ?? string.Empty;

    public string CardMapPath
        => cardMapPath;

    public DateTime LastHigherLowerSignalUtc
        => lastHigherLowerSignalUtc;

    public string LastHigherLowerSignalSource
        => lastHigherLowerSignalSource;

    public int CardMapCount
    {
        get
        {
            EnsureCardMapLoaded();
            return cardMap.GraphicToCard.Count;
        }
    }

    public HigherLowerLiveProbe CaptureLiveProbe()
    {
        var selectYesnoVisible = IsAddonVisible("SelectYesno");
        var selectYesnoPrompt = ReadSelectYesnoPrompt();
        var treasureHighLowVisible = IsAddonVisible(AddonName);
        var notificationChallengeVisible = IsAddonVisible("_NotificationChallenge");
        var addonCardDecode = CaptureAddonCardDecode();
        var objects = CaptureWorldObjects();
        var candidates = ResolveBoardCandidates(objects)
            .Select((candidate, index) => ToBoardCandidate(index == 0 ? "left" : "right", candidate))
            .ToList();
        var currentCandidate = candidates.FirstOrDefault();
        var currentGraphicKey = currentCandidate?.GraphicKey ?? string.Empty;
        var currentCard = ResolveCard(currentGraphicKey);
        var active = treasureHighLowVisible
            || notificationChallengeVisible
            || objects.Any(static x => x.IsHighLow)
            || (selectYesnoVisible && IsHigherLowerPrompt(selectYesnoPrompt));
        var warnings = BuildSafetyWarnings(candidates).ToList();
        var visualCardSourceSafe = currentCard.HasValue
                                   && IsAuthoritativeCardFaceKey(currentGraphicKey)
                                   && warnings.All(static x => !x.StartsWith("blocked:", StringComparison.OrdinalIgnoreCase));

        EnsureCardMapLoaded();
        var entries = cardMap.GraphicToCard
            .OrderBy(static x => x.Value)
            .ThenBy(static x => x.Key, StringComparer.Ordinal)
            .Select(x => new HigherLowerCardMapEntry(
                GraphicKey: x.Key,
                Card: x.Value,
                Source: cardMap.Sources.GetValueOrDefault(x.Key, string.Empty),
                Unsafe: IsUnsafeGraphicKey(x.Key)))
            .ToList();

        return new HigherLowerLiveProbe(
            Runtime: new HigherLowerRuntimeState(
                Active: active,
                TreasureHighLowVisible: treasureHighLowVisible,
                NotificationChallengeVisible: notificationChallengeVisible,
                SelectYesnoVisible: selectYesnoVisible,
                SelectYesnoPrompt: selectYesnoPrompt,
                HighTargetable: objects.Any(static x => x.IsHighLow && x.Targetable && string.Equals(x.Name, "High", StringComparison.OrdinalIgnoreCase)),
                LowTargetable: objects.Any(static x => x.IsHighLow && x.Targetable && string.Equals(x.Name, "Low", StringComparison.OrdinalIgnoreCase)),
                AddonCurrentCard: addonCardDecode.CurrentCard,
                AddonCurrentCardText: addonCardDecode.CurrentCardText,
                AddonOtherCard: addonCardDecode.OtherCard,
                AddonOtherCardText: addonCardDecode.OtherCardText,
                AddonCurrentCardSource: addonCardDecode.CurrentCardSource,
                CurrentGraphicKey: currentGraphicKey,
                CurrentCard: currentCard,
                KnownCardCount: entries.Count,
                CardSourceSafe: visualCardSourceSafe,
                SafetyStatus: visualCardSourceSafe ? "ready: visual card-face key" : "blocked: addon card-face key not proven"),
            BoardCandidates: candidates,
            CardMapEntries: entries,
            SafetyWarnings: warnings,
            DiagnosticDirectory: diagnosticDirectory,
            CurrentLogPath: CurrentLogPath,
            DatamineDirectory: datamineDirectory,
            CurrentDatamineSessionDirectory: CurrentDatamineSessionDirectory,
            VfxDataminingEnabled: VfxDataminingEnabled,
            CardMapPath: cardMapPath);
    }

    public unsafe IReadOnlyList<BoardSlotSnapshot> CaptureBoardSlots()
    {
        var objects = CaptureWorldObjects();
        var candidates = ResolveLiveCardSlotCandidates(objects).ToList();
        var result = new List<BoardSlotSnapshot>(candidates.Count);
        for (var i = 0; i < candidates.Count; i++)
        {
            if (!TryParsePosition(candidates[i].Position, out var position))
                continue;

            result.Add(new BoardSlotSnapshot(
                Side: "unknown",
                Position: position,
                GameObjectId: candidates[i].GameObjectId,
                BaseId: candidates[i].BaseId,
                LayoutId: candidates[i].LayoutId));
        }

        return result;
    }

    public KnownBoardSnapshot? PeekKnownBoardForSolver()
    {
        var tag = PeekKnownBoardTag(DateTime.UtcNow);
        return tag == null
            ? null
            : new KnownBoardSnapshot(tag.LeftCard, tag.RightCard, tag.Label, tag.Sequence, tag.CreatedUtc);
    }

    public void SetEnabled(bool enabled)
    {
        if (configuration.HigherLowerDiagnosticsEnabled == enabled)
        {
            log.Information($"{Prefix} diagnostics already {(enabled ? "enabled" : "disabled")} path='{currentLogPath ?? string.Empty}'");
            return;
        }

        configuration.HigherLowerDiagnosticsEnabled = enabled;
        configuration.Save();
        if (enabled)
        {
            log.Information($"{Prefix} diagnostics enabled.");
            EnsureWriter(clientState.TerritoryType);
        }
        else
        {
            CloseWriter();
            ResetRuntimeState();
            log.Information($"{Prefix} diagnostics disabled.");
        }
    }

    public void SetVfxDataminingEnabled(bool enabled)
    {
        if (configuration.HigherLowerVfxDataminingEnabled == enabled)
        {
            log.Information($"{Prefix} vfx datamining already {(enabled ? "enabled" : "disabled")} path='{currentDatamineSessionDirectory ?? string.Empty}'");
            return;
        }

        configuration.HigherLowerVfxDataminingEnabled = enabled;
        configuration.Save();
        if (enabled)
        {
            log.Information($"{Prefix} vfx datamining enabled.");
            return;
        }

        CloseDatamineWritersIfOpen();
        ResetDatamineSessionGate();
        ResetDatamineCooldown();
        log.Information($"{Prefix} vfx datamining disabled.");
    }

    public void ForceDump()
    {
        forceNextSnapshot = true;
        log.Information($"{Prefix} force dump queued.");
    }

    public void ForceStateProbe()
    {
        forceNextStateProbe = true;
        forceNextSnapshot = true;
        log.Information($"{Prefix} focused state probe queued.");
    }

    public HigherLowerTraceResult StartTrace(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds <= 0)
        {
            return new HigherLowerTraceResult(
                false,
                $"Higher/Lower trace seconds must be > 0 and <= {MaxTraceSeconds.ToString("0.###", CultureInfo.InvariantCulture)}.",
                CurrentLogPath,
                0);
        }

        var clampedSeconds = Math.Min(seconds, MaxTraceSeconds);
        var duration = TimeSpan.FromSeconds(clampedSeconds);
        if (!EnsureWriter(clientState.TerritoryType))
        {
            return new HigherLowerTraceResult(
                false,
                "Higher/Lower trace failed: diagnostic log file could not be opened.",
                CurrentLogPath,
                clampedSeconds);
        }

        var now = DateTime.UtcNow;
        traceSequence++;
        activeTraceSequence = traceSequence;
        traceSampleSequence = 0;
        traceStartedUtc = now;
        traceUntilUtc = now + duration;
        lastTraceSampleUtc = DateTime.MinValue;
        traceNeedsBaseline = true;
        traceVfxRowsWritten = 0;
        traceVfxRowsDropped = 0;
        traceVfxTruncationLogged = false;

        WriteLine(
            $"{Prefix} hldbg-trace start seq={activeTraceSequence} durationMs={(int)duration.TotalMilliseconds} " +
            $"sampleIntervalMs={(int)TraceSampleInterval.TotalMilliseconds} maxVfxRows={MaxTraceVfxRows} territory={clientState.TerritoryType}");
        log.Information($"{Prefix} trace started seconds={clampedSeconds.ToString("0.###", CultureInfo.InvariantCulture)} path='{CurrentLogPath}'");

        return new HigherLowerTraceResult(
            true,
            $"Higher/Lower trace started for {clampedSeconds.ToString("0.###", CultureInfo.InvariantCulture)}s; file={CurrentLogPath}",
            CurrentLogPath,
            clampedSeconds);
    }

    public void RecordServerEvent(HigherLowerServerEventTraceService.ServerEventRow row)
    {
        if (row.HigherLowerRelevant)
            MarkHigherLowerSignal(row.TimestampUtc, $"server:{row.Kind}");
        RecordDatamineServerEvent(row);

        var now = DateTime.UtcNow;
        FinishTraceIfExpired(now);
        if (!configuration.HigherLowerDiagnosticsEnabled && !IsTraceActive(now))
            return;

        var territoryId = row.TerritoryId != 0 ? row.TerritoryId : clientState.TerritoryType;
        if (!EnsureWriter(territoryId))
            return;

        WriteLine($"{Prefix} {row.ToBossModLogLine()}");
        FlushWriter();
    }

    public void RecordVfxEvent(HigherLowerVfxTraceService.VfxEventRow row)
    {
        if (row.HigherLowerRelevant)
            MarkHigherLowerSignal(row.TimestampUtc, $"vfx:{row.Kind}");

        if (row.HigherLowerRelevant || !string.IsNullOrWhiteSpace(row.CardSource))
            RecordDatamineVfxEvent(row);

        var now = DateTime.UtcNow;
        FinishTraceIfExpired(now);
        var traceActive = IsTraceActive(now);
        if (!configuration.HigherLowerDiagnosticsEnabled && !traceActive)
            return;

        if (traceActive)
        {
            if (row.TimestampUtc < traceStartedUtc || row.TimestampUtc >= traceUntilUtc)
                return;

            if (!IsCurrentTerritory(row))
                return;

            if (!EnsureWriter(row.TerritoryId != 0 ? row.TerritoryId : clientState.TerritoryType))
                return;

            if (traceVfxRowsWritten >= MaxTraceVfxRows)
            {
                traceVfxRowsDropped++;
                if (!traceVfxTruncationLogged)
                {
                    traceVfxTruncationLogged = true;
                    WriteLine($"{Prefix} vfx truncated seq={activeTraceSequence} reason=max-trace-vfx-rows cap={MaxTraceVfxRows}");
                    FlushWriter();
                }

                return;
            }

            traceVfxRowsWritten++;
            WriteLine($"{Prefix} {row.ToHldbgLogLine()}");
            FlushWriter();
            return;
        }

        if (!row.HigherLowerRelevant)
            return;

        if (!EnsureWriter(row.TerritoryId != 0 ? row.TerritoryId : clientState.TerritoryType))
            return;

        WriteLine($"{Prefix} {row.ToHldbgLogLine()}");
        FlushWriter();
    }

    public void RecordSolverLine(uint territoryId, string line)
    {
        var now = DateTime.UtcNow;
        FinishTraceIfExpired(now);
        var traceActive = IsTraceActive(now);
        if (!configuration.HigherLowerDiagnosticsEnabled && !traceActive)
            return;

        if (!EnsureWriter(territoryId != 0 ? territoryId : clientState.TerritoryType))
            return;

        WriteLine($"{Prefix} {line}");
        FlushWriter();
    }

    public void RecordDatamineSolverState(
        uint territoryId,
        HigherLowerRuntimeState runtime,
        HigherLowerCardVfxSolverService.SolverState state)
    {
        if (!runtime.Active || !ShouldRecordDatamine())
            return;

        territoryId = ResolveDatamineTerritory(territoryId);
        if (!EnsureDatamineWriters(territoryId))
            return;

        var addonCurrentCard = runtime.AddonCurrentCard?.ToString(CultureInfo.InvariantCulture) ?? runtime.AddonCurrentCardText;
        var addonOtherCard = runtime.AddonOtherCard?.ToString(CultureInfo.InvariantCulture) ?? runtime.AddonOtherCardText;
        var decodedCard = state.CurrentCard?.ToString(CultureInfo.InvariantCulture) ?? "unknown";
        WriteDatamineLogLine(
            $"{Prefix} datamine-solver addonCurrentCard={EscapeToken(addonCurrentCard)} addonOtherCard={EscapeToken(addonOtherCard)} " +
            $"graphicKey='{Escape(runtime.CurrentGraphicKey)}' visualCard={(runtime.CurrentCard?.ToString(CultureInfo.InvariantCulture) ?? "unknown")} " +
            $"decodedCard={decodedCard} action={EscapeToken(state.RecommendedChoice)} confidence={state.Confidence.ToString().ToLowerInvariant()} " +
            $"source='{Escape(state.CardSource)}' slot={EscapeToken(state.Slot)} textureIndex={(state.TextureIndex?.ToString(CultureInfo.InvariantCulture) ?? "unknown")} reason='{Escape(state.Reason)}'");
        WriteDatamineJsonRow(
            territoryId,
            "solver",
            new
            {
                runtime.AddonCurrentCard,
                runtime.AddonCurrentCardText,
                runtime.AddonOtherCard,
                runtime.AddonOtherCardText,
                runtime.AddonCurrentCardSource,
                runtime.CurrentGraphicKey,
                VisualCard = runtime.CurrentCard,
                runtime.CardSourceSafe,
                runtime.SafetyStatus,
                DecodedCard = state.CurrentCard,
                Action = state.RecommendedChoice,
                Confidence = state.Confidence.ToString().ToLowerInvariant(),
                Reason = state.Reason,
                Source = state.CardSource,
                state.Slot,
                state.TextureIndex,
                state.TextureIndexSource,
            });
    }

    public void RecordDatamineSurface(
        uint territoryId,
        HigherLowerRuntimeState runtime,
        HigherLowerCardVfxSolverService.SolverState solverState,
        string surface,
        int step,
        int playsCompleted,
        bool retained,
        int? retainedStep,
        int? retainedCard,
        string retainedAction,
        string retainedSource,
        int? decisionCard,
        string decisionAction,
        string decisionSource,
        string blockedReason)
    {
        if (!runtime.Active || !ShouldRecordDatamine())
            return;

        var key = string.Join(
            "|",
            surface,
            step.ToString(CultureInfo.InvariantCulture),
            playsCompleted.ToString(CultureInfo.InvariantCulture),
            runtime.AddonCurrentCardText,
            runtime.AddonOtherCardText,
            runtime.CurrentGraphicKey,
            runtime.CurrentCard?.ToString(CultureInfo.InvariantCulture) ?? "none",
            solverState.CurrentCard?.ToString(CultureInfo.InvariantCulture) ?? "none",
            solverState.Confidence.ToString(),
            solverState.Reason,
            decisionCard?.ToString(CultureInfo.InvariantCulture) ?? "none",
            decisionAction,
            retained.ToString(),
            retainedCard?.ToString(CultureInfo.InvariantCulture) ?? "none",
            blockedReason);
        var now = DateTime.UtcNow;
        if (key == lastDatamineSurfaceKey && now - lastDatamineSurfaceUtc < DatamineSurfaceCooldown)
            return;

        lastDatamineSurfaceKey = key;
        lastDatamineSurfaceUtc = now;

        territoryId = ResolveDatamineTerritory(territoryId);
        if (!EnsureDatamineWriters(territoryId))
            return;

        WriteDatamineLogLine(
            $"{Prefix} datamine-surface surface={EscapeToken(surface)} step={step} playsCompleted={playsCompleted} " +
            $"addonCurrentCard={EscapeToken(runtime.AddonCurrentCardText)} addonOtherCard={EscapeToken(runtime.AddonOtherCardText)} " +
            $"graphicKey='{Escape(runtime.CurrentGraphicKey)}' visualCard={(runtime.CurrentCard?.ToString(CultureInfo.InvariantCulture) ?? "unknown")} " +
            $"decodedCard={(solverState.CurrentCard?.ToString(CultureInfo.InvariantCulture) ?? "unknown")} decision={EscapeToken(decisionAction)} " +
            $"decisionCard={(decisionCard?.ToString(CultureInfo.InvariantCulture) ?? "unknown")} retained={retained.ToString().ToLowerInvariant()} " +
            $"retainedCard={(retainedCard?.ToString(CultureInfo.InvariantCulture) ?? "unknown")} blockedReason='{Escape(blockedReason)}'");
        WriteDatamineJsonRow(
            territoryId,
            "surface",
            new
            {
                Surface = surface,
                Step = step,
                PlaysCompleted = playsCompleted,
                runtime.TreasureHighLowVisible,
                runtime.NotificationChallengeVisible,
                runtime.SelectYesnoVisible,
                runtime.SelectYesnoPrompt,
                runtime.HighTargetable,
                runtime.LowTargetable,
                runtime.AddonCurrentCard,
                runtime.AddonCurrentCardText,
                runtime.AddonOtherCard,
                runtime.AddonOtherCardText,
                runtime.AddonCurrentCardSource,
                runtime.CurrentGraphicKey,
                VisualCard = runtime.CurrentCard,
                runtime.CardSourceSafe,
                runtime.SafetyStatus,
                DecodedCard = solverState.CurrentCard,
                SolverAction = solverState.RecommendedChoice,
                SolverConfidence = solverState.Confidence.ToString().ToLowerInvariant(),
                SolverReason = solverState.Reason,
                SolverSource = solverState.CardSource,
                solverState.Slot,
                solverState.TextureIndex,
                solverState.TextureIndexSource,
                DecisionCard = decisionCard,
                DecisionAction = decisionAction,
                DecisionSource = decisionSource,
                Retained = retained,
                RetainedStep = retainedStep,
                RetainedCard = retainedCard,
                RetainedAction = retainedAction,
                RetainedSource = retainedSource,
                BlockedReason = blockedReason,
            });
    }

    public void RecordDatamineCardProbe(HigherLowerCardVfxSolverService.CardProbeRow probe)
    {
        if (string.IsNullOrWhiteSpace(probe.Path) || !IsKnownCardAvfxPath(probe.Path, probe.NormalizedPath))
            return;

        TouchDatamineSession(DateTime.UtcNow, $"vfx-card:{probe.NormalizedPath}", probe.TerritoryId, allowStart: false);
        if (!ShouldRecordDatamine())
            return;

        var territoryId = ResolveDatamineTerritory(probe.TerritoryId);
        if (!EnsureDatamineWriters(territoryId))
            return;

        var textureCandidates = HigherLowerCardVfxSolverService.FormatCandidates(probe.TextureIndexCandidates);
        var cardTexturePaths = FormatDatamineCardTexturePaths(probe.CardTexturePaths);
        WriteDatamineLogLine(
            $"{Prefix} datamine-card-probe path='{Escape(probe.Path)}' normalizedPath='{Escape(probe.NormalizedPath)}' ptr=0x{probe.Pointer:X} " +
            $"slot={EscapeToken(probe.Slot)} textureIndex={(probe.TextureIndex?.ToString(CultureInfo.InvariantCulture) ?? "unknown")} " +
            $"textureCandidates=[{Escape(textureCandidates)}] cardTexturePaths=[{Escape(cardTexturePaths)}] pair='{Escape(probe.Pair)}' " +
            $"decodedCard={(probe.DecodedCard?.ToString(CultureInfo.InvariantCulture) ?? "unknown")} confidence={probe.Confidence.ToString().ToLowerInvariant()} " +
            $"source='{Escape(probe.CardSource)}' reason='{Escape(probe.Reason)}' pos={HigherLowerCardVfxSolverService.FormatPosition(probe.Position)}");
        WriteDatamineJsonRow(
            territoryId,
            "card_probe",
            new
            {
                probe.Path,
                probe.NormalizedPath,
                Pointer = $"0x{probe.Pointer:X}",
                probe.Slot,
                Position = HigherLowerCardVfxSolverService.FormatPosition(probe.Position),
                probe.CardSource,
                probe.TextureIndex,
                TextureCandidates = probe.TextureIndexCandidates.Select(static x => new
                {
                    x.Offset,
                    x.OffsetText,
                    x.Width,
                    x.Value,
                    x.Source,
                    x.TexturePath,
                }).ToList(),
                CardTexturePaths = probe.CardTexturePaths.Select(static x => new
                {
                    x.TextureIndex,
                    x.TexturePath,
                    x.Pair,
                }).ToList(),
                probe.SlotCandidates,
                probe.Pair,
                probe.DecodedCard,
                Confidence = probe.Confidence.ToString().ToLowerInvariant(),
                probe.Reason,
            });
    }

    public void RecordDatamineTrackedVfx(HigherLowerVfxTraceService.TrackedVfxRow row)
    {
        if (string.IsNullOrWhiteSpace(row.Path) || !IsKnownCardAvfxPath(row.Path, string.Empty))
            return;

        TouchDatamineSession(DateTime.UtcNow, $"tracked-vfx:{HigherLowerCardVfxCatalog.NormalizePath(row.Path)}", row.TerritoryId, allowStart: false);
        if (!ShouldRecordDatamine())
            return;

        var now = DateTime.UtcNow;
        var key = string.Create(
            CultureInfo.InvariantCulture,
            $"{row.VfxId:X}:{HigherLowerCardVfxCatalog.NormalizePath(row.Path)}:{row.HasRun}:{row.PositionText}:{row.TextureIndexText}:{row.DecodedCardText}:{row.SolverReason}");
        if (datamineTrackedVfxLogUtc.TryGetValue(key, out var last) && now - last < DatamineTrackedVfxCooldown)
            return;

        datamineTrackedVfxLogUtc[key] = now;
        PruneDatamineTrackedVfxLog(now);

        var territoryId = ResolveDatamineTerritory(row.TerritoryId);
        if (!EnsureDatamineWriters(territoryId))
            return;

        WriteDatamineLogLine(
            $"{Prefix} datamine-tracked-vfx path='{Escape(row.Path)}' vfxId=0x{row.VfxId:X} caster=0x{row.CasterId:X} target=0x{row.TargetId:X} " +
            $"territory={territoryId} map={row.MapId} slot={EscapeToken(row.Slot)} textureIndex={row.TextureIndexText} decodedCard={row.DecodedCardText} " +
            $"source='{Escape(row.CardSource)}' reason='{Escape(row.SolverReason)}' pos={row.PositionText} distance={row.DistanceText} ageSeconds={row.AgeSeconds.ToString("0.###", CultureInfo.InvariantCulture)} " +
            $"isStatic={row.IsStatic} hasRun={row.HasRun}");
        WriteDatamineJsonRow(
            territoryId,
            "tracked_vfx",
            new
            {
                row.Path,
                VfxId = $"0x{row.VfxId:X}",
                CasterId = $"0x{row.CasterId:X}",
                TargetId = $"0x{row.TargetId:X}",
                row.CasterLabel,
                row.TargetLabel,
                row.MapId,
                Position = row.PositionText,
                row.DistanceText,
                row.AgeSeconds,
                row.IsStatic,
                row.HasRun,
                row.HigherLowerRelevant,
                row.CardSource,
                row.Slot,
                row.TextureIndex,
                row.DecodedCard,
                row.SolverReason,
            });
    }

    public void RecordAutomationLine(string line)
    {
        var now = DateTime.UtcNow;
        FinishTraceIfExpired(now);
        if (!EnsureWriter(clientState.TerritoryType))
            return;

        WriteLine($"{Prefix} {line}");
        FlushWriter();
        RecordDatamineAutomationLine(line);
    }

    private void MarkHigherLowerSignal(DateTime timestampUtc, string source)
    {
        if (timestampUtc <= lastHigherLowerSignalUtc)
            return;

        lastHigherLowerSignalUtc = timestampUtc;
        lastHigherLowerSignalSource = string.IsNullOrWhiteSpace(source) ? "unknown" : source;
    }

    public HigherLowerRuntimeState CaptureRuntimeState()
    {
        var runtime = CaptureLiveProbe().Runtime;
        UpdateDatamineSessionFromRuntime(runtime, clientState.TerritoryType);
        StartLazyTraceIfAddonAppeared(runtime.TreasureHighLowVisible);
        return runtime;
    }

    private unsafe AddonCardDecode CaptureAddonCardDecode()
    {
        nint addonPointer = gameGui.GetAddonByName(AddonName, 1);
        return addonPointer == nint.Zero
            ? AddonCardDecode.None
            : DecodeAddonCards((AtkUnitBase*)addonPointer);
    }

    private static unsafe AddonCardDecode DecodeAddonCards(AtkUnitBase* addon)
    {
        if (addon == null || addon->AtkValues == null)
            return AddonCardDecode.None;

        var current = ReadAddonCardValue(addon, 4);
        var other = ReadAddonCardValue(addon, 5);
        return new AddonCardDecode(
            CurrentCard: current.Card,
            CurrentCardText: current.Text,
            OtherCard: other.Card,
            OtherCardText: other.Text,
            CurrentCardSource: current.Available ? "addon-atk-value[4]" : "none");
    }

    private static unsafe AddonCardValue ReadAddonCardValue(AtkUnitBase* addon, int index)
    {
        if (addon == null || addon->AtkValues == null || addon->AtkValuesCount <= index)
            return new AddonCardValue(null, "unavailable", false);

        var raw = ReadAtkValue(addon->AtkValues[index]).Trim();
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            if (value is >= 1 and <= 9)
                return new AddonCardValue(value, value.ToString(CultureInfo.InvariantCulture), true);
            if (value == 0)
                return new AddonCardValue(null, "blank", true);
        }

        return new AddonCardValue(null, string.IsNullOrWhiteSpace(raw) ? "unknown" : raw, true);
    }

    public int? ResolveCard(string graphicKey)
    {
        if (string.IsNullOrWhiteSpace(graphicKey))
            return null;

        EnsureCardMapLoaded();
        return cardMap.GraphicToCard.TryGetValue(graphicKey, out var card) ? card : null;
    }

    public bool TagKnownCard(int card, string role)
    {
        if (card is < 1 or > 9)
            return false;

        role = NormalizeKnownCardRole(role);
        if (string.IsNullOrWhiteSpace(role))
            return false;

        knownCardSequence++;
        knownCardTag = new KnownCardTag(card, role, DateTime.UtcNow, knownCardSequence, 0);
        forceNextSnapshot = true;
        log.Information($"{Prefix} known card tag queued card={card} role={role} seq={knownCardSequence}.");
        return true;
    }

    public bool TagKnownBoard(string left, string right, string? label)
    {
        var rawLeft = left;
        var rawRight = right;
        left = NormalizeKnownBoardCardToken(left);
        right = NormalizeKnownBoardCardToken(right);
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            log.Information($"{Prefix} invalid board tag args left='{Escape(rawLeft)}' right='{Escape(rawRight)}'.");
            return false;
        }

        label = (label ?? string.Empty).Trim();
        if (label.Length > 80)
            label = label[..80];

        knownBoardSequence++;
        knownBoardTag = new KnownBoardTag(left, right, label, DateTime.UtcNow, knownBoardSequence, 0);
        forceNextSnapshot = true;
        log.Information($"{Prefix} known board tag queued left={left} right={right} label='{Escape(label)}' seq={knownBoardSequence}.");
        return true;
    }

    public void ClearUnsafeCalibrationMap()
    {
        EnsureCardMapLoaded();
        var before = cardMap.GraphicToCard.Count;
        cardMap.GraphicToCard = cardMap.GraphicToCard
            .Where(static x => !IsUnsafeGraphicKey(x.Key))
            .ToDictionary(static x => x.Key, static x => x.Value, StringComparer.Ordinal);
        cardMap.Sources = cardMap.Sources
            .Where(x => cardMap.GraphicToCard.ContainsKey(x.Key))
            .ToDictionary(static x => x.Key, static x => x.Value, StringComparer.Ordinal);
        cardMap.UpdatedUtc = DateTime.UtcNow;
        cardMapDirty = true;
        SaveCardMap();
        log.Information($"{Prefix} cleared unsafe calibration map entries removed={before - cardMap.GraphicToCard.Count} remaining={cardMap.GraphicToCard.Count}.");
    }

    public HigherLowerTextureExportResult ExportCurrentTextureProbe()
    {
        try
        {
            nint addonPointer = gameGui.GetAddonByName(AddonName, 1);
            if (addonPointer == nint.Zero)
                return new HigherLowerTextureExportResult(false, $"Higher/Lower export failed: {AddonName} is not open.", string.Empty);

            unsafe
            {
                var addon = (AtkUnitBase*)addonPointer;
                if (!addon->IsVisible)
                    return new HigherLowerTextureExportResult(false, $"Higher/Lower export failed: {AddonName} is not visible.", string.Empty);

                var knownBoard = PeekKnownBoardTag();
                var candidates = CaptureTextureExportCandidates(addon, knownBoard);
                return WriteTextureExport(candidates, knownBoard, "live-addon", null);
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"{Prefix} texture export failed.");
            return new HigherLowerTextureExportResult(false, $"Higher/Lower export failed: {ex.Message}", string.Empty);
        }
    }

    public HigherLowerTextureExportResult ExportTexturePath(string texturePath, IReadOnlyList<string> rectTokens)
    {
        try
        {
            texturePath = NormalizeGameFilePath(texturePath);
            if (string.IsNullOrWhiteSpace(texturePath) || !texturePath.EndsWith(".tex", StringComparison.OrdinalIgnoreCase))
                return new HigherLowerTextureExportResult(false, "Higher/Lower exportpath failed: texture path must end with .tex.", string.Empty);

            TextureExportRect? rect = null;
            if (rectTokens.Count == 4)
            {
                if (!TryParseExportRect(rectTokens, out rect))
                    return new HigherLowerTextureExportResult(false, "Higher/Lower exportpath failed: rect must be four integers: u v w h.", string.Empty);
            }
            else if (rectTokens.Count != 0)
            {
                return new HigherLowerTextureExportResult(false, "Higher/Lower exportpath failed: use zero rect args or exactly u v w h.", string.Empty);
            }

            var candidate = new TextureExportCandidate(
                Source: "manual",
                Path: "manual",
                NodeId: 0,
                NodePointer: 0,
                Visible: true,
                ScreenX: 0,
                ScreenY: 0,
                Width: (ushort)Math.Clamp(rect?.Width ?? 0, 0, ushort.MaxValue),
                Height: (ushort)Math.Clamp(rect?.Height ?? 0, 0, ushort.MaxValue),
                ScaleX: 1,
                ScaleY: 1,
                Side: "manual",
                KnownCard: "unknown",
                TexturePath: texturePath,
                TexPathHash: 0,
                IconId: 0,
                PartsListId: 0,
                PartCount: 0,
                PartId: 0,
                AssetId: 0,
                Rect: rect,
                CandidateKey: $"manual-texture:file={texturePath};rect={FormatExportRect(rect)}");

            return WriteTextureExport(new[] { candidate }, null, "manual-path", texturePath);
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"{Prefix} manual texture export failed.");
            return new HigherLowerTextureExportResult(false, $"Higher/Lower exportpath failed: {ex.Message}", string.Empty);
        }
    }

    public void Dispose()
    {
        CloseWriter();
        CloseDatamineWriters();
        ResetDatamineSessionGate();
    }

    public unsafe void Update(
        DutyContextSnapshot context,
        ObservationSnapshot observation,
        PlannerSnapshot planner,
        string dialogStatus)
    {
        var now = DateTime.UtcNow;
        FinishTraceIfExpired(now);
        var traceActive = IsTraceActive(now);
        if (!configuration.HigherLowerVfxDataminingEnabled)
        {
            CloseDatamineWritersIfOpen();
            ResetDatamineSessionGate();
        }
        else
        {
            CloseDatamineSessionIfExpired(now);
        }

        if (!configuration.HigherLowerDiagnosticsEnabled && !forceNextSnapshot && !traceActive)
            return;

        Snapshot snapshot;
        try
        {
            snapshot = CaptureSnapshot(context, observation, planner, dialogStatus);
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"{Prefix} snapshot capture failed.");
            return;
        }

        if (!snapshot.Active && !forceNextSnapshot && !traceActive)
        {
            ResetIfPuzzleEnded(snapshot);
            return;
        }

        now = DateTime.UtcNow;
        FinishTraceIfExpired(now);
        traceActive = IsTraceActive(now);
        StartLazyTraceIfAddonAppeared(snapshot.TreasureHighLowVisible);
        now = DateTime.UtcNow;
        traceActive = IsTraceActive(now);

        var force = forceNextSnapshot;
        forceNextSnapshot = false;

        var markers = BuildMarkers(snapshot).ToList();
        var traceBaseline = traceActive && traceNeedsBaseline;
        var hasBaseline = lastSnapshotUtc != DateTime.MinValue
                          || !string.IsNullOrEmpty(lastSignature)
                          || lastAddonVisible
                          || lastHighLowTargetable;
        var phaseChanged = !traceBaseline
                           && (markers.Count > 0 || (hasBaseline && snapshot.Signature != lastSignature));
        var shouldEmitFull = force
                             || (traceActive
                                 ? phaseChanged
                                 : snapshot.Signature != lastSignature
                                   || now - lastSnapshotUtc >= TimeSpan.FromSeconds(10));
        var shouldEmitTraceSample = traceActive
                                    && now - lastTraceSampleUtc >= TraceSampleInterval;

        if (!shouldEmitFull && !shouldEmitTraceSample)
        {
            return;
        }

        if (shouldEmitTraceSample && !shouldEmitFull && EnsureWriter(snapshot))
            EmitTraceSample(snapshot, markers, now);

        UpdateSnapshotBaseline(snapshot, now);

        if (shouldEmitFull)
            EmitSnapshot(snapshot, markers, force);
    }

    private unsafe IReadOnlyList<TextureExportCandidate> CaptureTextureExportCandidates(AtkUnitBase* addon, KnownBoardTag? knownBoard)
    {
        var candidates = new List<TextureExportCandidate>(MaxExportImageCandidates);
        var seenNodes = new HashSet<nint>();
        var seenImages = new HashSet<nint>();
        var addonCenterX = GetAddonCenterX(addon);

        CollectNodeTextureExportCandidates(
            addon->RootNode,
            AddonName,
            "root",
            0,
            addonCenterX,
            knownBoard,
            seenNodes,
            seenImages,
            candidates);
        CollectUldManagerTextureExportCandidates(
            &addon->UldManager,
            AddonName,
            "uld",
            addonCenterX,
            knownBoard,
            seenNodes,
            seenImages,
            candidates);

        return candidates
            .Where(static x => IsExportableTextureCandidate(x))
            .OrderBy(static x => x.Side switch { "left" => 0, "right" => 1, "center" => 2, _ => 3 })
            .ThenBy(static x => x.ScreenY)
            .ThenBy(static x => x.ScreenX)
            .ThenBy(static x => x.NodeId)
            .Take(MaxExportImageCandidates)
            .ToList();
    }

    private unsafe void CollectNodeTextureExportCandidates(
        AtkResNode* node,
        string path,
        string source,
        int depth,
        float addonCenterX,
        KnownBoardTag? knownBoard,
        HashSet<nint> seenNodes,
        HashSet<nint> seenImages,
        List<TextureExportCandidate> candidates)
    {
        if (node == null || depth > MaxTreeDepth || candidates.Count >= MaxExportImageCandidates)
            return;

        var ptr = (nint)node;
        if (!seenNodes.Add(ptr))
            return;

        if (node->Type == NodeType.Image)
            TryAddTextureExportCandidate(source, path, (AtkImageNode*)node, addonCenterX, knownBoard, seenImages, candidates);
        else if ((int)node->Type >= 1000)
            CollectComponentTextureExportCandidates(source, path, (AtkComponentNode*)node, addonCenterX, knownBoard, seenNodes, seenImages, candidates);

        var childIndex = 0;
        for (var child = node->ChildNode; child != null && candidates.Count < MaxExportImageCandidates; child = child->NextSiblingNode)
        {
            CollectNodeTextureExportCandidates(
                child,
                $"{path}/{childIndex}",
                source,
                depth + 1,
                addonCenterX,
                knownBoard,
                seenNodes,
                seenImages,
                candidates);
            childIndex++;
        }
    }

    private unsafe void CollectUldManagerTextureExportCandidates(
        AtkUldManager* uldManager,
        string path,
        string source,
        float addonCenterX,
        KnownBoardTag? knownBoard,
        HashSet<nint> seenNodes,
        HashSet<nint> seenImages,
        List<TextureExportCandidate> candidates)
    {
        if (uldManager == null || uldManager->NodeList == null)
            return;

        var count = Math.Min(uldManager->NodeListCount, 512u);
        for (var i = 0u; i < count && candidates.Count < MaxExportImageCandidates; i++)
        {
            var node = uldManager->NodeList[i];
            if (node == null)
                continue;

            CollectNodeTextureExportCandidates(
                node,
                $"{path}[{i}]",
                source,
                0,
                addonCenterX,
                knownBoard,
                seenNodes,
                seenImages,
                candidates);
        }
    }

    private unsafe void CollectComponentTextureExportCandidates(
        string source,
        string path,
        AtkComponentNode* componentNode,
        float addonCenterX,
        KnownBoardTag? knownBoard,
        HashSet<nint> seenNodes,
        HashSet<nint> seenImages,
        List<TextureExportCandidate> candidates)
    {
        if (componentNode == null || componentNode->Component == null)
            return;

        CollectUldManagerTextureExportCandidates(
            &componentNode->Component->UldManager,
            $"{path}.component",
            "component",
            addonCenterX,
            knownBoard,
            seenNodes,
            seenImages,
            candidates);
    }

    private unsafe void TryAddTextureExportCandidate(
        string source,
        string path,
        AtkImageNode* imageNode,
        float addonCenterX,
        KnownBoardTag? knownBoard,
        HashSet<nint> seenImages,
        List<TextureExportCandidate> candidates)
    {
        if (imageNode == null || candidates.Count >= MaxExportImageCandidates)
            return;

        var node = &imageNode->AtkResNode;
        if (!seenImages.Add((nint)node))
            return;

        var partsList = imageNode->PartsList;
        var partCount = ReadPartsListCount(partsList);
        var parts = partsList == null ? null : partsList->Parts;
        var selectedPart = parts != null && imageNode->PartId < partCount
            ? &parts[imageNode->PartId]
            : null;
        var asset = selectedPart == null ? null : selectedPart->UldAsset;
        var texture = asset == null ? null : &asset->AtkTexture;
        var resource = texture == null ? null : texture->Resource;
        var texHandle = resource == null ? null : resource->TexFileResourceHandle;
        var texturePath = texHandle == null ? string.Empty : NormalizeGameFilePath(ReadResourceFileName(&texHandle->ResourceHandle));
        var rect = selectedPart == null
            ? null
            : new TextureExportRect(selectedPart->U, selectedPart->V, selectedPart->Width, selectedPart->Height);
        var side = InferExportSide(node->ScreenX, addonCenterX);
        var knownCard = side switch
        {
            "left" => knownBoard?.LeftCard ?? "unknown",
            "right" => knownBoard?.RightCard ?? "unknown",
            _ => "unknown",
        };

        candidates.Add(new TextureExportCandidate(
            Source: source,
            Path: path,
            NodeId: node->NodeId,
            NodePointer: (nint)node,
            Visible: node->IsVisible(),
            ScreenX: node->ScreenX,
            ScreenY: node->ScreenY,
            Width: node->Width,
            Height: node->Height,
            ScaleX: node->ScaleX,
            ScaleY: node->ScaleY,
            Side: side,
            KnownCard: knownCard,
            TexturePath: texturePath,
            TexPathHash: resource == null ? 0u : resource->TexPathHash,
            IconId: resource == null ? 0u : resource->IconId,
            PartsListId: ReadPartsListId(partsList),
            PartCount: partCount,
            PartId: imageNode->PartId,
            AssetId: asset == null ? 0u : asset->Id,
            Rect: rect,
            CandidateKey: BuildImageResourceKey(imageNode, selectedPart, asset, resource, texturePath)));
    }

    private HigherLowerTextureExportResult WriteTextureExport(
        IEnumerable<TextureExportCandidate> candidates,
        KnownBoardTag? knownBoard,
        string source,
        string? manualTexturePath)
    {
        var exportCandidates = candidates
            .Where(static x => IsExportableTextureCandidate(x))
            .Take(MaxExportImageCandidates)
            .ToList();
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        var exportRoot = CreateUniqueExportDirectory(timestamp);
        var rawRoot = Path.Combine(exportRoot, "raw");
        var atlasRoot = Path.Combine(exportRoot, "atlas");
        var labelRoot = Path.Combine(exportRoot, "to-label");
        Directory.CreateDirectory(rawRoot);
        Directory.CreateDirectory(atlasRoot);
        Directory.CreateDirectory(labelRoot);

        var manifest = new TextureExportManifest
        {
            GeneratedAtUtc = DateTime.UtcNow,
            Source = source,
            TerritoryId = clientState.TerritoryType,
            KnownLeftCard = knownBoard?.LeftCard ?? "unknown",
            KnownRightCard = knownBoard?.RightCard ?? "unknown",
            KnownBoardLabel = knownBoard?.Label ?? string.Empty,
            ManualTexturePath = manualTexturePath ?? string.Empty,
        };

        var rawByTexturePath = new Dictionary<string, (string Path, long Size, string Error)>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < exportCandidates.Count; i++)
        {
            var candidate = exportCandidates[i];
            var rawPath = string.Empty;
            var rawSize = 0L;
            var error = string.Empty;
            if (!rawByTexturePath.TryGetValue(candidate.TexturePath, out var cached))
            {
                rawPath = BuildOutputPath(rawRoot, candidate.TexturePath);
                if (TryWriteRawTextureFile(candidate.TexturePath, rawPath, out rawSize, out error))
                    cached = (rawPath, rawSize, string.Empty);
                else
                    cached = (rawPath, 0L, error);

                rawByTexturePath[candidate.TexturePath] = cached;
            }

            rawPath = cached.Path;
            rawSize = cached.Size;
            error = cached.Error;

            var fileStem = BuildCandidateFileStem(i + 1, candidate);
            var atlasPath = Path.Combine(atlasRoot, $"{SanitizeFileName(Path.GetFileNameWithoutExtension(candidate.TexturePath))}_{candidate.TexPathHash:X8}.png");
            var cropPath = Path.Combine(labelRoot, $"{fileStem}.png");
            manifest.Entries.Add(new TextureExportManifestEntry
            {
                CandidateIndex = i + 1,
                Side = candidate.Side,
                KnownCard = candidate.KnownCard,
                Source = candidate.Source,
                Path = candidate.Path,
                NodeId = candidate.NodeId,
                NodePointer = $"0x{candidate.NodePointer:X}",
                Visible = candidate.Visible,
                ScreenX = candidate.ScreenX,
                ScreenY = candidate.ScreenY,
                Width = candidate.Width,
                Height = candidate.Height,
                ScaleX = candidate.ScaleX,
                ScaleY = candidate.ScaleY,
                TexturePath = candidate.TexturePath,
                TexPathHash = $"0x{candidate.TexPathHash:X8}",
                IconId = candidate.IconId,
                PartsListId = candidate.PartsListId,
                PartCount = candidate.PartCount,
                PartId = candidate.PartId,
                AssetId = candidate.AssetId,
                Rect = candidate.Rect,
                CandidateKey = candidate.CandidateKey,
                RawTexPath = rawPath,
                RawTexSize = rawSize,
                AtlasPngPath = atlasPath,
                CropPngPath = cropPath,
                Error = error,
            });
        }

        var manifestPath = Path.Combine(exportRoot, "manifest.json");
        var csvPath = Path.Combine(exportRoot, "manifest.csv");
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(csvPath, BuildTextureExportCsv(manifest.Entries));

        var conversion = RunTextureExportConverter(manifestPath);
        File.WriteAllText(
            Path.Combine(exportRoot, "converter.log"),
            $"success={conversion.Success}{Environment.NewLine}command={conversion.Command}{Environment.NewLine}{conversion.Output}");

        var usable = manifest.Entries.Count(static x => string.IsNullOrWhiteSpace(x.Error));
        var converted = conversion.Success ? "png crops ready" : $"raw TEX only; converter failed: {conversion.Message}";
        var message = usable == 0
            ? $"Higher/Lower export wrote no usable texture candidates: {exportRoot}"
            : $"Higher/Lower export wrote {usable} candidate(s), {converted}: {exportRoot}";

        WriteLine($"{Prefix} hldbg-export source={source} path='{Escape(exportRoot)}' candidates={manifest.Entries.Count} usable={usable} converterSuccess={conversion.Success}");
        log.Information($"{Prefix} {message}");
        return new HigherLowerTextureExportResult(usable > 0, message, exportRoot);
    }

    private bool TryWriteRawTextureFile(string texturePath, string destination, out long size, out string error)
    {
        size = 0;
        error = string.Empty;
        try
        {
            if (!dataManager.FileExists(texturePath))
            {
                error = "file not found in game data";
                return false;
            }

            var file = dataManager.GetFile(texturePath);
            if (file == null)
            {
                error = "IDataManager.GetFile returned null";
                return false;
            }

            var rawData = file.Data;
            if (rawData.Length == 0)
            {
                error = "empty raw TEX payload";
                return false;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.WriteAllBytes(destination, rawData);
            size = rawData.Length;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private TextureConversionResult RunTextureExportConverter(string manifestPath)
    {
        var scriptPath = ResolveTextureExportScriptPath();
        if (string.IsNullOrWhiteSpace(scriptPath))
            return new TextureConversionResult(false, string.Empty, "converter script not found");

        var depRoot = Path.Combine(diagnosticDirectory, "_pydeps");
        foreach (var (fileName, args) in new[]
                 {
                     ("python", new[] { scriptPath, "--manifest", manifestPath, "--dep-root", depRoot }),
                     ("py", new[] { "-3", scriptPath, "--manifest", manifestPath, "--dep-root", depRoot }),
                 })
        {
            try
            {
                var psi = new ProcessStartInfo(fileName)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                foreach (var arg in args)
                    psi.ArgumentList.Add(arg);

                using var process = Process.Start(psi);
                if (process == null)
                    continue;

                if (!process.WaitForExit(120_000))
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch
                    {
                        // ignored; export already reports timeout below
                    }

                    return new TextureConversionResult(false, fileName, "converter timed out after 120s");
                }

                var output = (process.StandardOutput.ReadToEnd() + Environment.NewLine + process.StandardError.ReadToEnd()).Trim();
                if (process.ExitCode == 0)
                    return new TextureConversionResult(true, fileName, output);

                return new TextureConversionResult(false, fileName, string.IsNullOrWhiteSpace(output) ? $"exit code {process.ExitCode}" : output);
            }
            catch (Exception ex)
            {
                if (fileName.Equals("py", StringComparison.OrdinalIgnoreCase))
                    return new TextureConversionResult(false, fileName, ex.Message);
            }
        }

        return new TextureConversionResult(false, string.Empty, "python executable not found");
    }

    private static string? ResolveTextureExportScriptPath()
    {
        var baseDirectory = AppContext.BaseDirectory;
        foreach (var path in new[]
                 {
                     Path.Combine(baseDirectory, "Tools", TextureExportScriptName),
                     Path.Combine(baseDirectory, TextureExportScriptName),
                     Path.Combine(Directory.GetCurrentDirectory(), "Tools", TextureExportScriptName),
                 })
        {
            if (File.Exists(path))
                return path;
        }

        return null;
    }

    private string CreateUniqueExportDirectory(string timestamp)
    {
        var exportRoot = Path.Combine(diagnosticDirectory, "Exports");
        Directory.CreateDirectory(exportRoot);
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var suffix = attempt == 0 ? string.Empty : $"_{attempt}";
            var path = Path.Combine(exportRoot, $"HLTEX_{timestamp}_territory{clientState.TerritoryType}{suffix}");
            if (Directory.Exists(path))
                continue;

            Directory.CreateDirectory(path);
            return path;
        }

        throw new IOException($"Could not create unique Higher/Lower export directory in {exportRoot}.");
    }

    private static string BuildTextureExportCsv(IEnumerable<TextureExportManifestEntry> entries)
    {
        var builder = new StringBuilder();
        builder.AppendLine("candidateIndex,side,knownCard,source,path,nodeId,nodePointer,visible,screenX,screenY,width,height,scaleX,scaleY,texturePath,texPathHash,iconId,partsListId,partCount,partId,assetId,rectU,rectV,rectW,rectH,candidateKey,rawTexPath,rawTexSize,atlasPngPath,cropPngPath,error");
        foreach (var entry in entries)
        {
            builder.Append(entry.CandidateIndex).Append(',')
                .Append(Csv(entry.Side)).Append(',')
                .Append(Csv(entry.KnownCard)).Append(',')
                .Append(Csv(entry.Source)).Append(',')
                .Append(Csv(entry.Path)).Append(',')
                .Append(entry.NodeId).Append(',')
                .Append(Csv(entry.NodePointer)).Append(',')
                .Append(entry.Visible).Append(',')
                .Append(entry.ScreenX.ToString("0.##", CultureInfo.InvariantCulture)).Append(',')
                .Append(entry.ScreenY.ToString("0.##", CultureInfo.InvariantCulture)).Append(',')
                .Append(entry.Width).Append(',')
                .Append(entry.Height).Append(',')
                .Append(entry.ScaleX.ToString("0.###", CultureInfo.InvariantCulture)).Append(',')
                .Append(entry.ScaleY.ToString("0.###", CultureInfo.InvariantCulture)).Append(',')
                .Append(Csv(entry.TexturePath)).Append(',')
                .Append(Csv(entry.TexPathHash)).Append(',')
                .Append(entry.IconId).Append(',')
                .Append(entry.PartsListId).Append(',')
                .Append(entry.PartCount).Append(',')
                .Append(entry.PartId).Append(',')
                .Append(entry.AssetId).Append(',')
                .Append(entry.Rect?.U.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append(',')
                .Append(entry.Rect?.V.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append(',')
                .Append(entry.Rect?.Width.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append(',')
                .Append(entry.Rect?.Height.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append(',')
                .Append(Csv(entry.CandidateKey)).Append(',')
                .Append(Csv(entry.RawTexPath)).Append(',')
                .Append(entry.RawTexSize).Append(',')
                .Append(Csv(entry.AtlasPngPath)).Append(',')
                .Append(Csv(entry.CropPngPath)).Append(',')
                .Append(Csv(entry.Error))
                .AppendLine();
        }

        return builder.ToString();
    }

    private static string Csv(string? value)
    {
        value ??= string.Empty;
        return $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    private static string BuildCandidateFileStem(int index, TextureExportCandidate candidate)
    {
        var card = string.IsNullOrWhiteSpace(candidate.KnownCard) ? "unknown" : candidate.KnownCard;
        var rect = candidate.Rect == null
            ? "rectfull"
            : $"u{candidate.Rect.U}_v{candidate.Rect.V}_w{candidate.Rect.Width}_h{candidate.Rect.Height}";
        return SanitizeFileName(
            $"candidate_{index:000}_{candidate.Side}_card{card}_node{candidate.NodeId}_parts{candidate.PartsListId}_{candidate.PartId}_hash{candidate.TexPathHash:X8}_{rect}");
    }

    private static string BuildOutputPath(string root, string gameFilePath)
    {
        var relative = NormalizeGameFilePath(gameFilePath).Replace('/', Path.DirectorySeparatorChar);
        relative = string.Join(Path.DirectorySeparatorChar, relative.Split(Path.DirectorySeparatorChar).Select(SanitizeFileName));
        var path = Path.GetFullPath(Path.Combine(root, relative));
        var fullRoot = Path.GetFullPath(root);
        if (!path.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"Refusing to write outside export root: {path}");

        return path;
    }

    private static string SanitizeFileName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "unknown";

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
            builder.Append(invalid.Contains(ch) || ch is ':' or ';' or '=' or '\'' or '"' ? '_' : ch);

        return builder.ToString();
    }

    private static bool IsExportableTextureCandidate(TextureExportCandidate candidate)
        => !string.IsNullOrWhiteSpace(candidate.TexturePath)
           && candidate.TexturePath.EndsWith(".tex", StringComparison.OrdinalIgnoreCase)
           && !candidate.TexturePath.Contains("NeedGreed.tex", StringComparison.OrdinalIgnoreCase)
           && !candidate.TexturePath.Contains("<unreadable>", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeGameFilePath(string value)
    {
        value = (value ?? string.Empty).Trim().Trim('\0').Replace('\\', '/');
        while (value.StartsWith("/", StringComparison.Ordinal))
            value = value[1..];

        var marker = value.IndexOf("ui/", StringComparison.OrdinalIgnoreCase);
        if (marker > 0)
            value = value[marker..];

        return value;
    }

    private static bool TryParseExportRect(IReadOnlyList<string> tokens, out TextureExportRect? rect)
    {
        rect = null;
        if (tokens.Count != 4)
            return false;

        if (!int.TryParse(tokens[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var u)
            || !int.TryParse(tokens[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            || !int.TryParse(tokens[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var width)
            || !int.TryParse(tokens[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var height)
            || width < 0
            || height < 0)
        {
            return false;
        }

        rect = new TextureExportRect(u, v, width, height);
        return true;
    }

    private KnownBoardTag? PeekKnownBoardTag()
    {
        if (knownBoardTag == null)
            return null;

        var age = DateTime.UtcNow - knownBoardTag.CreatedUtc;
        if (age > KnownBoardTagTtl || knownBoardTag.EmittedSnapshots >= KnownBoardTagMaxSnapshots)
            return null;

        return knownBoardTag;
    }

    private static unsafe float GetAddonCenterX(AtkUnitBase* addon)
    {
        if (addon == null)
            return 0;

        var width = addon->RootNode == null ? 0 : addon->RootNode->Width * addon->RootNode->ScaleX;
        return addon->X + (width / 2f);
    }

    private static string InferExportSide(float screenX, float addonCenterX)
    {
        if (addonCenterX <= 0)
            return "unknown";

        if (screenX < addonCenterX - 8)
            return "left";

        if (screenX > addonCenterX + 8)
            return "right";

        return "center";
    }

    private static string FormatExportRect(TextureExportRect? rect)
        => rect == null ? "none" : $"u={rect.U} v={rect.V} w={rect.Width} h={rect.Height}";

    private void ResetRuntimeState()
    {
        lastSignature = string.Empty;
        lastAddonSignature = string.Empty;
        lastWorldSignature = string.Empty;
        lastAddonVisible = false;
        lastHighLowTargetable = false;
        lastSnapshotUtc = DateTime.MinValue;
        forceNextSnapshot = false;
        forceNextStateProbe = false;
        knownCardTag = null;
        knownBoardTag = null;
        traceStartedUtc = DateTime.MinValue;
        traceUntilUtc = DateTime.MinValue;
        lastTraceSampleUtc = DateTime.MinValue;
        activeTraceSequence = 0;
        traceSampleSequence = 0;
        traceNeedsBaseline = false;
        traceVfxRowsWritten = 0;
        traceVfxRowsDropped = 0;
        traceVfxTruncationLogged = false;
    }

    private bool IsTraceActive(DateTime nowUtc)
        => activeTraceSequence > 0 && nowUtc < traceUntilUtc;

    private void StartLazyTraceIfAddonAppeared(bool treasureHighLowVisible)
    {
        var now = DateTime.UtcNow;
        FinishTraceIfExpired(now);
        if (!treasureHighLowVisible || lastAddonVisible || IsTraceActive(now))
            return;

        var trace = StartTrace(DefaultTraceSeconds);
        if (trace.Success)
        {
            WriteLine($"{Prefix} hlauto lazy-trace-start addon={AddonName} durationSeconds={trace.DurationSeconds.ToString("0.###", CultureInfo.InvariantCulture)}");
            FlushWriter();
            return;
        }

        log.Warning($"{Prefix} hlauto lazy-trace-start failed addon={AddonName} reason='{Escape(trace.Message)}'");
    }

    private bool IsCurrentTerritory(HigherLowerVfxTraceService.VfxEventRow row)
        => row.TerritoryId == 0
           || clientState.TerritoryType == 0
           || row.TerritoryId == clientState.TerritoryType;

    private void FinishTraceIfExpired(DateTime nowUtc)
    {
        if (activeTraceSequence == 0 || nowUtc < traceUntilUtc)
            return;

        var elapsedMs = traceStartedUtc == DateTime.MinValue
            ? 0
            : Math.Max(0, (int)(nowUtc - traceStartedUtc).TotalMilliseconds);
        if (traceVfxRowsDropped > 0 && !traceVfxTruncationLogged)
            WriteLine($"{Prefix} vfx truncated seq={activeTraceSequence} reason=max-trace-vfx-rows cap={MaxTraceVfxRows}");

        WriteLine(
            $"{Prefix} hldbg-trace end seq={activeTraceSequence} elapsedMs={elapsedMs} " +
            $"samples={traceSampleSequence} vfxRows={traceVfxRowsWritten} vfxDropped={traceVfxRowsDropped} reason=expired");

        activeTraceSequence = 0;
        traceStartedUtc = DateTime.MinValue;
        traceUntilUtc = DateTime.MinValue;
        lastTraceSampleUtc = DateTime.MinValue;
        traceSampleSequence = 0;
        traceNeedsBaseline = false;
        traceVfxRowsWritten = 0;
        traceVfxRowsDropped = 0;
        traceVfxTruncationLogged = false;
    }

    private void UpdateSnapshotBaseline(Snapshot snapshot, DateTime nowUtc)
    {
        lastSignature = snapshot.Signature;
        lastAddonSignature = snapshot.AddonSignature;
        lastWorldSignature = snapshot.WorldSignature;
        lastSnapshotUtc = nowUtc;
        lastAddonVisible = snapshot.TreasureHighLowVisible;
        lastHighLowTargetable = snapshot.HighLowTargetable;
        traceNeedsBaseline = false;
    }

    private void ResetIfPuzzleEnded(Snapshot snapshot)
    {
        if (lastAddonVisible)
            WriteLine($"{Prefix} marker=addon disappeared territory={clientState.TerritoryType}");
        if (lastHighLowTargetable)
            WriteLine($"{Prefix} marker=High/Low untargetable territory={clientState.TerritoryType}");
        FlushWriter();

        lastSignature = string.Empty;
        lastAddonSignature = string.Empty;
        lastWorldSignature = string.Empty;
        lastAddonVisible = snapshot.TreasureHighLowVisible;
        lastHighLowTargetable = snapshot.HighLowTargetable;
        lastSnapshotUtc = DateTime.MinValue;
        forceNextStateProbe = false;
        traceNeedsBaseline = activeTraceSequence > 0;
    }

    private bool EnsureWriter(Snapshot snapshot)
    {
        try
        {
            Directory.CreateDirectory(diagnosticDirectory);
            if (writer != null && currentLogPath != null)
            {
                var fileInfo = new FileInfo(currentLogPath);
                if (fileInfo.Exists && fileInfo.Length < MaxLogBytes)
                    return true;

                CloseWriter();
                log.Information($"{Prefix} rotating diagnostics file at {MaxLogBytes} bytes.");
            }

            PruneOldLogs();
            return OpenWriter(snapshot.TerritoryId);
        }
        catch (Exception ex)
        {
            CloseWriter();
            if (!fileErrorLogged)
            {
                fileErrorLogged = true;
                log.Warning(ex, $"{Prefix} diagnostics file open failed in {diagnosticDirectory}.");
            }

            return false;
        }
    }

    private bool EnsureWriter(uint territoryId)
    {
        try
        {
            Directory.CreateDirectory(diagnosticDirectory);
            if (writer != null && currentLogPath != null)
                return true;

            PruneOldLogs();
            return OpenWriter(territoryId);
        }
        catch (Exception ex)
        {
            CloseWriter();
            if (!fileErrorLogged)
            {
                fileErrorLogged = true;
                log.Warning(ex, $"{Prefix} diagnostics file open failed in {diagnosticDirectory}.");
            }

            return false;
        }
    }

    private bool OpenWriter(uint territoryId)
    {
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        var attempt = 0;
        while (true)
        {
            var suffix = attempt == 0 ? string.Empty : $"_{attempt}";
            currentLogPath = Path.Combine(diagnosticDirectory, $"HLDBG_{timestamp}_territory{territoryId}{suffix}.log");
            if (!File.Exists(currentLogPath))
                break;

            attempt++;
        }

        writer = new StreamWriter(new FileStream(currentLogPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true,
        };
        fileErrorLogged = false;
        log.Information($"{Prefix} diagnostics file: {currentLogPath}");
        return true;
    }

    private void WriteLine(string line)
    {
        if (writer == null)
            return;

        try
        {
            writer.WriteLine(line);
        }
        catch (Exception ex)
        {
            CloseWriter();
            if (!fileErrorLogged)
            {
                fileErrorLogged = true;
                log.Warning(ex, $"{Prefix} diagnostics file write failed.");
            }
        }
    }

    private void FlushWriter()
    {
        try
        {
            writer?.Flush();
        }
        catch (Exception ex)
        {
            CloseWriter();
            if (!fileErrorLogged)
            {
                fileErrorLogged = true;
                log.Warning(ex, $"{Prefix} diagnostics file flush failed.");
            }
        }
    }

    private void RecordDatamineServerEvent(HigherLowerServerEventTraceService.ServerEventRow row)
    {
        if (row.HigherLowerRelevant)
            TouchDatamineSession(DateTime.UtcNow, $"server:{row.KindLabel}", row.TerritoryId, allowStart: false);
        if (!ShouldRecordDatamine())
            return;

        var territoryId = ResolveDatamineTerritory(row.TerritoryId);
        if (!EnsureDatamineWriters(territoryId))
            return;

        WriteDatamineLogLine(
            $"{Prefix} datamine-server rowSeq={row.Sequence} rowTsUtc={row.TimestampUtc:O} {row.ToBossModLogLine()} dataHex='{Escape(row.DataHex)}'");
        WriteDatamineJsonRow(
            territoryId,
            "server",
            new
            {
                row.Sequence,
                row.TimestampUtc,
                Kind = row.KindLabel,
                row.BossModKind,
                row.ActorId,
                TargetId = $"0x{row.TargetId:X}",
                row.ObjectName,
                row.ObjectKind,
                GameObjectId = $"0x{row.GameObjectId:X}",
                row.EntityId,
                row.BaseId,
                row.LayoutId,
                row.GimmickId,
                row.EventState,
                row.EventId,
                row.Targetable,
                Position = row.PositionText,
                row.DistanceText,
                row.StateData,
                row.SourceParams,
                row.DataHex,
                row.HigherLowerRelevant,
                BossModLine = row.ToBossModLogLine(),
            });
    }

    private void RecordDatamineVfxEvent(HigherLowerVfxTraceService.VfxEventRow row)
    {
        if (IsKnownCardAvfxPath(row.Path, string.Empty))
            TouchDatamineSession(DateTime.UtcNow, $"vfx:{HigherLowerCardVfxCatalog.NormalizePath(row.Path)}", row.TerritoryId, allowStart: false);

        if (!ShouldRecordDatamine())
            return;

        var territoryId = ResolveDatamineTerritory(row.TerritoryId);
        if (!EnsureDatamineWriters(territoryId))
            return;

        WriteDatamineLogLine(
            $"{Prefix} datamine-vfx kind={row.KindLabel} path='{Escape(row.Path)}' ptr=0x{row.Pointer:X} " +
            $"caster=0x{row.CasterId:X} target=0x{row.TargetId:X} territory={territoryId} map={row.MapId} " +
            $"slot={EscapeToken(row.Slot)} textureIndex={row.TextureIndexText} decodedCard={row.DecodedCardText} " +
            $"source='{Escape(row.CardSource)}' reason='{Escape(row.SolverReason)}' pos={row.PositionText} distance={row.DistanceText}");
        WriteDatamineJsonRow(
            territoryId,
            "vfx",
            new
            {
                Kind = row.KindLabel,
                row.Path,
                Pointer = $"0x{row.Pointer:X}",
                CasterId = $"0x{row.CasterId:X}",
                TargetId = $"0x{row.TargetId:X}",
                row.CasterLabel,
                row.TargetLabel,
                row.CasterName,
                row.TargetName,
                row.CasterBaseId,
                row.TargetBaseId,
                row.MapId,
                Position = row.PositionText,
                row.DistanceText,
                row.IsStatic,
                row.HasRun,
                row.HigherLowerRelevant,
                row.SourceParams,
                row.CardSource,
                row.Slot,
                row.TextureIndex,
                row.DecodedCard,
                row.SolverReason,
            });
    }

    private void RecordDatamineAutomationLine(string line)
    {
        if (!ShouldRecordDatamine())
            return;

        var territoryId = ResolveDatamineTerritory(clientState.TerritoryType);
        if (!EnsureDatamineWriters(territoryId))
            return;

        WriteDatamineLogLine($"{Prefix} datamine-automation {line}");
        WriteDatamineJsonRow(territoryId, "automation", new { Line = line });
    }

    private bool ShouldRecordDatamine()
    {
        if (!configuration.HigherLowerVfxDataminingEnabled)
        {
            CloseDatamineWritersIfOpen();
            ResetDatamineSessionGate();
            return false;
        }

        CloseDatamineSessionIfExpired(DateTime.UtcNow);
        return IsDatamineSessionActive(DateTime.UtcNow);
    }

    private void UpdateDatamineSessionFromRuntime(HigherLowerRuntimeState runtime, uint territoryId)
    {
        if (!configuration.HigherLowerVfxDataminingEnabled)
        {
            CloseDatamineWritersIfOpen();
            ResetDatamineSessionGate();
            return;
        }

        var now = DateTime.UtcNow;
        CloseDatamineSessionIfExpired(now);
        if (runtime.TreasureHighLowVisible)
        {
            TouchDatamineSession(
                now,
                $"addon:{AddonName}",
                territoryId,
                allowStart: true,
                new
                {
                    runtime.TreasureHighLowVisible,
                    runtime.HighTargetable,
                    runtime.LowTargetable,
                    runtime.NotificationChallengeVisible,
                    runtime.SelectYesnoVisible,
                });
            return;
        }

        if (runtime.HighTargetable || runtime.LowTargetable)
        {
            TouchDatamineSession(
                now,
                "targetable:HighLow",
                territoryId,
                allowStart: true,
                new
                {
                    runtime.HighTargetable,
                    runtime.LowTargetable,
                    runtime.NotificationChallengeVisible,
                    runtime.SelectYesnoVisible,
                });
        }
    }

    private bool TouchDatamineSession(
        DateTime signalUtc,
        string source,
        uint territoryId,
        bool allowStart,
        object? detail = null)
    {
        if (!configuration.HigherLowerVfxDataminingEnabled)
            return false;

        var now = signalUtc == DateTime.MinValue ? DateTime.UtcNow : signalUtc;
        CloseDatamineSessionIfExpired(now);
        var wasActive = IsDatamineSessionActive(now);
        if (!wasActive && !allowStart)
            return false;

        datamineSessionLastSignalUtc = now;
        datamineSessionLastSignalSource = string.IsNullOrWhiteSpace(source) ? "unknown" : source;
        datamineSessionGraceUntilUtc = now + DatamineSessionGrace;

        territoryId = ResolveDatamineTerritory(territoryId);
        if (!EnsureDatamineWriters(territoryId))
            return false;

        WriteDatamineSessionSignal(territoryId, datamineSessionLastSignalSource, started: !wasActive, now, detail);
        return true;
    }

    private bool IsDatamineSessionActive(DateTime nowUtc)
        => datamineSessionGraceUntilUtc != DateTime.MinValue && nowUtc <= datamineSessionGraceUntilUtc;

    private void CloseDatamineSessionIfExpired(DateTime nowUtc)
    {
        if (datamineSessionGraceUntilUtc == DateTime.MinValue || nowUtc <= datamineSessionGraceUntilUtc)
            return;

        if (datamineLogWriter != null || datamineJsonlWriter != null)
        {
            var quietMs = datamineSessionLastSignalUtc == DateTime.MinValue
                ? 0
                : Math.Max(0, (int)(nowUtc - datamineSessionLastSignalUtc).TotalMilliseconds);
            WriteDatamineLogLine(
                $"{Prefix} datamine-session-end reason=quiet quietMs={quietMs} lastSignal='{Escape(datamineSessionLastSignalSource)}'");
            WriteDatamineJsonRow(
                ResolveDatamineTerritory(currentDatamineTerritoryId),
                "session_end",
                new
                {
                    Reason = "quiet",
                    QuietMs = quietMs,
                    LastSignalUtc = datamineSessionLastSignalUtc,
                    LastSignalSource = datamineSessionLastSignalSource,
                });
            FlushDatamineWriters();
        }

        CloseDatamineWritersIfOpen();
        ResetDatamineSessionGate();
        ResetDatamineCooldown();
    }

    private void WriteDatamineSessionSignal(uint territoryId, string source, bool started, DateTime signalUtc, object? detail)
    {
        var now = DateTime.UtcNow;
        var key = $"{source}:{started}:{territoryId}";
        if (!started && key == lastDatamineSignalLogKey && now - lastDatamineSignalLogUtc < DatamineSignalLogCooldown)
            return;

        lastDatamineSignalLogKey = key;
        lastDatamineSignalLogUtc = now;
        WriteDatamineLogLine(
            $"{Prefix} datamine-session-signal source='{Escape(source)}' started={started.ToString().ToLowerInvariant()} " +
            $"signalUtc={signalUtc:O} graceUntilUtc={datamineSessionGraceUntilUtc:O} graceMs={(int)DatamineSessionGrace.TotalMilliseconds}");
        WriteDatamineJsonRow(
            territoryId,
            "session_signal",
            new
            {
                Source = source,
                Started = started,
                SignalUtc = signalUtc,
                GraceUntilUtc = datamineSessionGraceUntilUtc,
                GraceMs = (int)DatamineSessionGrace.TotalMilliseconds,
                Detail = detail,
            });
    }

    private uint ResolveDatamineTerritory(uint territoryId)
        => territoryId != 0 ? territoryId : clientState.TerritoryType;

    private bool EnsureDatamineWriters(uint territoryId)
    {
        try
        {
            territoryId = ResolveDatamineTerritory(territoryId);
            Directory.CreateDirectory(datamineDirectory);
            if (datamineLogWriter != null
                && datamineJsonlWriter != null
                && currentDatamineTerritoryId == territoryId)
            {
                return true;
            }

            CloseDatamineWriters();

            var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var attempt = 0;
            while (true)
            {
                var suffix = attempt == 0 ? string.Empty : $"-{attempt}";
                currentDatamineSessionDirectory = Path.Combine(datamineDirectory, $"{timestamp}-territory-{territoryId}{suffix}");
                if (!Directory.Exists(currentDatamineSessionDirectory))
                    break;

                attempt++;
            }

            Directory.CreateDirectory(currentDatamineSessionDirectory);
            currentDatamineLogPath = Path.Combine(currentDatamineSessionDirectory, "datamine.log");
            currentDatamineJsonlPath = Path.Combine(currentDatamineSessionDirectory, "datamine.jsonl");
            datamineLogWriter = new StreamWriter(new FileStream(currentDatamineLogPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                AutoFlush = true,
            };
            datamineJsonlWriter = new StreamWriter(new FileStream(currentDatamineJsonlPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                AutoFlush = true,
            };
            currentDatamineTerritoryId = territoryId;
            datamineFileErrorLogged = false;

            WriteDatamineLogLine($"{Prefix} datamine-session-start territory={territoryId} path='{Escape(currentDatamineSessionDirectory)}'");
            WriteDatamineJsonRow(
                territoryId,
                "session_start",
                new
                {
                    SessionDirectory = currentDatamineSessionDirectory,
                    LogPath = currentDatamineLogPath,
                    JsonlPath = currentDatamineJsonlPath,
                });
            log.Information($"{Prefix} datamine session: {currentDatamineSessionDirectory}");
            return true;
        }
        catch (Exception ex)
        {
            CloseDatamineWriters();
            if (!datamineFileErrorLogged)
            {
                datamineFileErrorLogged = true;
                log.Warning(ex, $"{Prefix} datamine file open failed in {datamineDirectory}.");
            }

            return false;
        }
    }

    private void WriteDatamineLogLine(string line)
    {
        if (datamineLogWriter == null)
            return;

        try
        {
            datamineLogWriter.WriteLine(line);
        }
        catch (Exception ex)
        {
            CloseDatamineWriters();
            if (!datamineFileErrorLogged)
            {
                datamineFileErrorLogged = true;
                log.Warning(ex, $"{Prefix} datamine log write failed.");
            }
        }
    }

    private void FlushDatamineWriters()
    {
        try
        {
            datamineLogWriter?.Flush();
            datamineJsonlWriter?.Flush();
        }
        catch (Exception ex)
        {
            CloseDatamineWriters();
            if (!datamineFileErrorLogged)
            {
                datamineFileErrorLogged = true;
                log.Warning(ex, $"{Prefix} datamine file flush failed.");
            }
        }
    }

    private void WriteDatamineJsonRow(uint territoryId, string type, object payload)
    {
        if (datamineJsonlWriter == null)
            return;

        try
        {
            var now = DateTimeOffset.UtcNow;
            var row = new DatamineJsonRow(
                Sequence: ++datamineSequence,
                TimestampUtc: now.UtcDateTime,
                UnixMs: now.ToUnixTimeMilliseconds(),
                Type: type,
                TerritoryId: territoryId,
                Payload: payload);
            datamineJsonlWriter.WriteLine(JsonSerializer.Serialize(row));
        }
        catch (Exception ex)
        {
            CloseDatamineWriters();
            if (!datamineFileErrorLogged)
            {
                datamineFileErrorLogged = true;
                log.Warning(ex, $"{Prefix} datamine jsonl write failed.");
            }
        }
    }

    private void CloseDatamineWriters()
    {
        try
        {
            datamineLogWriter?.Dispose();
            datamineJsonlWriter?.Dispose();
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"{Prefix} datamine file close failed.");
        }
        finally
        {
            datamineLogWriter = null;
            datamineJsonlWriter = null;
            currentDatamineSessionDirectory = null;
            currentDatamineLogPath = null;
            currentDatamineJsonlPath = null;
            currentDatamineTerritoryId = 0;
        }
    }

    private void CloseDatamineWritersIfOpen()
    {
        if (datamineLogWriter != null
            || datamineJsonlWriter != null
            || currentDatamineSessionDirectory != null
            || currentDatamineLogPath != null
            || currentDatamineJsonlPath != null)
        {
            CloseDatamineWriters();
        }
    }

    private void ResetDatamineCooldown()
    {
        lastDatamineSurfaceKey = string.Empty;
        lastDatamineSurfaceUtc = DateTime.MinValue;
        datamineTrackedVfxLogUtc.Clear();
    }

    private void ResetDatamineSessionGate()
    {
        datamineSessionLastSignalUtc = DateTime.MinValue;
        datamineSessionGraceUntilUtc = DateTime.MinValue;
        datamineSessionLastSignalSource = "none";
        lastDatamineSignalLogKey = string.Empty;
        lastDatamineSignalLogUtc = DateTime.MinValue;
    }

    private void PruneDatamineTrackedVfxLog(DateTime nowUtc)
    {
        foreach (var key in datamineTrackedVfxLogUtc
                     .Where(x => nowUtc - x.Value > TimeSpan.FromSeconds(30))
                     .Select(static x => x.Key)
                     .ToList())
        {
            datamineTrackedVfxLogUtc.Remove(key);
        }
    }

    private void CloseWriter()
    {
        try
        {
            writer?.Dispose();
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"{Prefix} diagnostics file close failed.");
        }
        finally
        {
            writer = null;
            currentLogPath = null;
        }
    }

    private void PruneOldLogs()
    {
        try
        {
            var files = Directory.EnumerateFiles(diagnosticDirectory, "HLDBG_*.log")
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ThenByDescending(file => file.Name, StringComparer.OrdinalIgnoreCase)
                .Skip(MaxLogFiles - 1)
                .ToList();

            foreach (var file in files)
                file.Delete();
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"{Prefix} diagnostics log prune failed in {diagnosticDirectory}.");
        }
    }

    private unsafe Snapshot CaptureSnapshot(
        DutyContextSnapshot context,
        ObservationSnapshot observation,
        PlannerSnapshot planner,
        string dialogStatus)
    {
        nint addonPointer = gameGui.GetAddonByName(AddonName, 1);
        var addon = addonPointer == nint.Zero ? null : (AtkUnitBase*)addonPointer;
        var addonVisible = addon != null && addon->IsVisible;
        var addonCardDecode = DecodeAddonCards(addon);
        var notificationChallengeVisible = IsAddonVisible("_NotificationChallenge");
        var selectYesnoVisible = IsAddonVisible("SelectYesno");
        var selectYesnoPrompt = ReadSelectYesnoPrompt();
        var objects = CaptureWorldObjects();
        var hasHighLowNearby = objects.Any(static x => x.IsHighLow);
        var highLowTargetable = objects.Any(static x => x.IsHighLow && x.Targetable);
        var dialogLooksRelated = selectYesnoVisible
            && ContainsAny(selectYesnoPrompt, "treasure", "coffer", "gamble", "lure", "high", "low");
        var active = addonVisible
            || notificationChallengeVisible
            || hasHighLowNearby
            || dialogLooksRelated;

        var addonSignature = addonVisible && addon != null
            ? BuildAddonSignature(addon)
            : "addon:none";
        var worldSignature = string.Join("|", objects.Select(static x => x.Signature));
        var dialogSignature = $"{selectYesnoVisible}:{selectYesnoPrompt}";
        var signature = $"{addonSignature}##{worldSignature}##{dialogSignature}";

        return new Snapshot(
            Active: active,
            TerritoryId: clientState.TerritoryType,
            Context: context,
            Observation: observation,
            Planner: planner,
            DialogStatus: dialogStatus,
            TreasureHighLowPointer: addonPointer,
            TreasureHighLowVisible: addonVisible,
            NotificationChallengeVisible: notificationChallengeVisible,
            SelectYesnoVisible: selectYesnoVisible,
            SelectYesnoPrompt: selectYesnoPrompt,
            AddonCurrentCard: addonCardDecode.CurrentCard,
            AddonCurrentCardText: addonCardDecode.CurrentCardText,
            AddonOtherCard: addonCardDecode.OtherCard,
            AddonOtherCardText: addonCardDecode.OtherCardText,
            AddonCurrentCardSource: addonCardDecode.CurrentCardSource,
            HighLowTargetable: highLowTargetable,
            WorldObjects: objects,
            Signature: signature,
            AddonSignature: addonSignature,
            WorldSignature: worldSignature);
    }

    private IEnumerable<string> BuildMarkers(Snapshot snapshot)
    {
        if (snapshot.TreasureHighLowVisible && !lastAddonVisible)
            yield return "addon appeared";
        if (!snapshot.TreasureHighLowVisible && lastAddonVisible)
            yield return "addon disappeared";
        if (snapshot.HighLowTargetable && !lastHighLowTargetable)
            yield return "High/Low became targetable";
        if (!snapshot.HighLowTargetable && lastHighLowTargetable)
            yield return "High/Low became untargetable";
        if (!string.IsNullOrEmpty(lastAddonSignature) && snapshot.AddonSignature != lastAddonSignature)
            yield return "addon node signature changed";
        if (!string.IsNullOrEmpty(lastWorldSignature) && snapshot.WorldSignature != lastWorldSignature)
            yield return "world object signature changed";
    }

    private unsafe void EmitSnapshot(Snapshot snapshot, IEnumerable<string> markers, bool force)
    {
        if (!EnsureWriter(snapshot))
            return;

        var knownCard = ConsumeKnownCardTag();
        var knownBoard = ConsumeKnownBoardTag();
        var runStateProbe = forceNextStateProbe;
        forceNextStateProbe = false;

        WriteLine(
            $"{Prefix} snapshot force={force} markers=[{string.Join("; ", markers)}] territory={snapshot.TerritoryId} " +
            FormatKnownCardFields(knownCard) +
            FormatKnownBoardFields(knownBoard) +
            $"map={snapshot.Context.MapId} duty='{Escape(snapshot.Context.CurrentDuty?.EnglishName ?? string.Empty)}' " +
            $"mode={snapshot.Planner.Mode} objective={snapshot.Planner.ObjectiveKind} target='{Escape(snapshot.Planner.TargetName ?? string.Empty)}' " +
            $"dialogStatus='{Escape(snapshot.DialogStatus)}'");

        WriteLine(
            $"{Prefix} raw-state addonPtr=0x{snapshot.TreasureHighLowPointer:X} addonVisible={snapshot.TreasureHighLowVisible} " +
            $"notificationChallengeVisible={snapshot.NotificationChallengeVisible} selectYesnoVisible={snapshot.SelectYesnoVisible} " +
            $"addonCurrentCard={snapshot.AddonCurrentCardText} addonOtherCard={snapshot.AddonOtherCardText} addonCardSource='{Escape(snapshot.AddonCurrentCardSource)}' " +
            $"selectYesnoPrompt='{Escape(snapshot.SelectYesnoPrompt)}'");

        LogObservationContext(snapshot.Observation);
        LogWorldObjects(snapshot.WorldObjects);
        LogKnownCardCandidates(snapshot.WorldObjects, knownBoard);
        LogSelectYesno();
        LogTreasureHighLowAddon();
        if (runStateProbe)
            LogFocusedStateProbe(snapshot, knownBoard);
        if (cardMapDirty)
            SaveCardMap();
        FlushWriter();
    }

    private void EmitTraceSample(Snapshot snapshot, IReadOnlyList<string> markers, DateTime nowUtc)
    {
        traceSampleSequence++;
        lastTraceSampleUtc = nowUtc;

        var knownBoard = PeekKnownBoardTag(nowUtc);
        var elapsedMs = traceStartedUtc == DateTime.MinValue
            ? 0
            : Math.Max(0, (int)(nowUtc - traceStartedUtc).TotalMilliseconds);
        var highTargetable = snapshot.WorldObjects.Any(static x =>
            x.IsHighLow && x.Targetable && string.Equals(x.Name, "High", StringComparison.OrdinalIgnoreCase));
        var lowTargetable = snapshot.WorldObjects.Any(static x =>
            x.IsHighLow && x.Targetable && string.Equals(x.Name, "Low", StringComparison.OrdinalIgnoreCase));

        WriteLine(
            $"{Prefix} hldbg-trace-sample seq={activeTraceSequence} sample={traceSampleSequence} elapsedMs={elapsedMs} " +
            $"active={snapshot.Active} territory={snapshot.TerritoryId} markers=[{string.Join("; ", markers)}] " +
            FormatTraceKnownBoardFields(knownBoard, nowUtc) +
            $"addonVisible={snapshot.TreasureHighLowVisible} notificationChallengeVisible={snapshot.NotificationChallengeVisible} " +
            $"selectYesnoVisible={snapshot.SelectYesnoVisible} selectYesnoPromptHash=0x{HashText(snapshot.SelectYesnoPrompt):X8} " +
            $"addonCurrentCard={snapshot.AddonCurrentCardText} addonOtherCard={snapshot.AddonOtherCardText} addonCardSource='{Escape(snapshot.AddonCurrentCardSource)}' " +
            $"highLowTargetable={snapshot.HighLowTargetable} highTargetable={highTargetable} lowTargetable={lowTargetable} " +
            $"boardCandidates=[{FormatTraceBoardCandidates(snapshot.WorldObjects, knownBoard)}]");
    }

    private KnownBoardTag? PeekKnownBoardTag(DateTime nowUtc)
    {
        if (knownBoardTag == null)
            return null;

        if (nowUtc - knownBoardTag.CreatedUtc > KnownBoardTagTtl
            || knownBoardTag.EmittedSnapshots >= KnownBoardTagMaxSnapshots)
        {
            knownBoardTag = null;
            return null;
        }

        return knownBoardTag;
    }

    private static string FormatTraceKnownBoardFields(KnownBoardTag? tag, DateTime nowUtc)
    {
        if (tag == null)
            return "knownLeftCard=none knownRightCard=none knownBoardLabel=none boardTagAgeMs=none boardTagSeq=none ";

        var ageMs = Math.Max(0, (int)(nowUtc - tag.CreatedUtc).TotalMilliseconds);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"knownLeftCard={tag.LeftCard} knownRightCard={tag.RightCard} knownBoardLabel='{Escape(tag.Label)}' boardTagAgeMs={ageMs} boardTagSeq={tag.Sequence} ");
    }

    private static string FormatTraceBoardCandidates(IReadOnlyList<WorldObjectSnapshot> objects, KnownBoardTag? knownBoard)
    {
        var candidates = ResolveBoardCandidates(objects).ToList();
        if (candidates.Count == 0)
            return "none";

        var parts = new List<string>(candidates.Count);
        for (var i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            var side = i == 0 ? "left" : "right";
            var knownCard = side == "left" ? knownBoard?.LeftCard : knownBoard?.RightCard;
            if (string.IsNullOrWhiteSpace(knownCard))
                knownCard = "unknown";

            parts.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{side}:known={knownCard},go=0x{candidate.GameObjectId:X},layout={candidate.LayoutId},gimmick={candidate.GimmickId},eventState={candidate.EventState},eventId=0x{candidate.EventId:X},targetable={candidate.Targetable},draw={Escape(ExtractDrawPointer(candidate.DrawSignature))},graphicHash=0x{HashText(candidate.GraphicKey):X8}"));
        }

        return string.Join("; ", parts);
    }

    private void LogObservationContext(ObservationSnapshot observation)
    {
        WriteLine(
            $"{Prefix} ads-context liveMonsters={observation.LiveMonsters.Count} liveFollow={observation.LiveFollowTargets.Count} " +
            $"liveInteractables={observation.LiveInteractables.Count} monsterGhosts={observation.MonsterGhosts.Count} interactableGhosts={observation.InteractableGhosts.Count}");

        foreach (var interactable in observation.LiveInteractables.Take(24))
        {
            WriteLine(
                $"{Prefix} ads-live-interactable name='{Escape(interactable.Name)}' kind={interactable.ObjectKind} dataId={interactable.DataId} " +
                $"gameObjectId=0x{interactable.GameObjectId:X} class={interactable.Classification} pos={Format(interactable.Position)} map={interactable.MapId}");
        }
    }

    private void LogWorldObjects(IReadOnlyList<WorldObjectSnapshot> objects)
    {
        WriteLine($"{Prefix} world-object-count {objects.Count}");
        foreach (var obj in objects)
        {
            WriteLine(
                $"{Prefix} world-object name='{Escape(obj.Name)}' kind={obj.Kind} baseId={obj.BaseId} gameObjectId=0x{obj.GameObjectId:X} " +
                $"dataId={obj.BaseId} entityId=0x{obj.EntityId:X} nativePtr=0x{obj.NativePointer:X} layoutId={obj.LayoutId} gimmickId={obj.GimmickId} " +
                $"eventState={obj.EventState} eventId=0x{obj.EventId:X} renderFlags=0x{obj.RenderFlags:X8} namePlateIconId={obj.NamePlateIconId} " +
                $"targetable={obj.Targetable} pos={obj.Position} distance={obj.Distance} draw={obj.DrawSignature} childDraw={obj.ChildDrawSignature} " +
                $"graphicKey='{Escape(obj.GraphicKey)}'");
        }
    }

    private void LogKnownCardCandidates(IReadOnlyList<WorldObjectSnapshot> objects, KnownBoardTag? knownBoard)
    {
        var candidates = ResolveBoardCandidates(objects).ToList();
        for (var i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            var side = i == 0 ? "left" : "right";
            var cardToken = side == "left" ? knownBoard?.LeftCard : knownBoard?.RightCard;
            var cardText = string.IsNullOrWhiteSpace(cardToken) ? "unknown" : cardToken;
            WriteLine(
                $"{Prefix} knownCardCandidate side={side} card={cardText} graphicKey='{Escape(candidate.GraphicKey)}' " +
                $"name='{Escape(candidate.Name)}' gameObjectId=0x{candidate.GameObjectId:X} nativePtr=0x{candidate.NativePointer:X}");

            if (knownBoard != null
                && int.TryParse(cardToken, NumberStyles.Integer, CultureInfo.InvariantCulture, out var card)
                && card is >= 1 and <= 9
                && !string.IsNullOrWhiteSpace(candidate.GraphicKey))
            {
                LearnCardGraphic(card, candidate.GraphicKey, $"board:{side}:seq={knownBoard.Sequence}:label={knownBoard.Label}");
            }
        }
    }

    private static HigherLowerBoardCandidate ToBoardCandidate(string side, WorldObjectSnapshot candidate)
        => new(
            Side: side,
            BaseId: candidate.BaseId,
            LayoutId: candidate.LayoutId,
            GimmickId: candidate.GimmickId,
            EventState: candidate.EventState,
            EventId: candidate.EventId,
            Targetable: candidate.Targetable,
            Position: candidate.Position,
            DrawPointer: ExtractDrawPointer(candidate.DrawSignature),
            DrawSignature: candidate.DrawSignature,
            GraphicKey: candidate.GraphicKey,
            Unsafe: IsUnsafeGraphicKey(candidate.GraphicKey));

    private static IEnumerable<string> BuildSafetyWarnings(IReadOnlyList<HigherLowerBoardCandidate> candidates)
    {
        if (candidates.Count == 0)
        {
            yield return "blocked: no board candidates found";
            yield break;
        }

        foreach (var candidate in candidates)
        {
            if (candidate.Unsafe)
                yield return $"blocked: unsafe-slot-key on {candidate.Side}; draw={candidate.DrawPointer}";
        }

        foreach (var group in candidates
                     .Where(static x => !string.IsNullOrWhiteSpace(x.GraphicKey))
                     .GroupBy(static x => x.GraphicKey, StringComparer.Ordinal)
                     .Where(static x => x.Count() > 1))
        {
            yield return $"blocked: duplicate board graphicKey on {string.Join("/", group.Select(static x => x.Side))}";
        }
    }

    private static string ExtractDrawPointer(string drawSignature)
    {
        if (string.IsNullOrWhiteSpace(drawSignature))
            return "ptr=unknown";

        var space = drawSignature.IndexOf(' ', StringComparison.Ordinal);
        return space > 0 ? drawSignature[..space] : drawSignature;
    }

    private unsafe void LogTreasureHighLowAddon()
    {
        nint addonPointer = gameGui.GetAddonByName(AddonName, 1);
        if (addonPointer == nint.Zero)
        {
            WriteLine($"{Prefix} addon {AddonName} ptr=0x0 visible=False");
            return;
        }

        var addon = (AtkUnitBase*)addonPointer;
        WriteLine(
            $"{Prefix} addon {AddonName} ptr=0x{addonPointer:X} visible={addon->IsVisible} atkValuesCount={addon->AtkValuesCount} " +
            $"id={addon->Id} parentId={addon->ParentId} flags198=0x{addon->Flags198:X8} flags1B4=0x{addon->Flags1B4:X8} " +
            $"pos=({addon->X},{addon->Y}) scale={addon->Scale.ToString("0.###", CultureInfo.InvariantCulture)} alpha={addon->Alpha}");

        DumpAtkValues(addon);
        DumpNodeTree(addon, AddonName);
        DumpUldNodeList(addon, AddonName);
        DumpCollisionNodes(addon, AddonName);
        DumpGetNodeByIdFallback(addon, AddonName);
        LogKnownCallbackCandidates(addon);
    }

    private unsafe void LogFocusedStateProbe(Snapshot snapshot, KnownBoardTag? knownBoard)
    {
        var cardValues = GetKnownBoardCardValues(knownBoard);
        WriteLine(
            $"{Prefix} hldbg-state begin knownLeftCard={knownBoard?.LeftCard ?? "none"} knownRightCard={knownBoard?.RightCard ?? "none"} " +
            $"needleCards='{string.Join(",", cardValues)}'");

        nint addonPointer = gameGui.GetAddonByName(AddonName, 1);
        if (addonPointer == nint.Zero)
        {
            WriteLine($"{Prefix} hldbg-state addon ptr=0x0 visible=False");
        }
        else
        {
            var addon = (AtkUnitBase*)addonPointer;
            var addonCenterX = GetAddonCenterX(addon);
            DumpMemoryFingerprint("addon.unit", addon, sizeof(AtkUnitBase), cardValues);

            if (addon->AtkValues == null || addon->AtkValuesCount == 0)
            {
                WriteLine($"{Prefix} hldbg-state-atk-values ptr=0x0 count={addon->AtkValuesCount}");
            }
            else
            {
                var atkValueCount = Math.Min((int)addon->AtkValuesCount, MaxAtkValues);
                DumpMemoryFingerprint("addon.atkValues", addon->AtkValues, atkValueCount * sizeof(AtkValue), cardValues);
            }

            if (addon->RootNode != null)
                DumpFocusedStateNode("root", 0, AddonName, addon->RootNode, addonCenterX, cardValues);

            DumpFocusedStateNodeList("uld", AddonName, addon->UldManager.NodeList, addon->UldManager.NodeListCount, addonCenterX, cardValues);
        }

        var boardCandidates = ResolveBoardCandidates(snapshot.WorldObjects).ToList();
        for (var i = 0; i < boardCandidates.Count; i++)
        {
            var candidate = boardCandidates[i];
            var side = i == 0 ? "left" : "right";
            var card = side == "left" ? knownBoard?.LeftCard : knownBoard?.RightCard;
            var native = (GameObject*)candidate.NativePointer;
            WriteLine(
                $"{Prefix} hldbg-state-world side={side} card={card ?? "unknown"} " +
                $"gameObjectId=0x{candidate.GameObjectId:X} nativePtr=0x{candidate.NativePointer:X} " +
                $"layoutId={candidate.LayoutId} gimmickId={candidate.GimmickId} eventState={candidate.EventState} eventId=0x{candidate.EventId:X}");

            if (native != null)
                DumpMemoryFingerprint($"world.{side}.gameObject", native, sizeof(GameObject), cardValues);
        }

        WriteLine($"{Prefix} hldbg-state end");
    }

    private unsafe void DumpFocusedStateNodeList(
        string source,
        string path,
        AtkResNode** nodeList,
        uint nodeListCount,
        float addonCenterX,
        IReadOnlyList<int> cardValues)
    {
        var count = Math.Min(nodeListCount, 512u);
        if (nodeList == null)
        {
            WriteLine($"{Prefix} hldbg-state-node-list source={source} path='{Escape(path)}' ptr=0x0 count={nodeListCount}");
            return;
        }

        for (var i = 0u; i < count; i++)
        {
            var node = nodeList[i];
            if (node == null)
                continue;

            DumpFocusedStateNode(source, i, $"{path}[{i}]", node, addonCenterX, cardValues);
        }

        if (nodeListCount > count)
            WriteLine($"{Prefix} hldbg-state-node-list truncated source={source} path='{Escape(path)}' logged={count} total={nodeListCount}");
    }

    private unsafe void DumpFocusedStateNode(
        string source,
        uint index,
        string path,
        AtkResNode* node,
        float addonCenterX,
        IReadOnlyList<int> cardValues)
    {
        if (node == null)
            return;

        var side = InferExportSide(node->ScreenX, addonCenterX);
        WriteLine(
            $"{Prefix} hldbg-state-node source={source} index={index} path='{Escape(path)}' side={side} " +
            $"{DescribeNode(node)}");
        DumpMemoryFingerprint($"node.{source}.{path}", node, GetStateNodeByteLength(node), cardValues);

        if ((int)node->Type < 1000)
            return;

        var componentNode = (AtkComponentNode*)node;
        var component = componentNode->Component;
        if (component == null)
        {
            WriteLine($"{Prefix} hldbg-state-component source={source} path='{Escape(path)}' ptr=0x0");
            return;
        }

        WriteLine(
            $"{Prefix} hldbg-state-component source={source} path='{Escape(path)}' ptr=0x{(nint)component:X} " +
            $"componentType={component->GetComponentType()} componentFlags=0x{component->ComponentFlags:X8} " +
            $"uldNodeListPtr=0x{(nint)component->UldManager.NodeList:X} uldNodeListCount={component->UldManager.NodeListCount}");
        DumpMemoryFingerprint($"component.{source}.{path}", component, sizeof(AtkComponentBase), cardValues);
        DumpMemoryFingerprint($"component-uld.{source}.{path}", &component->UldManager, sizeof(AtkUldManager), cardValues);
        DumpFocusedStateNodeList("component", $"{path}.component", component->UldManager.NodeList, component->UldManager.NodeListCount, addonCenterX, cardValues);
    }

    private unsafe void LogSelectYesno()
    {
        nint addonPointer = gameGui.GetAddonByName("SelectYesno", 1);
        if (addonPointer == nint.Zero)
        {
            WriteLine($"{Prefix} addon SelectYesno ptr=0x0 visible=False");
            return;
        }

        var addon = (AddonSelectYesno*)addonPointer;
        var unit = &addon->AtkUnitBase;
        WriteLine(
            $"{Prefix} addon SelectYesno ptr=0x{addonPointer:X} visible={unit->IsVisible} atkValuesCount={unit->AtkValuesCount} " +
            $"prompt='{Escape(ReadSelectYesnoPrompt())}'");

        DumpButtonEvent("SelectYesno.Yes", addon->YesButton);
        DumpButtonEvent("SelectYesno.No", addon->NoButton);
    }

    private unsafe void DumpAtkValues(AtkUnitBase* addon)
    {
        var count = Math.Min((int)addon->AtkValuesCount, MaxAtkValues);
        if (addon->AtkValues == null)
        {
            WriteLine($"{Prefix} atk-values unavailable ptr=0x0 count={addon->AtkValuesCount}");
            return;
        }

        for (var i = 0; i < count; i++)
        {
            var value = addon->AtkValues[i];
            WriteLine($"{Prefix} atk-value index={i} type={value.Type} value='{Escape(ReadAtkValue(value))}'");
        }

        if (addon->AtkValuesCount > count)
            WriteLine($"{Prefix} atk-values truncated logged={count} total={addon->AtkValuesCount}");
    }

    private unsafe void DumpNodeTree(AtkUnitBase* addon, string label)
    {
        var visited = new HashSet<nint>();
        var logged = 0;
        DumpNode(addon->RootNode, label, 0, visited, ref logged);
        if (logged >= MaxTreeNodes)
            WriteLine($"{Prefix} node-tree truncated maxNodes={MaxTreeNodes}");
    }

    private unsafe void DumpNode(AtkResNode* node, string path, int depth, HashSet<nint> visited, ref int logged)
    {
        if (node == null || depth > MaxTreeDepth || logged >= MaxTreeNodes)
            return;

        var ptr = (nint)node;
        if (!visited.Add(ptr))
            return;

        logged++;
        WriteLine($"{Prefix} node path='{path}' {DescribeNode(node)}");
        if (node->Type == NodeType.Image)
            DumpImageNodeProbe("root", path, 0, (AtkImageNode*)node);
        else if ((int)node->Type >= 1000)
            DumpComponentNodeProbe("root", path, 0, (AtkComponentNode*)node);

        DumpNodeEvents($"node path='{path}'", node);

        var childIndex = 0;
        for (var child = node->ChildNode; child != null && logged < MaxTreeNodes; child = child->NextSiblingNode)
        {
            DumpNode(child, $"{path}/{childIndex}", depth + 1, visited, ref logged);
            childIndex++;
        }
    }

    private unsafe void DumpCollisionNodes(AtkUnitBase* addon, string label)
    {
        var count = Math.Min(addon->CollisionNodeListCount, 80u);
        if (addon->CollisionNodeList == null)
        {
            WriteLine($"{Prefix} collision-list addon={label} ptr=0x0 count={addon->CollisionNodeListCount}");
            return;
        }

        for (var i = 0u; i < count; i++)
        {
            var node = (AtkResNode*)addon->CollisionNodeList[i];
            if (node == null)
                continue;

            WriteLine($"{Prefix} collision addon={label} index={i} {DescribeNode(node)}");
            DumpNodeEvents($"collision addon={label} index={i}", node);
        }
    }

    private unsafe void DumpUldNodeList(AtkUnitBase* addon, string label)
    {
        var count = Math.Min(addon->UldManager.NodeListCount, 512u);
        if (addon->UldManager.NodeList == null)
        {
            WriteLine($"{Prefix} uld-node-list addon={label} ptr=0x0 count={addon->UldManager.NodeListCount}");
            return;
        }

        for (var i = 0u; i < count; i++)
        {
            var node = addon->UldManager.NodeList[i];
            if (node == null)
                continue;

            WriteLine($"{Prefix} uld-node addon={label} index={i} {DescribeNode(node)}");
            if (node->Type == NodeType.Image)
                DumpImageNodeProbe("uld", $"{label}[{i}]", i, (AtkImageNode*)node);
            else if ((int)node->Type >= 1000)
                DumpComponentNodeProbe("uld", $"{label}[{i}]", i, (AtkComponentNode*)node);

            DumpNodeEvents($"uld-node addon={label} index={i}", node);
        }

        if (addon->UldManager.NodeListCount > count)
            WriteLine($"{Prefix} uld-node-list truncated addon={label} logged={count} total={addon->UldManager.NodeListCount}");
    }

    private unsafe void LogKnownCallbackCandidates(AtkUnitBase* addon)
    {
        for (var arg = 0; arg <= 3; arg++)
            WriteLine($"{Prefix} observed-callback-param addon=TreasureHighLow fireCallback updateState=True args=[{arg}] label=unverified-observed-param");

        for (uint id = 1; id <= MaxGetNodeById; id++)
        {
            var node = addon->GetNodeById(id);
            if (node == null)
                continue;

            var evt = node->AtkEventManager.Event;
            while (evt != null)
            {
                if (evt->State.EventType is AtkEventType.ButtonClick or AtkEventType.MouseClick or AtkEventType.MouseDown or AtkEventType.MouseUp)
                {
                    WriteLine(
                        $"{Prefix} event-candidate addon=TreasureHighLow nodeId={node->NodeId} type={node->Type} " +
                        $"eventType={evt->State.EventType} param={evt->Param} target=0x{(nint)evt->Target:X} listener=0x{(nint)evt->Listener:X}");
                }

                evt = evt->NextEvent;
            }
        }
    }

    private unsafe void DumpGetNodeByIdFallback(AtkUnitBase* addon, string label)
    {
        var logged = 0;
        for (uint id = 1; id <= MaxGetNodeById && logged < MaxTreeNodes; id++)
        {
            var node = addon->GetNodeById(id);
            if (node == null)
                continue;

            logged++;
            WriteLine($"{Prefix} get-node-by-id addon={label} queryId={id} {DescribeNode(node)}");
        }

        if (logged >= MaxTreeNodes)
            WriteLine($"{Prefix} get-node-by-id truncated maxNodes={MaxTreeNodes} maxQueryId={MaxGetNodeById}");
    }

    private unsafe void DumpComponentNodeProbe(string source, string path, uint index, AtkComponentNode* componentNode)
    {
        if (componentNode == null)
            return;

        var node = &componentNode->AtkResNode;
        var component = componentNode->Component;
        if (component == null)
        {
            WriteLine(
                $"{Prefix} hldbg-component addon={AddonName} source={source} index={index} path='{Escape(path)}' " +
                $"nodePtr=0x{(nint)node:X} nodeId={node->NodeId} componentPtr=0x0");
            return;
        }

        WriteLine(
            $"{Prefix} hldbg-component addon={AddonName} source={source} index={index} path='{Escape(path)}' " +
            $"nodePtr=0x{(nint)node:X} nodeId={node->NodeId} visible={node->IsVisible()} " +
            $"screen=({node->ScreenX:0.##},{node->ScreenY:0.##}) size=({node->Width},{node->Height}) " +
            $"componentPtr=0x{(nint)component:X} componentType={component->GetComponentType()} componentFlags=0x{component->ComponentFlags:X8} " +
            $"uldNodeListPtr=0x{(nint)component->UldManager.NodeList:X} uldNodeListCount={component->UldManager.NodeListCount}");

        DumpComponentUldNodeList(source, path, component);
    }

    private unsafe void DumpComponentUldNodeList(string source, string path, AtkComponentBase* component)
    {
        if (component == null)
            return;

        var count = Math.Min(component->UldManager.NodeListCount, 256u);
        if (component->UldManager.NodeList == null)
        {
            WriteLine($"{Prefix} hldbg-component-node-list source={source} path='{Escape(path)}' ptr=0x0 count={component->UldManager.NodeListCount}");
            return;
        }

        for (var i = 0u; i < count; i++)
        {
            var child = component->UldManager.NodeList[i];
            if (child == null)
                continue;

            var childPath = $"{path}.component[{i}]";
            WriteLine($"{Prefix} hldbg-component-node source={source} path='{Escape(childPath)}' index={i} {DescribeNode(child)}");
            if (child->Type == NodeType.Image)
                DumpImageNodeProbe("component", childPath, i, (AtkImageNode*)child);
        }

        if (component->UldManager.NodeListCount > count)
            WriteLine($"{Prefix} hldbg-component-node-list truncated source={source} path='{Escape(path)}' logged={count} total={component->UldManager.NodeListCount}");
    }

    private unsafe void DumpButtonEvent(string label, AtkComponentButton* button)
    {
        if (button == null)
        {
            WriteLine($"{Prefix} button {label} ptr=0x0");
            return;
        }

        var ownerNode = button->AtkComponentBase.OwnerNode;
        var resNode = ownerNode == null ? null : &ownerNode->AtkResNode;
        WriteLine(
            $"{Prefix} button {label} ptr=0x{(nint)button:X} enabled={button->IsEnabled} ownerNode=0x{(nint)ownerNode:X} " +
            $"{(resNode == null ? "resNode=0x0" : DescribeNode(resNode))}");

        if (resNode == null)
            return;

        var evt = resNode->AtkEventManager.Event;
        while (evt != null)
        {
            WriteLine(
                $"{Prefix} button-event {label} eventType={evt->State.EventType} param={evt->Param} stateFlags={evt->State.StateFlags} " +
                $"target=0x{(nint)evt->Target:X} listener=0x{(nint)evt->Listener:X} node=0x{(nint)evt->Node:X}");
            evt = evt->NextEvent;
        }
    }

    private unsafe void DumpNodeEvents(string label, AtkResNode* node)
    {
        var evt = node->AtkEventManager.Event;
        var index = 0;
        while (evt != null && index < 64)
        {
            WriteLine(
                $"{Prefix} node-event {label} eventIndex={index} eventType={evt->State.EventType} param={evt->Param} " +
                $"stateFlags={evt->State.StateFlags} target=0x{(nint)evt->Target:X} listener=0x{(nint)evt->Listener:X} node=0x{(nint)evt->Node:X}");
            evt = evt->NextEvent;
            index++;
        }

        if (evt != null)
            WriteLine($"{Prefix} node-event {label} truncated maxEvents=64");
    }

    private unsafe void DumpImageNodeProbe(string source, string path, uint index, AtkImageNode* imageNode)
    {
        if (imageNode == null)
            return;

        var node = &imageNode->AtkResNode;
        var partsList = imageNode->PartsList;
        var partCount = ReadPartsListCount(partsList);
        var parts = partsList == null ? null : partsList->Parts;
        var selectedPart = parts != null && imageNode->PartId < partCount
            ? &parts[imageNode->PartId]
            : null;
        var asset = selectedPart == null ? null : selectedPart->UldAsset;
        var texture = asset == null ? null : &asset->AtkTexture;
        var resource = texture == null ? null : texture->Resource;
        var texHandle = resource == null ? null : resource->TexFileResourceHandle;
        var fileName = texHandle == null ? string.Empty : ReadResourceFileName(&texHandle->ResourceHandle);
        var key = BuildImageResourceKey(imageNode, selectedPart, asset, resource, fileName);

        WriteLine(
            $"{Prefix} hldbg-image addon={AddonName} source={source} index={index} path='{Escape(path)}' " +
            $"nodePtr=0x{(nint)node:X} nodeId={node->NodeId} visible={node->IsVisible()} " +
            $"pos=({node->X:0.##},{node->Y:0.##}) screen=({node->ScreenX:0.##},{node->ScreenY:0.##}) " +
            $"size=({node->Width},{node->Height}) scale=({node->ScaleX:0.###},{node->ScaleY:0.###}) " +
            $"partsListPtr=0x{(nint)partsList:X} partsListId={ReadPartsListId(partsList)} partCount={partCount} partId={imageNode->PartId} " +
            $"partPtr=0x{(nint)selectedPart:X} assetPtr=0x{(nint)asset:X} assetId={(asset == null ? 0u : asset->Id)} " +
            $"resourcePtr=0x{(nint)resource:X} iconId={(resource == null ? 0u : resource->IconId)} texPathHash=0x{(resource == null ? 0u : resource->TexPathHash):X8} " +
            $"texHandlePtr=0x{(nint)texHandle:X} texFile='{Escape(fileName)}' partRect='{Escape(DescribeUldPart(selectedPart))}' " +
            $"candidateKey='{Escape(key)}'");
    }

    private unsafe string DescribeNode(AtkResNode* node)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"ptr=0x{(nint)node:X} id={node->NodeId} type={node->Type} visible={node->IsVisible()} ");
        sb.Append(CultureInfo.InvariantCulture, $"flags=0x{(ushort)node->NodeFlags:X4} drawFlags=0x{node->DrawFlags:X8} priority={node->Priority} ");
        sb.Append(CultureInfo.InvariantCulture, $"pos=({node->X:0.##},{node->Y:0.##}) screen=({node->ScreenX:0.##},{node->ScreenY:0.##}) ");
        sb.Append(CultureInfo.InvariantCulture, $"size=({node->Width},{node->Height}) scale=({node->ScaleX:0.###},{node->ScaleY:0.###}) ");
        sb.Append(CultureInfo.InvariantCulture, $"color=rgba({node->Color.R},{node->Color.G},{node->Color.B},{node->Color.A}) childCount={node->ChildCount}");

        if (node->Type == NodeType.Text)
        {
            var textNode = (AtkTextNode*)node;
            sb.Append(CultureInfo.InvariantCulture, $" text='{Escape(textNode->NodeText.ToString().Trim())}' textId={textNode->TextId} fontSize={textNode->FontSize}");
        }
        else if (node->Type == NodeType.Image)
        {
            var imageNode = (AtkImageNode*)node;
            sb.Append(CultureInfo.InvariantCulture, $" flags=0x{(ushort)imageNode->Flags:X4} wrapMode={imageNode->WrapMode} partsListId={ReadPartsListId(imageNode->PartsList)} partCount={ReadPartsListCount(imageNode->PartsList)} partId={imageNode->PartId}");
        }
        else if (node->Type == NodeType.NineGrid)
        {
            var nineGridNode = (AtkNineGridNode*)node;
            sb.Append(CultureInfo.InvariantCulture, $" partsListId={ReadPartsListId(nineGridNode->PartsList)} partCount={ReadPartsListCount(nineGridNode->PartsList)} partId={nineGridNode->PartId}");
        }
        else if ((int)node->Type >= 1000)
        {
            var componentNode = (AtkComponentNode*)node;
            var component = componentNode->Component;
            if (component != null)
            {
                sb.Append(CultureInfo.InvariantCulture, $" componentType={component->GetComponentType()} componentFlags=0x{component->ComponentFlags:X8}");
            }
        }

        var eventCount = CountEvents(node);
        if (eventCount > 0)
            sb.Append(CultureInfo.InvariantCulture, $" eventCount={eventCount}");

        return sb.ToString();
    }

    private unsafe IReadOnlyList<WorldObjectSnapshot> CaptureWorldObjects()
    {
        var localPosition = objectTable.LocalPlayer?.Position;
        var rows = new List<WorldObjectSnapshot>();
        foreach (var obj in objectTable)
        {
            if (obj == null || obj.GameObjectId == objectTable.LocalPlayer?.GameObjectId)
                continue;

            var name = obj.Name.TextValue.Trim();
            var distance = localPosition.HasValue
                ? Vector3.Distance(localPosition.Value, obj.Position)
                : float.NaN;

            if (!ShouldCaptureObject(obj, name, distance))
                continue;

            var native = (GameObject*)obj.Address;
            var nativePointer = (nint)native;
            var entityId = native == null ? obj.EntityId : native->EntityId;
            var layoutId = native == null ? 0u : native->LayoutId;
            var gimmickId = native == null ? 0u : native->GimmickId;
            var eventState = native == null ? 0 : native->EventState;
            var eventId = native == null ? 0u : native->EventId.Id;
            var renderFlags = native == null ? 0u : (uint)native->RenderFlags;
            var namePlateIconId = native == null ? 0u : native->NamePlateIconId;
            var drawObject = native == null ? null : native->DrawObject;
            var drawSignature = DescribeDrawObject(drawObject);
            var childDrawSignature = DescribeChildDrawObjects(drawObject);
            var graphicKey = BuildGraphicKey(native, obj, drawObject, drawSignature, childDrawSignature);

            rows.Add(new WorldObjectSnapshot(
                Name: name,
                Kind: obj.ObjectKind,
                BaseId: obj.BaseId,
                GameObjectId: obj.GameObjectId,
                EntityId: entityId,
                NativePointer: nativePointer,
                LayoutId: layoutId,
                GimmickId: gimmickId,
                EventState: eventState,
                EventId: eventId,
                RenderFlags: renderFlags,
                NamePlateIconId: namePlateIconId,
                Targetable: obj.IsTargetable,
                Position: Format(obj.Position),
                Distance: float.IsNaN(distance) ? "n/a" : distance.ToString("0.00", CultureInfo.InvariantCulture),
                DrawSignature: drawSignature,
                ChildDrawSignature: childDrawSignature,
                GraphicKey: graphicKey,
                IsHighLow: IsHighLowName(name)));
        }

        return rows
            .OrderByDescending(static x => x.IsHighLow)
            .ThenBy(static x => x.Kind)
            .ThenBy(static x => x.BaseId)
            .ThenBy(static x => x.GameObjectId)
            .ThenBy(static x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Take(80)
            .ToList();
    }

    private static bool ShouldCaptureObject(IGameObject obj, string name, float distance)
    {
        if (!float.IsNaN(distance) && distance > NearbyRadius)
            return false;

        if (IsHighLowName(name))
            return true;

        if (obj.BaseId == 2007457)
            return true;

        if (ContainsAny(name, "treasure", "coffer", "stage", "door", "card", "lure"))
            return true;

        if (string.IsNullOrWhiteSpace(name)
            && obj.ObjectKind is ObjectKind.EventObj or ObjectKind.EventNpc)
        {
            return true;
        }

        return false;
    }

    private static bool IsHighLowName(string name)
        => string.Equals(name, "High", StringComparison.OrdinalIgnoreCase)
           || string.Equals(name, "Low", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsAny(string value, params string[] needles)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return needles.Any(needle => value.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsHigherLowerPrompt(string value)
        => ContainsAny(value, "Guess higher?", "Guess lower?", "higher or lower", "higher/lower");

    private static IEnumerable<WorldObjectSnapshot> ResolveBoardCandidates(IReadOnlyList<WorldObjectSnapshot> objects)
        => objects
            .Where(static x => !string.IsNullOrWhiteSpace(x.GraphicKey)
                               && x.Kind == ObjectKind.EventObj
                               && x.BaseId == 2007457
                               && string.IsNullOrWhiteSpace(x.Name))
            .OrderBy(static x => ParsePositionX(x.Position))
            .ThenBy(static x => x.GameObjectId)
            .Take(2);

    private static IEnumerable<WorldObjectSnapshot> ResolveLiveCardSlotCandidates(IReadOnlyList<WorldObjectSnapshot> objects)
        => objects
            .Where(static x => x.Kind == ObjectKind.EventObj
                               && x.BaseId == 2007457
                               && string.IsNullOrWhiteSpace(x.Name))
            .OrderBy(static x => ParsePositionX(x.Position))
            .ThenBy(static x => x.GameObjectId);

    private static float ParsePositionX(string position)
    {
        var comma = position.IndexOf(',', StringComparison.Ordinal);
        return comma <= 0 || !float.TryParse(position[..comma], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            ? 0f
            : x;
    }

    private static bool TryParsePosition(string position, out Vector3 result)
    {
        result = Vector3.Zero;
        var parts = (position ?? string.Empty).Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 3
            || !float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
            || !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
        {
            return false;
        }

        result = new Vector3(x, y, z);
        return true;
    }

    private static unsafe string BuildGraphicKey(GameObject* native, IGameObject obj, DrawObject* drawObject, string drawSignature, string childDrawSignature)
    {
        if (native == null)
            return string.Empty;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"base={obj.BaseId};kind={obj.ObjectKind};layout={native->LayoutId};gimmick={native->GimmickId};event={native->EventId.Id};draw={drawSignature};child={childDrawSignature}");
    }

    private static unsafe string DescribeDrawObject(DrawObject* drawObject)
    {
        if (drawObject == null)
            return "ptr=0x0";

        return string.Create(
            CultureInfo.InvariantCulture,
            $"ptr=0x{(nint)drawObject:X} type={drawObject->Object.GetObjectType()} flags=0x{drawObject->Flags:X8} outline=0x{drawObject->OutlineFlags:X8} " +
            $"pos={Format(drawObject->Object.Position)} scale={Format(drawObject->Object.Scale)}");
    }

    private static unsafe string DescribeChildDrawObjects(DrawObject* drawObject)
    {
        if (drawObject == null)
            return "none";

        var firstChild = drawObject->Object.ChildObject;
        if (firstChild == null)
            return "none";

        var parts = new List<string>(4);
        var child = firstChild;
        for (var i = 0; child != null && i < 4; i++, child = child->NextSiblingObject)
        {
            parts.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"#{i}:ptr=0x{(nint)child:X}:type={child->GetObjectType()}:pos={Format(child->Position)}:scale={Format(child->Scale)}"));
        }

        return string.Join(",", parts);
    }

    private unsafe bool IsAddonVisible(string addonName)
    {
        nint ptr = gameGui.GetAddonByName(addonName, 1);
        return ptr != nint.Zero && ((AtkUnitBase*)ptr)->IsVisible;
    }

    private string ReadSelectYesnoPrompt()
        => GameInteractionHelper.TryGetSelectYesNoPromptText(gameGui, out var promptText)
            ? promptText
            : string.Empty;

    private unsafe string BuildAddonSignature(AtkUnitBase* addon)
    {
        var builder = new StringBuilder();
        builder.Append(CultureInfo.InvariantCulture, $"addon:{(nint)addon:X}:{addon->IsVisible}:{addon->AtkValuesCount}:");
        var cards = DecodeAddonCards(addon);
        builder.Append(CultureInfo.InvariantCulture, $"atk4={cards.CurrentCardText}:atk5={cards.OtherCardText}:");
        if (addon->RootNode != null)
            AppendNodeSignature(addon->RootNode, builder, 0, new HashSet<nint>(), 0);

        return builder.ToString();
    }

    private unsafe void AppendNodeSignature(AtkResNode* node, StringBuilder builder, int depth, HashSet<nint> visited, int count)
    {
        if (node == null || depth > MaxTreeDepth || count > MaxTreeNodes || !visited.Add((nint)node))
            return;

        builder.Append(CultureInfo.InvariantCulture, $"{node->NodeId}:{node->Type}:{node->IsVisible()}:{node->NodeFlags}:{node->Width}x{node->Height}:");
        if (node->Type == NodeType.Text)
            builder.Append(((AtkTextNode*)node)->NodeText.ToString().Trim());
        else if (node->Type == NodeType.Image)
            builder.Append(CultureInfo.InvariantCulture, $"{ReadPartsListId(((AtkImageNode*)node)->PartsList)}.{((AtkImageNode*)node)->PartId}");
        else if (node->Type == NodeType.NineGrid)
            builder.Append(CultureInfo.InvariantCulture, $"{ReadPartsListId(((AtkNineGridNode*)node)->PartsList)}.{((AtkNineGridNode*)node)->PartId}");

        builder.Append('|');
        var childCount = count + 1;
        for (var child = node->ChildNode; child != null && childCount < MaxTreeNodes; child = child->NextSiblingNode)
        {
            AppendNodeSignature(child, builder, depth + 1, visited, childCount);
            childCount++;
        }
    }

    private static unsafe int CountEvents(AtkResNode* node)
    {
        var count = 0;
        for (var evt = node->AtkEventManager.Event; evt != null && count < 64; evt = evt->NextEvent)
            count++;

        return count;
    }

    private static unsafe uint ReadPartsListId(AtkUldPartsList* partsList)
        => partsList == null ? 0 : partsList->Id;

    private static unsafe uint ReadPartsListCount(AtkUldPartsList* partsList)
        => partsList == null ? 0 : partsList->PartCount;

    private static unsafe string BuildImageResourceKey(
        AtkImageNode* imageNode,
        AtkUldPart* selectedPart,
        AtkUldAsset* asset,
        AtkTextureResource* resource,
        string fileName)
    {
        if (imageNode == null)
            return string.Empty;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"addon-image:parts={ReadPartsListId(imageNode->PartsList)}.{imageNode->PartId};asset={(asset == null ? 0u : asset->Id)};icon={(resource == null ? 0u : resource->IconId)};texHash=0x{(resource == null ? 0u : resource->TexPathHash):X8};file={fileName};rect={DescribeUldPart(selectedPart)}");
    }

    private static unsafe string DescribeUldPart(AtkUldPart* part)
    {
        if (part == null)
            return "none";

        return string.Create(
            CultureInfo.InvariantCulture,
            $"u={part->U} v={part->V} w={part->Width} h={part->Height}");
    }

    private static unsafe string ReadResourceFileName(ResourceHandle* handle)
    {
        if (handle == null)
            return string.Empty;

        try
        {
            return handle->FileName.ToString();
        }
        catch
        {
            return "<unreadable>";
        }
    }

    private static string ReadAtkValue(AtkValue value)
    {
        try
        {
            return value.GetValueAsString();
        }
        catch
        {
            return "<unreadable>";
        }
    }

    private static unsafe int GetStateNodeByteLength(AtkResNode* node)
    {
        if (node == null)
            return 0;

        return node->Type switch
        {
            NodeType.Text => sizeof(AtkTextNode),
            NodeType.Image => sizeof(AtkImageNode),
            NodeType.NineGrid => sizeof(AtkNineGridNode),
            _ when (int)node->Type >= 1000 => sizeof(AtkComponentNode),
            _ => sizeof(AtkResNode),
        };
    }

    private unsafe void DumpMemoryFingerprint(string label, void* pointer, int declaredLength, IReadOnlyList<int> cardValues)
    {
        if (pointer == null || declaredLength <= 0)
        {
            WriteLine($"{Prefix} hldbg-state-memory label='{Escape(label)}' ptr=0x0 len=0");
            return;
        }

        var length = Math.Min(declaredLength, MaxStateProbeBytes);
        var bytes = (byte*)pointer;
        WriteLine(
            $"{Prefix} hldbg-state-memory label='{Escape(label)}' ptr=0x{(nint)pointer:X} len={length} declaredLen={declaredLength} " +
            $"hash=0x{HashMemory(bytes, length):X16} firstBytes='{FormatHexPrefix(bytes, length, 64)}' " +
            $"cardNeedles='{Escape(FindCardNeedles(bytes, length, cardValues))}'");
    }

    private static unsafe ulong HashMemory(byte* bytes, int length)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;

        var hash = offset;
        for (var i = 0; i < length; i++)
        {
            hash ^= bytes[i];
            hash *= prime;
        }

        return hash;
    }

    private static unsafe string FormatHexPrefix(byte* bytes, int length, int maxBytes)
    {
        var count = Math.Min(length, maxBytes);
        if (count <= 0)
            return string.Empty;

        var builder = new StringBuilder(count * 3);
        for (var i = 0; i < count; i++)
        {
            if (i > 0)
                builder.Append(' ');
            builder.Append(bytes[i].ToString("X2", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    private static unsafe string FindCardNeedles(byte* bytes, int length, IReadOnlyList<int> cardValues)
    {
        if (cardValues.Count == 0 || length <= 0)
            return "none";

        var tokens = new List<string>(MaxStateNeedleTokens);
        var capped = false;

        void Add(string token)
        {
            if (tokens.Count >= MaxStateNeedleTokens)
            {
                capped = true;
                return;
            }

            tokens.Add(token);
        }

        for (var i = 0; i + 3 < length && !capped; i++)
        {
            var value = ReadLe32(bytes + i);
            foreach (var card in cardValues)
            {
                if (value == card)
                    Add(string.Create(CultureInfo.InvariantCulture, $"u32+0x{i:X}={card}"));
            }
        }

        for (var i = 0; i + 1 < length && !capped; i++)
        {
            var value = ReadLe16(bytes + i);
            foreach (var card in cardValues)
            {
                if (value == card)
                    Add(string.Create(CultureInfo.InvariantCulture, $"u16+0x{i:X}={card}"));
            }
        }

        for (var i = 0; i < length && !capped; i++)
        {
            foreach (var card in cardValues)
            {
                if (bytes[i] == '0' + card)
                    Add(string.Create(CultureInfo.InvariantCulture, $"ascii+0x{i:X}={card}"));
            }
        }

        for (var i = 0; i < length && !capped; i++)
        {
            foreach (var card in cardValues)
            {
                if (bytes[i] == card)
                    Add(string.Create(CultureInfo.InvariantCulture, $"u8+0x{i:X}={card}"));
            }
        }

        if (tokens.Count == 0)
            return "none";

        if (capped)
            tokens.Add("more");

        return string.Join(",", tokens);
    }

    private static unsafe ushort ReadLe16(byte* bytes)
        => (ushort)(bytes[0] | (bytes[1] << 8));

    private static unsafe uint ReadLe32(byte* bytes)
        => (uint)(bytes[0] | (bytes[1] << 8) | (bytes[2] << 16) | (bytes[3] << 24));

    private static IReadOnlyList<int> GetKnownBoardCardValues(KnownBoardTag? knownBoard)
    {
        if (knownBoard == null)
            return Array.Empty<int>();

        var values = new List<int>(2);
        AddKnownCardValue(knownBoard.LeftCard, values);
        AddKnownCardValue(knownBoard.RightCard, values);
        return values;
    }

    private static void AddKnownCardValue(string token, List<int> values)
    {
        if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var card) || card is < 1 or > 9)
            return;

        if (!values.Contains(card))
            values.Add(card);
    }

    private static string Format(Vector3 value)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{value.X:0.00},{value.Y:0.00},{value.Z:0.00}");

    private static string Escape(string value)
        => value.Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal);

    private static string EscapeToken(string value)
        => string.IsNullOrWhiteSpace(value)
            ? "none"
            : Escape(value).Replace(' ', '_');

    private static bool IsKnownCardAvfxPath(string path, string normalizedPath)
    {
        var value = string.IsNullOrWhiteSpace(normalizedPath)
            ? HigherLowerCardVfxCatalog.NormalizePath(path)
            : HigherLowerCardVfxCatalog.NormalizePath(normalizedPath);
        return HigherLowerCardVfxCatalog.TryGetCatalog(value, out _);
    }

    private static string FormatDatamineCardTexturePaths(IReadOnlyList<HigherLowerCardVfxSolverService.CardTexturePathMatch> cardTexturePaths)
    {
        if (cardTexturePaths.Count == 0)
            return "none";

        return string.Join(
            ";",
            cardTexturePaths.Select(static x => $"Tex[{x.TextureIndex}]={x.TexturePath}:{x.Pair}"));
    }

    private static uint HashText(string value)
    {
        unchecked
        {
            var hash = 2166136261u;
            foreach (var ch in value)
            {
                hash ^= ch;
                hash *= 16777619u;
            }

            return hash;
        }
    }

    private KnownCardTag? ConsumeKnownCardTag()
    {
        if (knownCardTag == null)
            return null;

        var age = DateTime.UtcNow - knownCardTag.CreatedUtc;
        if (age > KnownCardTagTtl || knownCardTag.EmittedSnapshots >= KnownCardTagMaxSnapshots)
        {
            knownCardTag = null;
            return null;
        }

        var current = knownCardTag;
        knownCardTag = current with { EmittedSnapshots = current.EmittedSnapshots + 1 };
        return current;
    }

    private KnownBoardTag? ConsumeKnownBoardTag()
    {
        if (knownBoardTag == null)
            return null;

        var age = DateTime.UtcNow - knownBoardTag.CreatedUtc;
        if (age > KnownBoardTagTtl || knownBoardTag.EmittedSnapshots >= KnownBoardTagMaxSnapshots)
        {
            knownBoardTag = null;
            return null;
        }

        var current = knownBoardTag;
        knownBoardTag = current with { EmittedSnapshots = current.EmittedSnapshots + 1 };
        return current;
    }

    private static string FormatKnownCardFields(KnownCardTag? tag)
    {
        if (tag == null)
            return "knownCard=none knownCardRole=none tagAgeMs=none tagSeq=none ";

        var ageMs = Math.Max(0, (int)(DateTime.UtcNow - tag.CreatedUtc).TotalMilliseconds);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"knownCard={tag.Card} knownCardRole={tag.Role} tagAgeMs={ageMs} tagSeq={tag.Sequence} ");
    }

    private static string FormatKnownBoardFields(KnownBoardTag? tag)
    {
        if (tag == null)
            return "knownLeftCard=none knownRightCard=none knownBoardLabel=none boardTagAgeMs=none boardTagSeq=none ";

        var ageMs = Math.Max(0, (int)(DateTime.UtcNow - tag.CreatedUtc).TotalMilliseconds);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"knownLeftCard={tag.LeftCard} knownRightCard={tag.RightCard} knownBoardLabel='{Escape(tag.Label)}' boardTagAgeMs={ageMs} boardTagSeq={tag.Sequence} ");
    }

    private void EnsureCardMapLoaded()
    {
        if (cardMapLoaded)
            return;

        cardMapLoaded = true;
        try
        {
            if (!File.Exists(cardMapPath))
                return;

            var loaded = JsonSerializer.Deserialize<HigherLowerCardMap>(File.ReadAllText(cardMapPath));
            if (loaded != null)
                cardMap = loaded.Normalize();
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"{Prefix} card map load failed path='{cardMapPath}'.");
            cardMap = new HigherLowerCardMap();
        }
    }

    private void LearnCardGraphic(int card, string graphicKey, string source)
    {
        EnsureCardMapLoaded();
        if (card is < 1 or > 9 || string.IsNullOrWhiteSpace(graphicKey))
            return;

        if (IsUnsafeGraphicKey(graphicKey))
        {
            WriteLine($"{Prefix} learn-card blocked reason=unsafe-slot-key card={card} graphicKey='{Escape(graphicKey)}' source='{Escape(source)}'");
            return;
        }

        if (cardMap.GraphicToCard.TryGetValue(graphicKey, out var conflicting) && conflicting != card)
        {
            WriteLine($"{Prefix} learn-card blocked reason=duplicate-graphic-key card={card} existing={conflicting} graphicKey='{Escape(graphicKey)}' source='{Escape(source)}'");
            return;
        }

        if (cardMap.GraphicToCard.TryGetValue(graphicKey, out var existing) && existing == card)
            return;

        cardMap.GraphicToCard[graphicKey] = card;
        cardMap.Sources[graphicKey] = source;
        cardMap.UpdatedUtc = DateTime.UtcNow;
        cardMapDirty = true;
        WriteLine($"{Prefix} learned-card card={card} graphicKey='{Escape(graphicKey)}' source='{Escape(source)}'");
        InferSequentialCardGraphics();
    }

    private static bool IsUnsafeGraphicKey(string graphicKey)
        => string.IsNullOrWhiteSpace(graphicKey)
           || !IsAuthoritativeCardFaceKey(graphicKey)
           || graphicKey.Contains("draw=ptr=0x0", StringComparison.OrdinalIgnoreCase)
           || graphicKey.Contains("NeedGreed.tex", StringComparison.OrdinalIgnoreCase);

    private static bool IsAuthoritativeCardFaceKey(string graphicKey)
        => graphicKey.StartsWith("addon-image:", StringComparison.Ordinal);

    private void InferSequentialCardGraphics()
    {
        if (!TryGetGraphicKeyForCard(1, out var key1)
            || !TryGetGraphicKeyForCard(3, out var key3)
            || !TryGetGraphicKeyForCard(7, out var key7))
        {
            return;
        }

        if (!TryInferNumericTemplate(key1, key3, key7, out var template, out var valueForCard1))
            return;

        for (var card = 1; card <= 9; card++)
        {
            var inferredKey = string.Format(CultureInfo.InvariantCulture, template, valueForCard1 + card - 1);
            if (cardMap.GraphicToCard.TryGetValue(inferredKey, out var existing) && existing != card)
            {
                WriteLine($"{Prefix} infer-card-map blocked duplicate graphicKey='{Escape(inferredKey)}' existing={existing} inferred={card}");
                return;
            }
        }

        for (var card = 1; card <= 9; card++)
        {
            var inferredKey = string.Format(CultureInfo.InvariantCulture, template, valueForCard1 + card - 1);
            if (!cardMap.GraphicToCard.ContainsKey(inferredKey))
            {
                cardMap.GraphicToCard[inferredKey] = card;
                cardMap.Sources[inferredKey] = "inferred-sequential-from-1-3-7";
                cardMapDirty = true;
                WriteLine($"{Prefix} inferred-card card={card} graphicKey='{Escape(inferredKey)}'");
            }
        }
    }

    private bool TryGetGraphicKeyForCard(int card, out string graphicKey)
    {
        foreach (var pair in cardMap.GraphicToCard)
        {
            if (pair.Value == card)
            {
                graphicKey = pair.Key;
                return true;
            }
        }

        graphicKey = string.Empty;
        return false;
    }

    private static bool TryInferNumericTemplate(string key1, string key3, string key7, out string template, out long valueForCard1)
    {
        template = string.Empty;
        valueForCard1 = 0;
        var nums1 = ExtractNumberSpans(key1);
        var nums3 = ExtractNumberSpans(key3);
        var nums7 = ExtractNumberSpans(key7);
        if (nums1.Count != nums3.Count || nums1.Count != nums7.Count)
            return false;

        for (var i = 0; i < nums1.Count; i++)
        {
            var (start, length, value1) = nums1[i];
            var (_, length3, value3) = nums3[i];
            var (_, length7, value7) = nums7[i];
            if (length != length3 || length != length7 || value3 - value1 != 2 || value7 - value1 != 6)
                continue;

            var candidateTemplate = key1.Remove(start, length).Insert(start, "{0}");
            if (string.Format(CultureInfo.InvariantCulture, candidateTemplate, value3) == key3
                && string.Format(CultureInfo.InvariantCulture, candidateTemplate, value7) == key7)
            {
                template = candidateTemplate;
                valueForCard1 = value1;
                return true;
            }
        }

        return false;
    }

    private static List<(int Start, int Length, long Value)> ExtractNumberSpans(string value)
    {
        var result = new List<(int, int, long)>();
        var index = 0;
        while (index < value.Length)
        {
            if (!char.IsDigit(value[index]))
            {
                index++;
                continue;
            }

            var start = index;
            while (index < value.Length && char.IsDigit(value[index]))
                index++;

            if (long.TryParse(value.AsSpan(start, index - start), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                result.Add((start, index - start, number));
        }

        return result;
    }

    private void SaveCardMap()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cardMapPath)!);
            File.WriteAllText(cardMapPath, JsonSerializer.Serialize(cardMap.Normalize(), new JsonSerializerOptions { WriteIndented = true }));
            cardMapDirty = false;
            WriteLine($"{Prefix} saved-card-map path='{Escape(cardMapPath)}' entries={cardMap.GraphicToCard.Count}");
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"{Prefix} card map save failed path='{cardMapPath}'.");
        }
    }

    public static string NormalizeKnownCardRole(string? role)
    {
        var normalized = (role ?? "current").Trim().ToLowerInvariant();
        return normalized switch
        {
            "" or "current" => "current",
            "next" => "next",
            "previous" or "prev" => "previous",
            _ => string.Empty,
        };
    }

    public static string NormalizeKnownBoardCardToken(string? token)
    {
        var normalized = (token ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "blank" => "blank",
            "unknown" => "unknown",
            _ when int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var card)
                   && card is >= 1 and <= 9 => card.ToString(CultureInfo.InvariantCulture),
            _ => string.Empty,
        };
    }

    private sealed record KnownCardTag(
        int Card,
        string Role,
        DateTime CreatedUtc,
        int Sequence,
        int EmittedSnapshots);

    private sealed record KnownBoardTag(
        string LeftCard,
        string RightCard,
        string Label,
        DateTime CreatedUtc,
        int Sequence,
        int EmittedSnapshots);

    public sealed record HigherLowerRuntimeState(
        bool Active,
        bool TreasureHighLowVisible,
        bool NotificationChallengeVisible,
        bool SelectYesnoVisible,
        string SelectYesnoPrompt,
        bool HighTargetable,
        bool LowTargetable,
        int? AddonCurrentCard,
        string AddonCurrentCardText,
        int? AddonOtherCard,
        string AddonOtherCardText,
        string AddonCurrentCardSource,
        string CurrentGraphicKey,
        int? CurrentCard,
        int KnownCardCount,
        bool CardSourceSafe,
        string SafetyStatus);

    public sealed record HigherLowerLiveProbe(
        HigherLowerRuntimeState Runtime,
        IReadOnlyList<HigherLowerBoardCandidate> BoardCandidates,
        IReadOnlyList<HigherLowerCardMapEntry> CardMapEntries,
        IReadOnlyList<string> SafetyWarnings,
        string DiagnosticDirectory,
        string CurrentLogPath,
        string DatamineDirectory,
        string CurrentDatamineSessionDirectory,
        bool VfxDataminingEnabled,
        string CardMapPath);

    public sealed record HigherLowerBoardCandidate(
        string Side,
        uint BaseId,
        uint LayoutId,
        uint GimmickId,
        int EventState,
        uint EventId,
        bool Targetable,
        string Position,
        string DrawPointer,
        string DrawSignature,
        string GraphicKey,
        bool Unsafe);

    public sealed record BoardSlotSnapshot(
        string Side,
        Vector3 Position,
        ulong GameObjectId,
        uint BaseId,
        uint LayoutId);

    public sealed record KnownBoardSnapshot(
        string LeftCard,
        string RightCard,
        string Label,
        int Sequence,
        DateTime CreatedUtc);

    public sealed record HigherLowerCardMapEntry(
        string GraphicKey,
        int Card,
        string Source,
        bool Unsafe);

    public sealed record HigherLowerTextureExportResult(
        bool Success,
        string Message,
        string ExportDirectory);

    public sealed record HigherLowerTraceResult(
        bool Success,
        string Message,
        string LogPath,
        double DurationSeconds);

    private sealed record TextureExportCandidate(
        string Source,
        string Path,
        uint NodeId,
        nint NodePointer,
        bool Visible,
        float ScreenX,
        float ScreenY,
        ushort Width,
        ushort Height,
        float ScaleX,
        float ScaleY,
        string Side,
        string KnownCard,
        string TexturePath,
        uint TexPathHash,
        uint IconId,
        uint PartsListId,
        uint PartCount,
        ushort PartId,
        uint AssetId,
        TextureExportRect? Rect,
        string CandidateKey);

    private sealed record TextureExportRect(
        int U,
        int V,
        int Width,
        int Height);

    private sealed record TextureConversionResult(
        bool Success,
        string Command,
        string Output)
    {
        public string Message
            => string.IsNullOrWhiteSpace(Output) ? "unknown error" : Output.Split('\n', '\r').FirstOrDefault(static x => !string.IsNullOrWhiteSpace(x)) ?? Output;
    }

    private sealed record DatamineJsonRow(
        ulong Sequence,
        DateTime TimestampUtc,
        long UnixMs,
        string Type,
        uint TerritoryId,
        object Payload);

    private sealed class TextureExportManifest
    {
        public DateTime GeneratedAtUtc { get; set; }
        public string Source { get; set; } = string.Empty;
        public uint TerritoryId { get; set; }
        public string KnownLeftCard { get; set; } = "unknown";
        public string KnownRightCard { get; set; } = "unknown";
        public string KnownBoardLabel { get; set; } = string.Empty;
        public string ManualTexturePath { get; set; } = string.Empty;
        public List<TextureExportManifestEntry> Entries { get; set; } = new();
    }

    private sealed class TextureExportManifestEntry
    {
        public int CandidateIndex { get; set; }
        public string Side { get; set; } = string.Empty;
        public string KnownCard { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public uint NodeId { get; set; }
        public string NodePointer { get; set; } = string.Empty;
        public bool Visible { get; set; }
        public float ScreenX { get; set; }
        public float ScreenY { get; set; }
        public ushort Width { get; set; }
        public ushort Height { get; set; }
        public float ScaleX { get; set; }
        public float ScaleY { get; set; }
        public string TexturePath { get; set; } = string.Empty;
        public string TexPathHash { get; set; } = string.Empty;
        public uint IconId { get; set; }
        public uint PartsListId { get; set; }
        public uint PartCount { get; set; }
        public ushort PartId { get; set; }
        public uint AssetId { get; set; }
        public TextureExportRect? Rect { get; set; }
        public string CandidateKey { get; set; } = string.Empty;
        public string RawTexPath { get; set; } = string.Empty;
        public long RawTexSize { get; set; }
        public string AtlasPngPath { get; set; } = string.Empty;
        public string CropPngPath { get; set; } = string.Empty;
        public string Error { get; set; } = string.Empty;
    }

    private sealed class HigherLowerCardMap
    {
        public Dictionary<string, int> GraphicToCard { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> Sources { get; set; } = new(StringComparer.Ordinal);
        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

        public HigherLowerCardMap Normalize()
        {
            GraphicToCard = GraphicToCard
                .Where(static x => !IsUnsafeGraphicKey(x.Key) && x.Value is >= 1 and <= 9)
                .GroupBy(static x => x.Key, StringComparer.Ordinal)
                .ToDictionary(static x => x.Key, static x => x.Last().Value, StringComparer.Ordinal);
            Sources = Sources
                .Where(x => GraphicToCard.ContainsKey(x.Key))
                .GroupBy(static x => x.Key, StringComparer.Ordinal)
                .ToDictionary(static x => x.Key, static x => x.Last().Value, StringComparer.Ordinal);
            return this;
        }
    }

    private sealed record Snapshot(
        bool Active,
        uint TerritoryId,
        DutyContextSnapshot Context,
        ObservationSnapshot Observation,
        PlannerSnapshot Planner,
        string DialogStatus,
        nint TreasureHighLowPointer,
        bool TreasureHighLowVisible,
        bool NotificationChallengeVisible,
        bool SelectYesnoVisible,
        string SelectYesnoPrompt,
        int? AddonCurrentCard,
        string AddonCurrentCardText,
        int? AddonOtherCard,
        string AddonOtherCardText,
        string AddonCurrentCardSource,
        bool HighLowTargetable,
        IReadOnlyList<WorldObjectSnapshot> WorldObjects,
        string Signature,
        string AddonSignature,
        string WorldSignature);

    private sealed record AddonCardDecode(
        int? CurrentCard,
        string CurrentCardText,
        int? OtherCard,
        string OtherCardText,
        string CurrentCardSource)
    {
        public static AddonCardDecode None { get; } = new(null, "unavailable", null, "unavailable", "none");
    }

    private readonly record struct AddonCardValue(int? Card, string Text, bool Available);

    private sealed record WorldObjectSnapshot(
        string Name,
        ObjectKind Kind,
        uint BaseId,
        ulong GameObjectId,
        uint EntityId,
        nint NativePointer,
        uint LayoutId,
        uint GimmickId,
        int EventState,
        uint EventId,
        uint RenderFlags,
        uint NamePlateIconId,
        bool Targetable,
        string Position,
        string Distance,
        string DrawSignature,
        string ChildDrawSignature,
        string GraphicKey,
        bool IsHighLow)
    {
        public string Signature
            => $"{Kind}:{BaseId}:{GameObjectId:X}:{Targetable}:{Position}:{Name}:{GraphicKey}";
    }
}
