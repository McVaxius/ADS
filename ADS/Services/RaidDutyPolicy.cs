using System.Text.Json;
using ADS.Models;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace ADS.Services;

internal readonly record struct RaidDutyState(
    bool Enabled, bool Readable, string? SessionId, uint TerritoryTypeId,
    uint ContentFinderConditionId, bool AdsLootEnabled, bool AutomaticExitAllowedNow,
    bool AwaitingCountdown = false, bool AutomaticStartAllowedNow = false, long RecoveryGeneration = 0,
    long? CountdownRevision = null)
{
    internal bool Matches(DutyContextSnapshot context)
        => Enabled && Readable && context.IsLoggedIn && context.InInstancedDuty
           && context.TerritoryTypeId != 0 && context.ContentFinderConditionId != 0
           && TerritoryTypeId == context.TerritoryTypeId
           && ContentFinderConditionId == context.ContentFinderConditionId;
}

// Only the current FR session is retained, so a transient IPC failure cannot
// turn a known Raid hold into permission to leave or roll. No settings are saved.
internal sealed class RaidDutyPolicy
{
    internal Func<string?> QueryRuntimeStateJson { get; set; } = static () => null;
    private RaidDutyState current;
    private RaidDutyState Withheld => current with
    {
        Readable = false, AdsLootEnabled = false, AutomaticExitAllowedNow = false,
        AutomaticStartAllowedNow = false, AwaitingCountdown = true,
    };

    internal RaidDutyState Read(DutyContextSnapshot context)
    {
        if (!context.IsLoggedIn)
        {
            current = default;
            return current;
        }
        if (!context.IsUnsafeTransition && current.Enabled
            && (!context.InInstancedDuty || current.TerritoryTypeId != context.TerritoryTypeId
                || current.ContentFinderConditionId != context.ContentFinderConditionId))
            current = default;

        try
        {
            var json = QueryRuntimeStateJson();
            if (string.IsNullOrWhiteSpace(json))
                return Withheld;
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.GetProperty("schemaVersion").GetInt32() != 1)
                throw new JsonException("Unsupported Raid runtime schema.");
            if (!root.GetProperty("enabled").GetBoolean())
            {
                current = default(RaidDutyState) with { Readable = true };
                return current;
            }

            current = current with
            {
                Enabled = true, Readable = false, AdsLootEnabled = false,
                AutomaticExitAllowedNow = false, AutomaticStartAllowedNow = false,
            };

            var session = root.GetProperty("sessionId").GetString();
            if (string.IsNullOrWhiteSpace(session) || session.Length > 128)
                throw new JsonException("Invalid Raid session.");
            current = new RaidDutyState(true, false, session,
                root.GetProperty("territoryTypeId").GetUInt32(),
                root.GetProperty("contentFinderConditionId").GetUInt32(), false, false);
            if (!root.GetProperty("sourceReadable").GetBoolean())
                return Withheld;
            var recoveryGeneration = root.GetProperty("recoveryGeneration").GetInt64();
            if (recoveryGeneration < 0)
                throw new JsonException("Invalid Raid recovery generation.");
            long? countdownRevision = null;
            if (root.TryGetProperty("countdownRevision", out var revision))
            {
                if (!revision.TryGetInt64(out var number) || number < 0)
                    throw new JsonException("Invalid Raid countdown revision.");
                countdownRevision = number;
            }
            current = current with
            {
                Readable = true,
                AdsLootEnabled = root.GetProperty("adsLootEnabled").GetBoolean(),
                AutomaticExitAllowedNow = root.GetProperty("automaticExitAllowedNow").GetBoolean(),
                AwaitingCountdown = root.GetProperty("awaitingCountdown").GetBoolean(),
                AutomaticStartAllowedNow = root.GetProperty("automaticStartAllowedNow").GetBoolean(),
                RecoveryGeneration = recoveryGeneration,
                CountdownRevision = countdownRevision,
            };
            return current;
        }
        catch
        {
            return Withheld;
        }
    }

    internal static bool IsOutstanding(LootItem item)
        => item.ItemId != 0 && item.ChestObjectId is not (0 or 0xE0000000)
           && item.RollResult == RollResult.UnAwarded;

    internal static unsafe bool? ReadOutstandingLoot()
    {
        var loot = Loot.Instance();
        if (loot == null)
            return null;
        foreach (var item in loot->Items)
            if (IsOutstanding(item))
                return true;
        return false;
    }
}
