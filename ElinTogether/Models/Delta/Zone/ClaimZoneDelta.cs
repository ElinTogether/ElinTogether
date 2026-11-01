using System.Collections.Generic;
using System.Linq;
using ElinTogether.Helper;
using ElinTogether.Net;
using ElinTogether.Patches;
using MessagePack;

namespace ElinTogether.Models;

/// <summary>
///     A player reads a deed and says yes: the land is claimed by the game that keeps the world, which is the only one
///     that can make a base everyone has (the branch, the hearth stone, the residents to come). Until 2026-10-13 the
///     deed was refused to a guest, "the one inequality that stays". <br />
///     On the host's map the host claims it as the game does. Away, on a field the player holds alone (zone lease,
///     2026-10-30: on a dedicated server the host never leaves its meadow, so a guest's deed could never be used), the
///     host makes the land in the world (its faction, a branch, its team) and answers <see cref="Accepted" />: the
///     player's game then claims its own copy as the game does (the hearth stone on its map, the deed spent) and sends
///     a checkpoint, whose base the host takes (LeasedBase). <br />
///     Read again from this game's state: the land must be free to claim here, and the sender must hold a deed
/// </summary>
[MessagePackObject]
public class ClaimZoneDelta : ElinDelta
{
    [Key(0)]
    public required int ZoneUid { get; init; }

    /// <summary>
    ///     The host's answer when it does not claim
    /// </summary>
    [Key(1)]
    public bool Refused { get; init; }

    /// <summary>
    ///     The host's answer to a player away on the field it holds: the land is made, claim your copy
    /// </summary>
    [Key(2)]
    public bool Accepted { get; init; }

    protected override void OnApply(ElinNetBase net)
    {
        if (net is not ElinNetHost { IsZoneSession: false } host) {
            if (Refused) {
                RemoteBasePaidPatch.Refuse(true);
            } else if (Accepted) {
                RemoteBasePaidPatch.ClaimHeldLand(ZoneUid);
            }

            return;
        }

        // the player's own game could not claim the land made for it: it is undone here
        if (Refused && host.IsAwayPeer(OriginPeer)) {
            UndoAway(OriginPeer, "its game did not claim it");
            return;
        }

        if (Refused || Accepted) {
            return;
        }

        if (host.IsAwayPeer(OriginPeer)) {
            ClaimAway(host);
            return;
        }

        if (NetSession.Instance.Rules.HostManagesBase || !host.ActiveRemoteCharas.TryGetValue(OriginPeer, out var sender) ||
            _zone.uid != ZoneUid || _zone.mainFaction == pc.faction || !_zone.isClaimable ||
            _zone.instance is not null || Teams.LandCapReached(sender) || sender.things.List(t => t.trait is TraitDeed, true).FirstOrDefault() is not { } deed) {
            EmpLog.Information("Land {ZoneUid} not claimed for peer {Peer}", ZoneUid, OriginPeer);
            host.SendDeltaTo(OriginPeer, new ClaimZoneDelta { ZoneUid = ZoneUid, Refused = true });
            return;
        }

        EmpLog.Information("Land {ZoneUid} claimed for player {Uid}", ZoneUid, sender.uid);
        using (Simulate()) {
            deed.ModNum(-1);
            Teams.Claimer = sender;
            try {
                _zone.ClaimZone(false, sender.pos.GetNearestPoint(false, true, false) ?? sender.pos);
            } finally {
                Teams.Claimer = null;
            }
        }

        // what TraitDeed.OnRead does after the claim, for the start zone: the home quest and the main quest move on (in the
        // sender's story). Outside the simulation: the phase changes reach the other games
        if (_zone == game.StartZone) {
            using (TeamOwnStory.AsSender(host, OriginPeer)) {
                if (game.quests.Get<QuestHome>() != null) {
                    game.quests.Home.ChangePhase(1);
                }

                if (QuestMain.Phase < 200) {
                    game.quests.Main.ChangePhase(200);
                }
            }
        }
    }

    /// <summary>
    ///     A player away on a field it holds: the land is made here without its map (the player's game holds the map,
    ///     it puts the hearth stone there and its checkpoint brings both the map and the base)
    /// </summary>
    private void ClaimAway(ElinNetHost host)
    {
        var zone = game.spatials.Find(ZoneUid) as Zone;
        var sender = host.PlayerChara(OriginPeer);
        var deed = sender?.things.List(t => t.trait is TraitDeed, true).FirstOrDefault();
        if (NetSession.Instance.Rules.HostManagesBase || zone is null || sender is null || deed is null || zone == _zone ||
            !host.LeasedZones(OriginPeer).Contains(zone) || zone.mainFaction == pc.faction || !zone.isClaimable ||
            zone.instance is not null || zone.branch is not null || Teams.LandCapReached(sender) || PendingAway.ContainsKey(OriginPeer)) {
            EmpLog.Information("Land {ZoneUid} not claimed for away peer {Peer} (deed {Deed}, held {Held})", ZoneUid, OriginPeer,
                deed is not null, zone is not null && host.LeasedZones(OriginPeer).Contains(zone));
            host.SendDeltaTo(OriginPeer, new ClaimZoneDelta { ZoneUid = ZoneUid, Refused = true });
            return;
        }

        EmpLog.Information("Land {ZoneUid} claimed for away player {Uid}", ZoneUid, sender.uid);
        // final once that player's own game claimed it too (its checkpoint brings the base, its deed spent)
        PendingAway[OriginPeer] = new(zone.uid, zone.mainFaction, zone.icon, zone.dateExpire, zone.GetInt(2), zone.idPrefix, Deeds(sender),
            UnityEngine.Time.realtimeSinceStartup);
        using (Simulate()) {
            // (the player's own copy spends its deed too, and its checkpoints replace this copy of its character)
            deed.ModNum(-1);
        }

        // what Zone.ClaimZone does to the zone itself (the map's part, the hearth stone, is the player's game's)
        zone.SetMainFaction(pc.faction);
        zone.branch = new FactionBranch();
        zone.branch.OnCreate(zone);
        if (zone.icon == 0) {
            zone.icon = 332;
        }

        zone.dateExpire = 0;
        zone.SetInt(2, world.date.GetRaw());
        zone.idPrefix = 0;
        Teams.Claimer = sender;
        try {
            Teams.OnClaimed(zone);
        } finally {
            Teams.Claimer = null;
        }

        host.SendDeltaTo(OriginPeer, new ClaimZoneDelta { ZoneUid = ZoneUid, Accepted = true });
    }

    // ---- host: a land made for a player away is final only when that player's game claimed it

    private sealed record Pending(int ZoneUid, Faction? Faction, int Icon, int DateExpire, int Claimed, int IdPrefix, int Deeds, float At);

    // peer -> the land made for it and what the zone was before
    private static readonly Dictionary<int, Pending> PendingAway = [];

    // a checkpoint without the base this long after the land was made: the player's game never claimed it
    private const float ConfirmSeconds = 60f;

    private static int Deeds(Chara chara)
    {
        return chara.things.List(t => t.trait is TraitDeed, true).Sum(t => t.Num);
    }

    /// <summary>
    ///     A checkpoint or a release of a player for whom a land was made away: the base came (and the deed was spent
    ///     in its game, the character now replaced from it), the claim is final; no base in its last word (a release),
    ///     or still none long after: undone
    /// </summary>
    internal static void ResolveAway(int peerId, ZoneLeaseRelease release, Chara? chara)
    {
        if (!PendingAway.TryGetValue(peerId, out var pending)) {
            return;
        }

        if (release.ZoneUid == pending.ZoneUid && release.Branch is not null) {
            if (chara is not null && Deeds(chara) >= pending.Deeds) {
                UndoAway(peerId, "its deed was not spent");
                return;
            }

            PendingAway.Remove(peerId);
            EmpLog.Information("Land {ZoneUid} claimed away by peer {Peer}: confirmed by its game", pending.ZoneUid, peerId);
            return;
        }

        if (!release.Checkpoint || UnityEngine.Time.realtimeSinceStartup - pending.At > ConfirmSeconds) {
            UndoAway(peerId, release.Checkpoint ? "no base in its checkpoints" : "handed back without a base");
        }
    }

    /// <summary>
    ///     The land made for that player is not one: the zone as it was, nobody's in the book (the deed comes back with
    ///     the player's next checkpoint, its game never spent it)
    /// </summary>
    internal static void UndoAway(int peerId, string why)
    {
        if (!PendingAway.TryGetValue(peerId, out var pending)) {
            return;
        }

        PendingAway.Remove(peerId);
        if (game.spatials.Find(pending.ZoneUid) is not Zone zone || zone.mainFaction != pc.faction) {
            return;
        }

        zone.branch = null;
        zone.SetMainFaction(pending.Faction);
        zone.icon = pending.Icon;
        zone.dateExpire = pending.DateExpire;
        zone.SetInt(2, pending.Claimed);
        zone.idPrefix = pending.IdPrefix;
        Teams.Unclaim(zone);
        EmpLog.Warning("Land {ZoneUid} made for away peer {Peer} undone: {Why}", pending.ZoneUid, peerId, why);
    }

    [ElinPreLoad]
    private static void Forget(GameIOContext context)
    {
        PendingAway.Clear();
    }
}
