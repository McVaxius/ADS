using System.Globalization;
using System.Text.RegularExpressions;
using Dalamud.Plugin.Services;

namespace ADS.Services;

public sealed class TreasurePortalOpenerTracker
{
    private static readonly TimeSpan PendingOpenerTtl = TimeSpan.FromMinutes(10);
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex YouPlaceRegex = new(@"^You\s+place\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex NamedPlacesRegex = new(@"^(?<name>.+?)\s+places\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly IObjectTable objectTable;
    private readonly IPartyList partyList;
    private readonly IPlayerState playerState;
    private readonly IPluginLog log;

    private PendingPortalOpener? pendingPortalOpener;

    public TreasurePortalOpenerTracker(
        IObjectTable objectTable,
        IPartyList partyList,
        IPlayerState playerState,
        IPluginLog log)
    {
        this.objectTable = objectTable;
        this.partyList = partyList;
        this.playerState = playerState;
        this.log = log;
    }

    public TreasurePortalOpenerSnapshot? Current
        => pendingPortalOpener?.ToSnapshot();

    public bool HandleChatMessage(string message)
    {
        if (!TryParsePortalOpenerChat(message, out var parsed))
            return false;

        var local = CaptureLocalPlayer();
        PendingPortalOpener? opener = null;
        if (parsed.LocalPronoun)
        {
            opener = new PendingPortalOpener(
                string.IsNullOrWhiteSpace(local.DisplayName) ? "You" : local.DisplayName,
                local.DisplayName,
                FindPartySlotForLocal(local),
                local.GameObjectId,
                local.EntityId,
                local.ContentId,
                IsLocalOpener: true,
                parsed.MatchedText,
                DateTime.UtcNow);
        }
        else if (NamesMatch(parsed.OpenerName, local.DisplayName))
        {
            opener = new PendingPortalOpener(
                string.IsNullOrWhiteSpace(local.DisplayName) ? parsed.OpenerName : local.DisplayName,
                local.DisplayName,
                FindPartySlotForLocal(local),
                local.GameObjectId,
                local.EntityId,
                local.ContentId,
                IsLocalOpener: true,
                parsed.MatchedText,
                DateTime.UtcNow);
        }
        else if (TryFindPartyMember(parsed.OpenerName, local, out var partyMember))
        {
            opener = new PendingPortalOpener(
                partyMember.DisplayName,
                local.DisplayName,
                partyMember.Slot,
                partyMember.GameObjectId,
                partyMember.EntityId,
                partyMember.ContentId,
                IsLocalOpener: false,
                parsed.MatchedText,
                DateTime.UtcNow);
        }

        if (opener is null)
        {
            log.Debug($"[ADS] Ignored treasure portal opener chat because opener '{parsed.OpenerName}' did not match local player or party list. Chat='{parsed.MatchedText}'");
            return false;
        }

        pendingPortalOpener = opener;
        log.Information(
            $"[ADS] Captured treasure portal opener from chat: opener='{opener.OpenerName}', local='{FormatName(opener.LocalName)}', slot={FormatSlot(opener.PartySlot)}, contentId={FormatId(opener.ContentId)}, objectId={FormatId(opener.GameObjectId)}, entityId={FormatId(opener.EntityId)}, chat='{opener.ChatText}'.");
        return true;
    }

    public void Update()
    {
        ClearExpiredPendingOpener(DateTime.UtcNow);
        RefreshPendingOpener();
    }

    public void ClearPendingOpener(string reason)
    {
        if (pendingPortalOpener is null)
            return;

        pendingPortalOpener = null;
        log.Information($"[ADS] Cleared pending treasure portal opener after {reason}.");
    }

    private void RefreshPendingOpener()
    {
        if (pendingPortalOpener is not { } opener)
            return;

        var local = CaptureLocalPlayer();
        PendingPortalOpener? refreshed = null;
        if (opener.IsLocalOpener || NamesMatch(opener.OpenerName, local.DisplayName))
        {
            refreshed = opener with
            {
                LocalName = local.DisplayName,
                PartySlot = FindPartySlotForLocal(local) ?? opener.PartySlot,
                GameObjectId = local.GameObjectId ?? opener.GameObjectId,
                EntityId = local.EntityId ?? opener.EntityId,
                ContentId = local.ContentId ?? opener.ContentId,
                IsLocalOpener = true,
            };
        }
        else if (TryFindPartyMember(opener.OpenerName, local, out var member))
        {
            refreshed = opener with
            {
                OpenerName = member.DisplayName,
                LocalName = local.DisplayName,
                PartySlot = member.Slot,
                GameObjectId = member.GameObjectId ?? opener.GameObjectId,
                EntityId = member.EntityId ?? opener.EntityId,
                ContentId = member.ContentId ?? opener.ContentId,
            };
        }

        if (refreshed is null || refreshed == opener)
            return;

        pendingPortalOpener = refreshed;
        if (opener.ContentId is null && refreshed.ContentId is not null)
        {
            log.Information(
                $"[ADS] Resolved treasure portal opener content id: opener='{refreshed.OpenerName}', slot={FormatSlot(refreshed.PartySlot)}, contentId={FormatId(refreshed.ContentId)}.");
        }
    }

    private void ClearExpiredPendingOpener(DateTime now)
    {
        if (pendingPortalOpener is not { } opener)
            return;

        var age = now - opener.CapturedUtc;
        if (age <= PendingOpenerTtl)
            return;

        pendingPortalOpener = null;
        log.Information(
            $"[ADS] Ignored stale treasure portal opener chat after {age.TotalMinutes:0.0}m: opener='{opener.OpenerName}', chat='{opener.ChatText}'.");
    }

    private LocalPlayerIdentity CaptureLocalPlayer()
    {
        try
        {
            var player = objectTable.LocalPlayer;
            if (player is null)
                return new LocalPlayerIdentity(string.Empty, null, null, GetLocalContentId());

            return new LocalPlayerIdentity(
                NormalizeDisplayName(player.Name.TextValue),
                player.GameObjectId == 0 ? null : player.GameObjectId,
                player.EntityId == 0 ? null : player.EntityId,
                GetLocalContentId());
        }
        catch (Exception ex)
        {
            log.Debug($"[ADS] Failed to resolve local player while tracking treasure portal opener: {ex.Message}");
            return new LocalPlayerIdentity(string.Empty, null, null, GetLocalContentId());
        }
    }

    private ulong? GetLocalContentId()
    {
        try
        {
            return playerState.ContentId == 0 ? null : playerState.ContentId;
        }
        catch (Exception ex)
        {
            log.Debug($"[ADS] Failed to resolve local content id while tracking treasure portal opener: {ex.Message}");
            return null;
        }
    }

    private int? FindPartySlotForLocal(LocalPlayerIdentity local)
    {
        if (string.IsNullOrWhiteSpace(local.DisplayName)
            && local.GameObjectId is null
            && local.EntityId is null
            && local.ContentId is null)
        {
            return null;
        }

        foreach (var member in EnumeratePartyMembers(local))
        {
            if (member.IsLocal)
                return member.Slot;
        }

        return null;
    }

    private bool TryFindPartyMember(string openerName, LocalPlayerIdentity local, out PartyMemberIdentity partyMember)
    {
        foreach (var member in EnumeratePartyMembers(local))
        {
            if (NamesMatch(openerName, member.DisplayName))
            {
                partyMember = member;
                return true;
            }
        }

        partyMember = default;
        return false;
    }

    private IEnumerable<PartyMemberIdentity> EnumeratePartyMembers(LocalPlayerIdentity local)
    {
        for (var i = 0; i < partyList.Length; i++)
        {
            var member = partyList[i];
            if (member is null)
                continue;

            string displayName;
            ulong? contentId;
            ulong? entityId;
            ulong? gameObjectId;
            try
            {
                displayName = NormalizeDisplayName(member.Name.TextValue);
                contentId = member.ContentId == 0 ? null : member.ContentId;
                entityId = member.EntityId == 0 ? null : member.EntityId;
                gameObjectId = member.GameObject?.GameObjectId is { } id && id != 0 ? id : null;
            }
            catch (Exception ex)
            {
                log.Debug($"[ADS] Failed to inspect party slot {i + 1} while tracking treasure portal opener: {ex.Message}");
                continue;
            }

            if (string.IsNullOrWhiteSpace(displayName)
                && contentId is null
                && entityId is null
                && gameObjectId is null)
            {
                continue;
            }

            var isLocal = (local.ContentId is not null && local.ContentId == contentId)
                          || (local.GameObjectId is not null && local.GameObjectId == gameObjectId)
                          || (local.EntityId is not null && local.EntityId == entityId)
                          || (!string.IsNullOrWhiteSpace(local.DisplayName) && NamesMatch(local.DisplayName, displayName));
            yield return new PartyMemberIdentity(
                i + 1,
                displayName,
                contentId,
                gameObjectId,
                entityId,
                isLocal);
        }
    }

    private static bool TryParsePortalOpenerChat(string message, out ParsedPortalChat parsed)
    {
        parsed = default;
        var normalized = NormalizeDisplayName(message);
        if (string.IsNullOrWhiteSpace(normalized)
            || !ContainsWord(normalized, "hand")
            || !ContainsWord(normalized, "portal")
            || (!ContainsWord(normalized, "place") && !ContainsWord(normalized, "places")))
        {
            return false;
        }

        if (YouPlaceRegex.IsMatch(normalized))
        {
            parsed = new ParsedPortalChat(string.Empty, LocalPronoun: true, normalized);
            return true;
        }

        var namedMatch = NamedPlacesRegex.Match(normalized);
        if (!namedMatch.Success)
            return false;

        var openerName = NormalizeDisplayName(namedMatch.Groups["name"].Value.Trim('.', ':', '-', ' '));
        if (string.IsNullOrWhiteSpace(openerName))
            return false;

        parsed = new ParsedPortalChat(openerName, LocalPronoun: false, normalized);
        return true;
    }

    private static bool ContainsWord(string text, string word)
        => Regex.IsMatch(text, $@"(^|[^A-Za-z]){Regex.Escape(word)}([^A-Za-z]|$)", RegexOptions.IgnoreCase);

    private static bool NamesMatch(string left, string right)
        => string.Equals(NormalizeDisplayName(left), NormalizeDisplayName(right), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeDisplayName(string value)
        => WhitespaceRegex.Replace(value.Trim(), " ");

    private static string FormatName(string value)
        => string.IsNullOrWhiteSpace(value) ? "Unknown" : value;

    private static string FormatSlot(int? slot)
        => slot.HasValue ? slot.Value.ToString(CultureInfo.InvariantCulture) : "Unknown";

    private static string FormatId(ulong? value)
        => value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "Unknown";

    private readonly record struct ParsedPortalChat(string OpenerName, bool LocalPronoun, string MatchedText);

    private readonly record struct LocalPlayerIdentity(string DisplayName, ulong? GameObjectId, ulong? EntityId, ulong? ContentId);

    private readonly record struct PartyMemberIdentity(
        int Slot,
        string DisplayName,
        ulong? ContentId,
        ulong? GameObjectId,
        ulong? EntityId,
        bool IsLocal);

    private sealed record PendingPortalOpener(
        string OpenerName,
        string LocalName,
        int? PartySlot,
        ulong? GameObjectId,
        ulong? EntityId,
        ulong? ContentId,
        bool IsLocalOpener,
        string ChatText,
        DateTime CapturedUtc)
    {
        public TreasurePortalOpenerSnapshot ToSnapshot()
            => new(
                OpenerName,
                LocalName,
                PartySlot,
                GameObjectId,
                EntityId,
                ContentId,
                IsLocalOpener,
                ChatText,
                CapturedUtc);
    }
}

public sealed record TreasurePortalOpenerSnapshot(
    string OpenerName,
    string LocalName,
    int? PartySlot,
    ulong? GameObjectId,
    ulong? EntityId,
    ulong? ContentId,
    bool IsLocalOpener,
    string ChatText,
    DateTime CapturedUtc);
