using System.Globalization;
using System.Text.RegularExpressions;
using ADS.Models;
using Dalamud.Game.Gui.Toast;
using Dalamud.Plugin.Services;

namespace ADS.Services;

public sealed class TreasurePortalOpenerTracker
{
    private static readonly TimeSpan PendingOpenerTtl = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan PartySlot2FallbackGrace = TimeSpan.FromSeconds(10);
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex YouHandPortalRegex = new(@"^You\b.*\bhand\b.*\bportal\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex NamedPlacesRegex = new(@"^(?<name>.+?)\s+places?\b.*\bhand\b.*\bportal\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private const string PortalChatSource = "PortalChat";
    private const string PortalChatUnresolvedSource = "PortalChat:Unresolved";
    private const string FallbackPartySlot2Source = "Fallback:PartySlot2";
    private const string RelaySourcePrefix = "Relay:";

    private readonly IObjectTable objectTable;
    private readonly IPartyList partyList;
    private readonly IPlayerState playerState;
    private readonly IChatGui chatGui;
    private readonly IToastGui toastGui;
    private readonly TreasurePortalOpenerRelayService relayService;
    private readonly IPluginLog log;

    private PendingPortalOpener? pendingPortalOpener;
    private string lastPublishedRelayKey = string.Empty;
    private DateTime fallbackEligibleSinceUtc = DateTime.MinValue;
    private DateTime fallbackEligibleAtUtc = DateTime.MinValue;
    private string fallbackDelayLogKey = string.Empty;

    public TreasurePortalOpenerTracker(
        IObjectTable objectTable,
        IPartyList partyList,
        IPlayerState playerState,
        IChatGui chatGui,
        IToastGui toastGui,
        TreasurePortalOpenerRelayService relayService,
        IPluginLog log)
    {
        this.objectTable = objectTable;
        this.partyList = partyList;
        this.playerState = playerState;
        this.chatGui = chatGui;
        this.toastGui = toastGui;
        this.relayService = relayService;
        this.log = log;
    }

    public TreasurePortalOpenerSnapshot? Current
        => pendingPortalOpener?.ToSnapshot();

    public double? CurrentAgeSeconds
        => pendingPortalOpener is { } opener
            ? Math.Max(0, (DateTime.UtcNow - opener.CapturedUtc).TotalSeconds)
            : null;

    public string RelayStatus { get; private set; } = "Relay idle.";

    public DateTime? FallbackEligibleAtUtc
        => fallbackEligibleAtUtc == DateTime.MinValue ? null : fallbackEligibleAtUtc;

    public double? FallbackRemainingSeconds
        => fallbackEligibleAtUtc == DateTime.MinValue
            ? null
            : Math.Max(0, (fallbackEligibleAtUtc - DateTime.UtcNow).TotalSeconds);

    public string FallbackReason { get; private set; } = "Fallback disabled until follower owns an instanced treasure duty.";

    public bool HandleChatMessage(string message)
    {
        if (!TryParsePortalOpenerChat(message, out var parsed, out var ignoreReason))
        {
            LogIgnoredPortalishLine(message, ignoreReason);
            return false;
        }

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
                PortalChatSource,
                parsed.MatchedText,
                DateTime.UtcNow);
        }
        else if (NamesMatch(parsed.OpenerName, local.DisplayName)
                 || MessageStartsWithName(parsed.MatchedText, local.DisplayName))
        {
            opener = new PendingPortalOpener(
                string.IsNullOrWhiteSpace(local.DisplayName) ? parsed.OpenerName : local.DisplayName,
                local.DisplayName,
                FindPartySlotForLocal(local),
                local.GameObjectId,
                local.EntityId,
                local.ContentId,
                IsLocalOpener: true,
                PortalChatSource,
                parsed.MatchedText,
                DateTime.UtcNow);
        }
        else if (TryFindPartyMember(parsed.OpenerName, local, out var partyMember)
                 || TryFindPartyMemberAtMessageStart(parsed.MatchedText, local, out partyMember))
        {
            opener = new PendingPortalOpener(
                partyMember.DisplayName,
                local.DisplayName,
                partyMember.Slot,
                partyMember.GameObjectId,
                partyMember.EntityId,
                partyMember.ContentId,
                IsLocalOpener: false,
                PortalChatSource,
                parsed.MatchedText,
                DateTime.UtcNow);
        }

        if (opener is null)
        {
            opener = new PendingPortalOpener(
                parsed.OpenerName,
                local.DisplayName,
                null,
                null,
                null,
                null,
                IsLocalOpener: false,
                PortalChatUnresolvedSource,
                parsed.MatchedText,
                DateTime.UtcNow);
        }

        var replacedFallback = IsFallback(pendingPortalOpener);
        pendingPortalOpener = opener;
        lastPublishedRelayKey = string.Empty;
        ResetFallbackGate($"real opener captured from {opener.Source}");

        log.Information(
            $"[ADS] Captured treasure portal opener from local chat: source={opener.Source}, opener='{opener.OpenerName}', local='{FormatName(opener.LocalName)}', slot={FormatSlot(opener.PartySlot)}, contentId={FormatId(opener.ContentId)}, objectId={FormatId(opener.GameObjectId)}, entityId={FormatId(opener.EntityId)}, chat='{opener.ChatText}'.");
        if (replacedFallback)
        {
            log.Information(
                $"[ADS] Replaced party-slot-2 fallback with real treasure portal opener: source={opener.Source}, opener='{opener.OpenerName}', slot={FormatSlot(opener.PartySlot)}.");
        }

        return true;
    }

    public void Update(DutyContextSnapshot dutyContext, bool fallbackAllowed)
    {
        var now = DateTime.UtcNow;
        ClearExpiredPendingOpener(now);
        RefreshPendingOpener();

        var relayContext = BuildRelayContext(dutyContext);
        PublishCurrentOpener(relayContext);
        var importedRelay = TryImportRelayOpener(relayContext);
        if (importedRelay)
            RefreshPendingOpener();

        TryCapturePartySlot2Fallback(fallbackAllowed, now);
    }

    public void ClearPendingOpener(string reason)
    {
        if (pendingPortalOpener is null)
            return;

        pendingPortalOpener = null;
        lastPublishedRelayKey = string.Empty;
        ResetFallbackGate($"pending opener cleared after {reason}");
        log.Information($"[ADS] Cleared pending treasure portal opener after {reason}.");
    }

    private void PublishCurrentOpener(TreasurePortalRelayContext relayContext)
    {
        if (pendingPortalOpener is not { } opener
            || IsFallback(opener)
            || IsRelaySource(opener.Source))
        {
            RelayStatus = relayService.Status;
            return;
        }

        var relayKey = BuildRelayPublishKey(opener, relayContext);
        if (string.Equals(relayKey, lastPublishedRelayKey, StringComparison.Ordinal))
        {
            RelayStatus = relayService.Status;
            return;
        }

        if (relayService.TryPublish(opener.ToSnapshot(), relayContext))
            lastPublishedRelayKey = relayKey;

        RelayStatus = relayService.Status;
    }

    private bool TryImportRelayOpener(TreasurePortalRelayContext relayContext)
    {
        if (!relayService.TryReadFresh(relayContext, out var relaySnapshot))
        {
            RelayStatus = relayService.Status;
            return false;
        }

        RelayStatus = relayService.Status;
        if (relaySnapshot.Source.StartsWith("Fallback", StringComparison.OrdinalIgnoreCase))
        {
            RelayStatus = "Relay ignored: fallback snapshots are not imported.";
            return false;
        }

        var local = CaptureLocalPlayer();
        if (!TryResolveRelayOpener(relaySnapshot, local, out var partyMember, out var resolveReason))
        {
            RelayStatus = $"Relay opener unresolved locally: {resolveReason}";
            return false;
        }

        if (!ShouldPromoteRelay(relaySnapshot, partyMember, out var promoteReason))
        {
            RelayStatus = $"Relay not promoted: {promoteReason}";
            return false;
        }

        var replacedFallback = IsFallback(pendingPortalOpener);
        var imported = new PendingPortalOpener(
            partyMember.DisplayName,
            local.DisplayName,
            partyMember.Slot,
            partyMember.GameObjectId,
            partyMember.EntityId,
            partyMember.ContentId ?? relaySnapshot.ContentId,
            partyMember.IsLocal,
            NormalizeRelaySource(relaySnapshot.Source),
            relaySnapshot.ChatText,
            relaySnapshot.CapturedUtc);

        pendingPortalOpener = imported;
        ResetFallbackGate("relay opener imported");
        RelayStatus = $"Imported relay opener '{imported.OpenerName}' at slot {FormatSlot(imported.PartySlot)}.";
        log.Information(
            $"[ADS] Imported treasure portal opener relay: source={imported.Source}, opener='{imported.OpenerName}', slot={FormatSlot(imported.PartySlot)}, contentId={FormatId(imported.ContentId)}, relaySource={relaySnapshot.Source}, relayLocal='{relaySnapshot.PublisherLocalName}'.");
        if (replacedFallback)
        {
            log.Information(
                $"[ADS] Replaced party-slot-2 fallback with relay treasure portal opener: source={imported.Source}, opener='{imported.OpenerName}', slot={FormatSlot(imported.PartySlot)}.");
        }

        return true;
    }

    private bool ShouldPromoteRelay(TreasurePortalRelaySnapshot relaySnapshot, PartyMemberIdentity partyMember, out string reason)
    {
        reason = string.Empty;
        if (pendingPortalOpener is null)
            return true;

        if (IsFallback(pendingPortalOpener))
            return true;

        if (pendingPortalOpener.Source == PortalChatUnresolvedSource)
            return true;

        if (IsRelaySource(pendingPortalOpener.Source))
        {
            if (pendingPortalOpener.PartySlot != partyMember.Slot
                || !NamesMatch(pendingPortalOpener.OpenerName, partyMember.DisplayName)
                || relaySnapshot.CapturedUtc > pendingPortalOpener.CapturedUtc)
            {
                return true;
            }

            reason = "current relay opener is same or newer.";
            return false;
        }

        reason = $"local opener source {pendingPortalOpener.Source} already active.";
        return false;
    }

    private bool TryResolveRelayOpener(
        TreasurePortalRelaySnapshot relaySnapshot,
        LocalPlayerIdentity local,
        out PartyMemberIdentity partyMember,
        out string reason)
    {
        if (relaySnapshot.ContentId is { } contentId
            && TryFindPartyMemberByContentId(contentId, local, out partyMember))
        {
            reason = string.Empty;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(relaySnapshot.OpenerName)
            && (TryFindPartyMember(relaySnapshot.OpenerName, local, out partyMember)
                || TryFindPartyMemberAtMessageStart(relaySnapshot.ChatText, local, out partyMember)))
        {
            reason = string.Empty;
            return true;
        }

        reason = $"name='{relaySnapshot.OpenerName}', contentId={FormatId(relaySnapshot.ContentId)}.";
        partyMember = default;
        return false;
    }

    private void TryCapturePartySlot2Fallback(bool enabled, DateTime now)
    {
        if (!enabled)
        {
            ResetFallbackGate("fallback disabled until follower owns an instanced treasure duty");
            return;
        }

        if (HasResolvedRealOpener())
        {
            ResetFallbackGate("real opener available");
            FallbackReason = "Fallback blocked by real opener.";
            return;
        }

        var fallbackReason = BuildFallbackReason();
        if (IsFallback(pendingPortalOpener))
        {
            RefreshFallbackPartySlot2();
            FallbackReason = "Party-slot-2 fallback already active.";
            return;
        }

        if (fallbackEligibleSinceUtc == DateTime.MinValue)
        {
            fallbackEligibleSinceUtc = now;
            fallbackEligibleAtUtc = now + PartySlot2FallbackGrace;
            FallbackReason = fallbackReason;
        }

        if (now < fallbackEligibleAtUtc)
        {
            var delayKey = $"{fallbackEligibleAtUtc:O}:{fallbackReason}";
            if (!string.Equals(delayKey, fallbackDelayLogKey, StringComparison.Ordinal))
            {
                fallbackDelayLogKey = delayKey;
                log.Information(
                    $"[ADS] Delaying party-slot-2 fallback until {fallbackEligibleAtUtc:O}: reason={fallbackReason}; relayStatus='{RelayStatus}'.");
            }

            return;
        }

        var local = CaptureLocalPlayer();
        if (!TryGetPartyMemberBySlot(2, local, out var partyMember))
        {
            FallbackReason = "Party slot 2 unavailable after fallback grace.";
            return;
        }

        var opener = new PendingPortalOpener(
            partyMember.DisplayName,
            local.DisplayName,
            partyMember.Slot,
            partyMember.GameObjectId,
            partyMember.EntityId,
            partyMember.ContentId,
            partyMember.IsLocal,
            FallbackPartySlot2Source,
            "Party slot 2 fallback",
            now);

        pendingPortalOpener = opener;
        var message = $"ADS did not catch the portal opener. Using party slot 2 {opener.OpenerName} as BMRAI follow target.";
        toastGui.ShowNormal(message);
        chatGui.Print(message);
        log.Information(
            $"[ADS] Captured delayed treasure portal opener fallback: source={opener.Source}, opener='{opener.OpenerName}', local='{FormatName(opener.LocalName)}', slot={FormatSlot(opener.PartySlot)}, contentId={FormatId(opener.ContentId)}, objectId={FormatId(opener.GameObjectId)}, entityId={FormatId(opener.EntityId)}, reason={fallbackReason}, relayStatus='{RelayStatus}'.");
        FallbackReason = fallbackReason;
    }

    private void RefreshFallbackPartySlot2()
    {
        if (pendingPortalOpener is not { Source: FallbackPartySlot2Source } existing)
            return;

        var local = CaptureLocalPlayer();
        if (!TryGetPartyMemberBySlot(2, local, out var partyMember))
            return;

        if (existing.PartySlot != partyMember.Slot
            || !NamesMatch(existing.OpenerName, partyMember.DisplayName))
        {
            return;
        }

        var refreshed = existing with
        {
            LocalName = local.DisplayName,
            GameObjectId = partyMember.GameObjectId ?? existing.GameObjectId,
            EntityId = partyMember.EntityId ?? existing.EntityId,
            ContentId = partyMember.ContentId ?? existing.ContentId,
            IsLocalOpener = partyMember.IsLocal,
        };
        pendingPortalOpener = refreshed;
        if (existing.ContentId is null && refreshed.ContentId is not null)
        {
            log.Information(
                $"[ADS] Resolved fallback treasure portal opener content id: opener='{refreshed.OpenerName}', slot={FormatSlot(refreshed.PartySlot)}, contentId={FormatId(refreshed.ContentId)}.");
        }
    }

    private void RefreshPendingOpener()
    {
        if (pendingPortalOpener is not { } opener || IsFallback(opener))
            return;

        var local = CaptureLocalPlayer();
        PendingPortalOpener? refreshed = null;
        var resolvedSource = IsRelaySource(opener.Source)
            ? opener.Source
            : PortalChatSource;
        if (opener.IsLocalOpener || NamesMatch(opener.OpenerName, local.DisplayName))
        {
            refreshed = opener with
            {
                OpenerName = string.IsNullOrWhiteSpace(local.DisplayName) ? opener.OpenerName : local.DisplayName,
                LocalName = local.DisplayName,
                PartySlot = FindPartySlotForLocal(local) ?? opener.PartySlot,
                GameObjectId = local.GameObjectId ?? opener.GameObjectId,
                EntityId = local.EntityId ?? opener.EntityId,
                ContentId = local.ContentId ?? opener.ContentId,
                IsLocalOpener = true,
                Source = resolvedSource,
            };
        }
        else if (opener.ContentId is { } contentId && TryFindPartyMemberByContentId(contentId, local, out var contentMember))
        {
            refreshed = opener with
            {
                OpenerName = contentMember.DisplayName,
                LocalName = local.DisplayName,
                PartySlot = contentMember.Slot,
                GameObjectId = contentMember.GameObjectId ?? opener.GameObjectId,
                EntityId = contentMember.EntityId ?? opener.EntityId,
                ContentId = contentMember.ContentId ?? opener.ContentId,
                IsLocalOpener = contentMember.IsLocal,
                Source = resolvedSource,
            };
        }
        else if (TryFindPartyMember(opener.OpenerName, local, out var member)
                 || TryFindPartyMemberAtMessageStart(opener.ChatText, local, out member))
        {
            refreshed = opener with
            {
                OpenerName = member.DisplayName,
                LocalName = local.DisplayName,
                PartySlot = member.Slot,
                GameObjectId = member.GameObjectId ?? opener.GameObjectId,
                EntityId = member.EntityId ?? opener.EntityId,
                ContentId = member.ContentId ?? opener.ContentId,
                IsLocalOpener = member.IsLocal,
                Source = resolvedSource,
            };
        }

        if (refreshed is null || refreshed == opener)
            return;

        pendingPortalOpener = refreshed;
        if (opener.Source == PortalChatUnresolvedSource)
        {
            log.Information(
                $"[ADS] Resolved treasure portal opener chat against party list: opener='{refreshed.OpenerName}', slot={FormatSlot(refreshed.PartySlot)}, contentId={FormatId(refreshed.ContentId)}.");
        }
        else if (opener.ContentId is null && refreshed.ContentId is not null)
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
        lastPublishedRelayKey = string.Empty;
        ResetFallbackGate("pending opener expired");
        log.Information(
            $"[ADS] Expired stale treasure portal opener after {age.TotalSeconds:0}s: source={opener.Source}, opener='{opener.OpenerName}', chat='{opener.ChatText}'.");
    }

    private TreasurePortalRelayContext BuildRelayContext(DutyContextSnapshot dutyContext)
    {
        var local = CaptureLocalPlayer();
        var members = EnumeratePartyMembers(local).ToList();
        var tokens = members
            .Select(member => member.ContentId?.ToString(CultureInfo.InvariantCulture) ?? NormalizeDisplayName(member.DisplayName).ToUpperInvariant())
            .Where(static token => !string.IsNullOrWhiteSpace(token))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (tokens.Count == 0)
        {
            var localToken = local.ContentId?.ToString(CultureInfo.InvariantCulture)
                             ?? NormalizeDisplayName(local.DisplayName).ToUpperInvariant();
            if (!string.IsNullOrWhiteSpace(localToken))
                tokens.Add(localToken);
        }

        return new TreasurePortalRelayContext(
            string.Join("|", tokens),
            dutyContext.TerritoryTypeId,
            dutyContext.ContentFinderConditionId,
            dutyContext.InInstancedDuty,
            local.DisplayName,
            local.ContentId);
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

    private bool TryFindPartyMemberByContentId(ulong contentId, LocalPlayerIdentity local, out PartyMemberIdentity partyMember)
    {
        foreach (var member in EnumeratePartyMembers(local))
        {
            if (member.ContentId == contentId)
            {
                partyMember = member;
                return true;
            }
        }

        partyMember = default;
        return false;
    }

    private bool TryFindPartyMemberAtMessageStart(string message, LocalPlayerIdentity local, out PartyMemberIdentity partyMember)
    {
        foreach (var member in EnumeratePartyMembers(local))
        {
            if (MessageStartsWithName(message, member.DisplayName))
            {
                partyMember = member;
                return true;
            }
        }

        partyMember = default;
        return false;
    }

    private bool TryGetPartyMemberBySlot(int slot, LocalPlayerIdentity local, out PartyMemberIdentity partyMember)
    {
        foreach (var member in EnumeratePartyMembers(local))
        {
            if (member.Slot == slot)
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

    private bool TryParsePortalOpenerChat(string message, out ParsedPortalChat parsed, out string ignoreReason)
    {
        parsed = default;
        ignoreReason = string.Empty;
        var normalized = NormalizeDisplayName(message);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            ignoreReason = "empty chat line";
            return false;
        }

        var handMatch = FindWord(normalized, "hand");
        var portalMatch = FindWord(normalized, "portal");
        if (!handMatch.Success || !portalMatch.Success)
        {
            ignoreReason = !handMatch.Success && !portalMatch.Success
                ? "missing hand and portal words"
                : !handMatch.Success
                    ? "missing hand word"
                    : "missing portal word";
            return false;
        }

        if (handMatch.Index > portalMatch.Index)
        {
            ignoreReason = "portal appears before hand";
            return false;
        }

        if (YouHandPortalRegex.IsMatch(normalized))
        {
            parsed = new ParsedPortalChat(string.Empty, LocalPronoun: true, normalized);
            return true;
        }

        var namedMatch = NamedPlacesRegex.Match(normalized);
        if (namedMatch.Success)
        {
            var openerName = NormalizeDisplayName(namedMatch.Groups["name"].Value.Trim('.', ':', '-', ' '));
            if (string.IsNullOrWhiteSpace(openerName))
            {
                ignoreReason = "named opener prefix was empty";
                return false;
            }

            parsed = new ParsedPortalChat(openerName, LocalPronoun: false, normalized);
            return true;
        }

        var openerPrefix = NormalizeDisplayName(normalized[..handMatch.Index].Trim('.', ':', '-', ' '));
        if (string.IsNullOrWhiteSpace(openerPrefix))
        {
            ignoreReason = "no opener prefix before hand";
            return false;
        }

        parsed = new ParsedPortalChat(openerPrefix, LocalPronoun: false, normalized);
        return true;
    }

    private string BuildFallbackReason()
    {
        if (pendingPortalOpener is null)
            return $"No local or relay portal opener captured; relayStatus='{RelayStatus}'.";

        if (pendingPortalOpener.Source == PortalChatUnresolvedSource)
            return $"Local portal chat opener unresolved and relay lookup failed; relayStatus='{RelayStatus}'.";

        if (IsRelaySource(pendingPortalOpener.Source))
            return $"Relay opener did not resolve to a valid follow slot; relayStatus='{RelayStatus}'.";

        return $"No usable real portal opener available from source {pendingPortalOpener.Source}; relayStatus='{RelayStatus}'.";
    }

    private bool HasResolvedRealOpener()
    {
        if (pendingPortalOpener is not { } opener || IsFallback(opener))
            return false;

        if (opener.IsLocalOpener)
            return true;

        return opener.PartySlot is >= 1 and <= 8;
    }

    private void ResetFallbackGate(string reason)
    {
        fallbackEligibleSinceUtc = DateTime.MinValue;
        fallbackEligibleAtUtc = DateTime.MinValue;
        fallbackDelayLogKey = string.Empty;
        FallbackReason = reason;
    }

    private static string BuildRelayPublishKey(PendingPortalOpener opener, TreasurePortalRelayContext context)
        => string.Join(
            ":",
            opener.Source,
            opener.OpenerName,
            opener.PartySlot?.ToString(CultureInfo.InvariantCulture) ?? "none",
            opener.ContentId?.ToString(CultureInfo.InvariantCulture) ?? "none",
            opener.CapturedUtc.Ticks.ToString(CultureInfo.InvariantCulture),
            context.PartyKey,
            context.InInstancedDuty ? "duty" : "outside",
            context.TerritoryTypeId.ToString(CultureInfo.InvariantCulture),
            context.ContentFinderConditionId.ToString(CultureInfo.InvariantCulture));

    private static string NormalizeRelaySource(string source)
        => source.Contains("PortalChat", StringComparison.OrdinalIgnoreCase)
            ? $"{RelaySourcePrefix}{PortalChatSource}"
            : $"{RelaySourcePrefix}{source}";

    private static bool IsFallback(PendingPortalOpener? opener)
        => opener?.Source == FallbackPartySlot2Source;

    private static bool IsRelaySource(string source)
        => source.StartsWith(RelaySourcePrefix, StringComparison.OrdinalIgnoreCase);

    private static bool ContainsWord(string text, string word)
        => Regex.IsMatch(text, $@"(^|[^A-Za-z]){Regex.Escape(word)}([^A-Za-z]|$)", RegexOptions.IgnoreCase);

    private static Match FindWord(string text, string word)
        => Regex.Match(text, $@"(^|[^A-Za-z]){Regex.Escape(word)}([^A-Za-z]|$)", RegexOptions.IgnoreCase);

    private void LogIgnoredPortalishLine(string message, string reason)
    {
        var normalized = NormalizeDisplayName(message);
        if (string.IsNullOrWhiteSpace(normalized)
            || !ContainsWord(normalized, "portal"))
        {
            return;
        }

        log.Debug($"[ADS] Ignored portal-ish chat line while tracking treasure opener: reason={reason}; chat='{normalized}'");
    }

    private static bool NamesMatch(string left, string right)
        => string.Equals(NormalizeDisplayName(left), NormalizeDisplayName(right), StringComparison.OrdinalIgnoreCase);

    private static bool MessageStartsWithName(string message, string displayName)
    {
        var normalizedMessage = NormalizeDisplayName(message);
        var normalizedName = NormalizeDisplayName(displayName);
        if (string.IsNullOrWhiteSpace(normalizedMessage)
            || string.IsNullOrWhiteSpace(normalizedName)
            || !normalizedMessage.StartsWith(normalizedName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return normalizedMessage.Length == normalizedName.Length
               || !char.IsLetterOrDigit(normalizedMessage[normalizedName.Length]);
    }

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
        string Source,
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
                Source,
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
    string Source,
    string ChatText,
    DateTime CapturedUtc);
