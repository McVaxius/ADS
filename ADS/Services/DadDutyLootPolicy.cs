using System.Text.Json;
using ADS.Models;

namespace ADS.Services;

// A live view of the exact DAD session. This never writes Configuration or retains an
// override after the provider disappears, stops, or moves to another duty.
internal sealed class DadDutyLootPolicy
{
    internal const string ValidationChannel = "ADS.Duty.ValidateLootPolicy";
    internal const string StateChannel = "dad.Duty.GetLootPolicyJson";
    internal Func<string?> QueryRuntimeStateJson { get; set; } = static () => null;
    internal Action<string> Log { get; set; } = static _ => { };
    private State? lastState;
    private string? lastFailure;

    internal sealed record State(string SessionId, uint TerritoryTypeId, uint ContentFinderConditionId,
        bool LootTreasure, bool LootBossTreasureOnly, string LootMethod)
    {
        internal bool OpenChests => LootTreasure && (LootMethod is "AutoDuty" or "All");
    }

    internal static string ValidateJson(string json)
    {
        try
        {
            if (Parse(json) == null)
                throw new JsonException("An active DAD duty loot policy is required.");
            return JsonSerializer.Serialize(new { schemaVersion = 1, success = true, message = "Temporary DAD loot policy supported." });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { schemaVersion = 1, success = false, message = ex.Message });
        }
    }

    internal State? Read(DutyContextSnapshot context)
    {
        State? next = null;
        try
        {
            var json = QueryRuntimeStateJson();
            var state = string.IsNullOrWhiteSpace(json) ? null : Parse(json);
            if (state != null && context.IsLoggedIn && context.InInstancedDuty && !context.IsUnsafeTransition &&
                state.TerritoryTypeId == context.TerritoryTypeId &&
                state.ContentFinderConditionId == context.ContentFinderConditionId)
                next = state;
            lastFailure = null;
        }
        catch (Exception ex)
        {
            if (lastFailure != ex.Message)
                Log($"DAD duty loot policy unavailable; using saved ADS preferences: {ex.Message}");
            lastFailure = ex.Message;
        }

        if (next != lastState)
        {
            Log(next == null
                ? "DAD duty loot override released; using current saved ADS preferences."
                : $"DAD duty loot override session={next.SessionId} duty={next.ContentFinderConditionId} " +
                  $"treasure={next.LootTreasure} bossOnly={next.LootBossTreasureOnly} method={next.LootMethod}; saved preferences unchanged.");
            lastState = next;
        }
        return next;
    }

    private static State? Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.GetProperty("schemaVersion").GetInt32() != 1)
            throw new JsonException("Unsupported DAD duty loot policy version.");
        if (!root.GetProperty("active").GetBoolean())
            return null;
        var session = root.GetProperty("sessionId").GetString();
        var territory = root.GetProperty("territoryTypeId").GetUInt32();
        var duty = root.GetProperty("contentFinderConditionId").GetUInt32();
        var treasure = root.GetProperty("lootTreasure").GetBoolean();
        var bossOnly = root.GetProperty("lootBossTreasureOnly").GetBoolean();
        var method = root.GetProperty("lootMethodEnum").GetString();
        if (string.IsNullOrWhiteSpace(session) || session.Length > 128 || territory == 0 || duty == 0)
            throw new JsonException("DAD duty loot policy requires a session and exact territory/CFC identity.");
        if (method is not ("AutoDuty" or "Pandora" or "All"))
            throw new JsonException("Unsupported DAD loot method; expected AutoDuty, Pandora or All.");
        // ADS has no reliable boss-coffer classifier. Reject this combination during
        // DAD's preflight instead of silently opening non-boss coffers. WigglyQuest's
        // forced gearing contract explicitly requests bossOnly=false.
        if (treasure && bossOnly && (method is "AutoDuty" or "All"))
            throw new JsonException("ADS does not support boss-only chest selection; set LootBossTreasureOnly=False.");
        return new State(session, territory, duty, treasure, bossOnly, method);
    }
}
