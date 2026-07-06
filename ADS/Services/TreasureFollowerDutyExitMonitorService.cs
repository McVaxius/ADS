using System.Globalization;
using ADS.Models;
using Dalamud.Game.Command;
using Dalamud.Plugin.Services;

namespace ADS.Services;

public sealed class TreasureFollowerDutyExitMonitorService
{
    private static readonly TimeSpan StableOutsideDelay = TimeSpan.FromSeconds(1);
    private static readonly string[] CleanupCommands =
    [
        "/bmrai followoutofcombat off",
        "/cbt disable AutoFollow",
        "/bmrai followcombat off",
        "/vbmai follow Slot1",
    ];

    private readonly ICommandManager commandManager;
    private readonly IPluginLog log;
    private DateTime outsideStableSinceUtc = DateTime.MinValue;

    public TreasureFollowerDutyExitMonitorService(ICommandManager commandManager, IPluginLog log)
    {
        this.commandManager = commandManager;
        this.log = log;
    }

    public bool Armed { get; private set; }
    public bool CleanupSent { get; private set; }
    public DateTime? ArmedAtUtc { get; private set; }
    public DateTime? CleanupSentAtUtc { get; private set; }
    public string DutyKey { get; private set; } = string.Empty;
    public string Source { get; private set; } = string.Empty;
    public string Status { get; private set; } = "Treasure follower exit monitor idle.";

    public void Update(
        DutyContextSnapshot context,
        bool supportedTreasureDuty,
        TreasureDungeonRole role,
        string roleDisplayName)
    {
        if (context.PluginEnabled
            && context.IsLoggedIn
            && supportedTreasureDuty
            && role == TreasureDungeonRole.Follower)
        {
            Arm(context, $"auto {roleDisplayName}", logIfAlreadyArmed: false);
        }

        if (!Armed)
            return;

        if (!context.IsLoggedIn)
        {
            outsideStableSinceUtc = DateTime.MinValue;
            Status = "Treasure follower exit monitor armed; waiting for login.";
            return;
        }

        if (context.IsUnsafeTransition)
        {
            outsideStableSinceUtc = DateTime.MinValue;
            Status = "Treasure follower exit monitor armed; waiting for loading to clear.";
            return;
        }

        if (context.InInstancedDuty)
        {
            outsideStableSinceUtc = DateTime.MinValue;
            Status = $"Treasure follower exit monitor armed inside {context.CurrentDuty?.EnglishName ?? "duty"}.";
            return;
        }

        var now = DateTime.UtcNow;
        if (outsideStableSinceUtc == DateTime.MinValue)
        {
            outsideStableSinceUtc = now;
            Status = "Treasure follower exit monitor saw outside duty; waiting for stable outside state.";
            return;
        }

        var stableFor = now - outsideStableSinceUtc;
        if (stableFor < StableOutsideDelay)
        {
            Status = $"Treasure follower exit monitor outside duty for {stableFor.TotalSeconds:0.0}s; waiting for {StableOutsideDelay.TotalSeconds:0.0}s.";
            return;
        }

        SendCleanupOnce("stable outside duty without /ads leave");
    }

    public void Arm(DutyContextSnapshot context, string source)
        => Arm(context, source, logIfAlreadyArmed: true);

    public void Disarm(string reason)
    {
        outsideStableSinceUtc = DateTime.MinValue;
        if (!Armed)
        {
            Status = $"Treasure follower exit monitor already disarmed after {reason}.";
            return;
        }

        Armed = false;
        Status = $"Treasure follower exit monitor disarmed after {reason}.";
        log.Information($"[ADS] {Status}");
    }

    private void Arm(DutyContextSnapshot context, string source, bool logIfAlreadyArmed)
    {
        var dutyKey = BuildDutyKey(context);
        if (Armed && string.Equals(DutyKey, dutyKey, StringComparison.Ordinal))
        {
            if (logIfAlreadyArmed)
                Status = $"Treasure follower exit monitor already armed for {context.CurrentDuty?.EnglishName ?? dutyKey}.";
            return;
        }

        Armed = true;
        CleanupSent = false;
        ArmedAtUtc = DateTime.UtcNow;
        CleanupSentAtUtc = null;
        DutyKey = dutyKey;
        Source = source;
        outsideStableSinceUtc = DateTime.MinValue;
        Status = $"Treasure follower exit monitor armed for {context.CurrentDuty?.EnglishName ?? dutyKey} from {source}.";
        log.Information($"[ADS] {Status}");
    }

    private void SendCleanupOnce(string reason)
    {
        if (CleanupSent)
            return;

        var statuses = new List<string>();
        foreach (var command in CleanupCommands)
        {
            var sent = commandManager.ProcessCommand(command);
            statuses.Add($"{command}={(sent ? "sent" : "failed")}");
        }

        CleanupSent = true;
        CleanupSentAtUtc = DateTime.UtcNow;
        Armed = false;
        outsideStableSinceUtc = DateTime.MinValue;
        Status = $"Treasure follower exit monitor sent cleanup after {reason}: {string.Join(", ", statuses)}.";
        log.Information($"[ADS] {Status}");
    }

    private static string BuildDutyKey(DutyContextSnapshot context)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{context.TerritoryTypeId}:{context.ContentFinderConditionId}");
}
