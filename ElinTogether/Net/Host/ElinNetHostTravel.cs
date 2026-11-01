using ElinTogether.Patches;
using System;
using System.Collections.Generic;
using System.Linq;
using ElinTogether.Common;
using ElinTogether.Helper;
using ElinTogether.Helper.Extensions;
using ElinTogether.LangMod;
using ElinTogether.Models;
using ElinTogether.Net.Steam;
using HeathenEngineering.SteamworksIntegration;

namespace ElinTogether.Net;

/// <summary>
///     Independent travel: a client leases a zone the host is not in, simulates it on its own,
///     and hands it back when leaving
/// </summary>
internal partial class ElinNetHost
{
    /// <summary>
    ///     Headroom between the host uid counter and a client range, the host keeps allocating below it
    ///     during the lease. The counter only jumps past a range the client actually used
    /// </summary>
    private const int LeaseUidHeadroom = 50_000;

    // what a player alone on a map may allocate at most: a counter it reported beyond (int.MaxValue was tried, security
    // audit 2026-10-27) would have made the host's uids wrap and collide in the save
    private const int MaxLeaseUids = 5_000_000;

    /// <summary>
    ///     Same for quest uids: a zone simulated by a client creates the quests of its residents there
    /// </summary>
    private const int LeaseQuestUidHeadroom = 10_000;

    private const int FloorCards = 0;
    private const int FloorQuests = 1;

    // Spatial.lv, see the game
    private const int LvIndex = 7;

    private int _questRangeNext;

    /// <summary>
    ///     The highest ranges handed out, cards and quests, kept in the save: a player travelling alone keeps what
    ///     it created (its quests, the maps and bags of its last checkpoint) when the host quits, and the numbers
    ///     in there must not be handed out again by the next session
    /// </summary>
    [ElinGameIOProperty("lease_range_floor")]
    private static int[] LeaseRangeFloor
    {
        get => field is { Length: 2 } ? field : field = new int[2];
        set;
    }

    private int ReserveQuestUids()
    {
        _questRangeNext = Math.Max(Math.Max(game.quests.uid, _questRangeNext), LeaseRangeFloor[FloorQuests]) +
                          LeaseQuestUidHeadroom;
        LeaseRangeFloor[FloorQuests] = _questRangeNext;
        return _questRangeNext;
    }

    /// <summary>
    ///     Peer id -> leased zone uid -> first card uid reserved for it <br />
    ///     Two zones while moving between them: the next one is granted before the previous one is handed back
    /// </summary>
    private readonly Dictionary<int, Dictionary<int, int>> _leases = [];

    /// <summary>
    ///     Peers that left the host map, see <see cref="ZoneLeaseAck" />
    /// </summary>
    private readonly HashSet<int> _departed = [];

    // peer id -> when it left the host map, see IsOffHostMap
    private readonly Dictionary<int, float> _departedAt = [];

    /// <summary>
    ///     Guest peer id -> zone it joins and the peer holding it, before and after the holder expects it
    /// </summary>
    private readonly Dictionary<int, (int ZoneUid, int HolderId)> _pendingGuests = [];

    private readonly Dictionary<int, (int ZoneUid, int HolderId)> _guests = [];

    // Steam id of a visitor -> the player whose map it last visited: a holder uploads its visitors' characters, never
    // anyone else's (security audit 2026-10-27: an offline player's character was replaced by any holder)
    private readonly Dictionary<ulong, int> _visitedHolder = [];

    /// <summary>
    ///     Peers standing on the host map: they finished replicating it. Not those still loading it
    ///     (joining, coming back): these follow the host if it moves on meanwhile, see LeavePlayersBehind
    /// </summary>
    private readonly HashSet<int> _settled = [];

    /// <summary>
    ///     Peer id -> card uid counter of its last checkpoint
    /// </summary>
    private readonly Dictionary<int, int> _checkpointUidNext = [];

    /// <summary>
    ///     Host move into a leased zone, performed once the client hands it back
    /// </summary>
    private (Zone Zone, ZoneTransition Transition)? _pendingHostMove;

    /// <summary>
    ///     The player finished replicating the host map and stands on it
    /// </summary>
    private void MarkSettled(ISteamNetPeer peer)
    {
        _settled.Add(peer.Id);
    }

    /// <summary>
    ///     Its game holds the copy of this map it was sent: what happens to its character from now on reaches it
    /// </summary>
    internal bool IsSettled(int peerId)
    {
        return _settled.Contains(peerId);
    }

    internal bool IsAwayPeer(int peerId)
    {
        return _departed.Contains(peerId);
    }

    /// <summary>
    ///     The maps this player's game simulates on its own (its leases): none while it plays on the host's
    /// </summary>
    internal IEnumerable<Zone?> LeasedZones(int peerId)
    {
        return _leases.TryGetValue(peerId, out var zones) ? zones.Keys.Select(uid => game.spatials.Find(uid) as Zone) : [];
    }

    /// <summary>
    ///     To every player that is not away (on a map), except one
    /// </summary>
    internal void SendDeltaToMapPeers(int exceptPeer, ElinDelta delta)
    {
        foreach (var peer in Socket.Peers) {
            if (peer.Id != exceptPeer && !IsAway(peer)) {
                peer.Send(new WorldStateDeltaList {
                    DeltaList = [delta],
                });
            }
        }
    }

    internal bool IsAway(ISteamNetPeer peer)
    {
        // zone session: a leaving guest already handed its character to its host link
        return _departed.Contains(peer.Id) || _leavingGuests.Contains(peer.Id);
    }

    /// <summary>
    ///     The host player moves into a zone <br />
    ///     Returns true to let the move happen now, otherwise the zone is recalled from the client holding it
    /// </summary>
    internal bool TryEnterZone(Zone zone, ZoneTransition transition)
    {
        // not a move, the game does nothing of it
        if (zone == _zone) {
            return true;
        }

        // out of the zone of a quest the game goes back to where it came from whatever was asked (Chara.MoveZone):
        // that town may be held by the player who stayed there
        if (_zone?.instance is { } leaving) {
            zone = game.spatials.Find(leaving.uidZone) ?? zone;
        }

        // a land closed to the host's player: it stays where it is, told so (never a refusal to leave)
        if (Teams.EntryDenied(pc, zone) is { } closed) {
            Msg.SayRaw(closed.lang());
            return false;
        }

        if (!CanEnterNow(zone, transition)) {
            return false;
        }

        LeavePlayersBehind(zone, LeaveAccompaniedZone());
        return true;
    }

    /// <summary>
    ///     The host leaves its map: with independent travel the players on it stay where they are instead of
    ///     being dragged along. The first one simulates the map from now on, as it stands on its screen when
    ///     that is what the host has too, from the host's copy otherwise,
    ///     the others join its zone session (on the world map everyone has its own copy) <br />
    ///     To follow the host, a player takes the same way out
    /// </summary>
    /// <param name="keeper">the player whose quest zone this is, when the host who came along goes back alone</param>
    private void LeavePlayersBehind(Zone destination, int keeper = 0)
    {
        if (IsZoneSession || !Session.Rules.AllowIndependentTravel || _zone is not { } zone || destination == zone) {
            return;
        }

        // nobody stays in the zone of a quest without the one who took it: whoever came along leaves with
        // the host and arrives where it arrives, as its party does. The quest is settled by the host's own move
        if (zone.instance is ZoneInstanceRandomQuest && keeper == 0) {
            _settled.Clear();
            return;
        }

        // only those standing here: a player still loading this map (it was called back for this very move,
        // or just joined) comes along as before
        // the one whose quest it is keeps its zone, before anyone else
        // not one that was just granted another map: it is leaving too, its own way (its answer comes after this
        // move, see OnZoneLeaseAck). Handed this map, it stayed behind on it instead of going where it went
        // (simultaneous moves, 2026-10-14)
        var staying = Socket.Peers.Where(p => ActiveRemoteCharas.ContainsKey(p.Id) && _settled.Contains(p.Id) &&
                                              !(_leases.TryGetValue(p.Id, out var held) && held.Keys.Any(uid => uid != zone.uid)))
            .OrderByDescending(p => p.Id == keeper).ToList();
        _settled.Clear();
        if (staying.Count == 0) {
            return;
        }

        // a player the first one may not host comes along with the host instead, as one still loading this map
        // (security audit 2026-10-27, item 8)
        if (!zone.IsRegion) {
            var first = staying[0];
            staying = staying.Where(p => p == first || MayHost(first, p)).ToList();
        }

        // their last actions here are applied and answered, their characters and companions leave the host map
        // (the party of the host must not drag them along)
        foreach (var peer in staying) {
            DepartFromHostMap(peer);
        }

        ISteamNetPeer? heir = null;
        foreach (var peer in staying) {
            if (zone.IsRegion || heir is null) {
                heir ??= peer;

                var rangeStart = ReserveLease(peer, zone);

                // our copy is the reference, not what stands on its screen: it goes along with its numbers, and
                // that player only loads it when its own numbers differ, see ElinNetClient.AdoptHostCopy.
                // Not the world map (everyone has its own), not the zone of a quest (its taker runs it as it is)
                // (with the host's "repair the map by itself" box: unticked, the map stays as it stands on its screen)
                var map = !Session.Rules.AutoResync || zone.IsRegion || zone.IsInstance || zone.map is null
                    ? null
                    : TryCollectMap(zone);
                var sums = map is null ? null : ZoneLeaseState.Sums(zone.map);

                EmpLog.Information("Host leaves {ZoneFullName}, {@Peer} keeps it, uid range from {UidRangeStart}, host copy sent along {HasMap}: {Sums}",
                    zone.ZoneFullName, peer, rangeStart, map is not null, ZoneLeaseState.TellSums(sums ?? []));

                peer.Send(new ZoneLeaseGrant {
                    ZoneUid = zone.uid,
                    RequestedUid = zone.uid,
                    UidRangeStart = rangeStart,
                    QuestUidRangeStart = ReserveQuestUids(),
                    ZoneState = ZoneLeaseState.GetState(zone),
                    IdCurrentSubset = zone.idCurrentSubset,
                    Branch = LeasedBase.Pack(zone),
                    Name = LeasedBase.Name(zone),
                    BaseElements = LeasedBase.Elements(zone),
                    Handoff = true,
                    Map = map,
                    MapSums = sums,
                });
                continue;
            }

            // stays too, as a guest of the one keeping the map, see ElinNetClient.StayAsGuest
            _pendingGuests[peer.Id] = (zone.uid, heir.Id);
            peer.Send(new ZoneLeaseGrant {
                ZoneUid = zone.uid,
                RequestedUid = zone.uid,
                UidRangeStart = 0,
                ZoneState = ZoneLeaseState.GetState(zone),
                IdCurrentSubset = zone.idCurrentSubset,
                Guest = true,
                Handoff = true,
            });
        }
    }

    /// <summary>
    ///     Our copy for the player keeping the map we leave. None when it cannot be read: that player keeps the one
    ///     on its screen, as with the host's "repair the map by itself" box unticked. A throw here held the lease
    ///     with no grant sent, and stopped the host's own move (reliability audit 2026-10-27, #4)
    /// </summary>
    private static Dictionary<string, LZ4Bytes>? TryCollectMap(Zone zone)
    {
        try {
            return ZoneLeaseState.CollectMap(zone);
        } catch (Exception ex) {
            EmpLog.Warning(ex, "Map of zone {ZoneFullName} could not be read, the player keeping it keeps its own copy", zone.ZoneFullName);
            return null;
        }
    }

    /// <summary>
    ///     The host stands in the zone of a quest it took: the players it left in the town the quest comes from
    ///     are asked along, see ElinNetClient.OnQuestFollowInvite. The quest and its reward stay the host's
    /// </summary>
    private void InviteToQuestZone(Zone zone)
    {
        if (IsZoneSession || !PersonalQuests.InstancesEnabled || zone != _zone ||
            zone.instance is not ZoneInstanceRandomQuest { uidQuest: not 0 } instance) {
            return;
        }

        // the quest of the player the host came along with is that player's, and so is the reward
        var taker = _accompanied is { } along && along.ZoneUid == zone.uid &&
                    ActiveRemoteCharas.TryGetValue(along.PeerId, out var chara)
            ? chara
            : pc;

        if (taker != pc) {
            // the monsters are in by now (ZoneEvent.OnVisit ran when the host walked in)
            SendQuestZoneState(taker, zone);
        }

        foreach (var peer in Socket.Peers) {
            // the one keeping that town since the host left it, or visiting the one who does: a player elsewhere
            // has its own business
            var keeps = _leases.TryGetValue(peer.Id, out var zones) && zones.ContainsKey(instance.uidZone);
            var visits = _guests.TryGetValue(peer.Id, out var at) && at.ZoneUid == instance.uidZone;
            if (!_departed.Contains(peer.Id) || !(keeps || visits)) {
                continue;
            }

            EmpLog.Information("Asking player {@Peer} along to quest zone {ZoneFullName}", peer, zone.ZoneFullName);

            SendDeltaTo(peer.Id, new QuestFollowDelta {
                Kind = QuestFollowDelta.Invite,
                Name = taker.Name,
                ZoneUid = zone.uid,
            });
        }
    }

    /// <summary>
    ///     How long the question "come along?" stays open on the host's screen, no answer is a no
    /// </summary>
    private const float QuestAskSeconds = 15f;

    /// <summary>
    ///     The question open on the host's screen, and the request of the player it holds back meanwhile
    /// </summary>
    private (ZoneLeaseRequest Request, ISteamNetPeer Peer, Dialog Box, float Deadline)? _questAsk;

    /// <summary>
    ///     The player whose quest zone the host runs since it came along, until the host leaves that zone.
    ///     In memory only: nothing of it is in the save, see QuestZoneSavePatch
    /// </summary>
    private (int PeerId, int CharaUid, int QuestUid, int ZoneUid, bool Leaving)? _accompanied;

    private byte[]? _accompaniedData;
    private Quest? _accompaniedQuest;

    /// <summary>
    ///     The quest the host was last asked along to: asked once, a request coming again for it is not
    /// </summary>
    private int _lastQuestAsked;

    /// <summary>
    ///     The session ends (the host stops it, or loads another game): the zone it came along to is a plain one
    ///     from here, nothing of another player's quest stays in the host's game or in its save
    /// </summary>
    internal override void Stop()
    {
        ForgetQuestCompanion(null);
        base.Stop();
    }

    protected override void Update()
    {
        base.Update();
        UpdateJoinCopies();
        UpdateRestartWarnings();
        UpdateQuestAsk();
        UpdateSoftRejoins();
        UpdateWorldMapTogether();
        UpdateLandRules();
    }

    /// <summary>
    ///     The character of the player whose quest zone the host came along to, null when it is not that quest's
    /// </summary>
    internal Chara? AccompaniedTaker(int questUid)
    {
        return _accompanied is { } along && along.QuestUid == questUid ? ActiveRemoteCharas.GetValueOrDefault(along.PeerId) : null;
    }

    /// <summary>
    ///     The quest of the player the host came along with, as that player last told it (it counts what is
    ///     delivered, the host only reads). The same object until it tells something new
    /// </summary>
    internal Quest? AccompaniedQuest(int questUid)
    {
        if (_accompanied is not { } along || along.QuestUid != questUid) {
            return null;
        }

        if (PersonalQuestLogs.TryGetValue(along.CharaUid, out var log) && log.TryGetValue(questUid, out var data) &&
            !ReferenceEquals(data, _accompaniedData)) {
            _accompaniedData = data;
            _accompaniedQuest = new LZ4Bytes { Bytes = data }.Decompress<Quest>();
        }

        return _accompaniedQuest?.uid == questUid ? _accompaniedQuest : null;
    }

    /// <summary>
    ///     A player standing on the host's map leaves for the zone of a quest it took: the host is asked along
    ///     (the same question a player gets when the host leaves, see ElinNetClient.OnQuestFollowInvite). <br />
    ///     Yes: the host makes that zone itself and runs it, the player follows it there as anywhere else. The
    ///     quest and its reward stay the player's. No, or no answer: as before, the player holds the zone alone
    /// </summary>
    /// <returns>true when the request waits for the host's answer</returns>
    private bool AskAlongToQuestZone(ZoneLeaseRequest request, ISteamNetPeer peer)
    {
        if (IsZoneSession || !PersonalQuests.InstancesEnabled || GetPeerDenyReason(peer) is not null ||
            request.Blueprint is not { Instance: true, QuestUid: not 0 } blueprint) {
            return false;
        }

        // only with nothing else going on here: no question open, no quest zone of its own or of someone else,
        // no trade, and not asked for that quest already
        if (_questAsk is not null || _accompanied is not null || _pendingHostMove is not null ||
            _lastQuestAsked == blueprint.QuestUid ||
            !CanRunQuestZoneFor(peer, blueprint, out var taker) ||
            game.quests.list.Any(q => q.UseInstanceZone && PersonalQuests.IsPersonal(q)) ||
            PlayerTrade.View is { Phase: PlayerTrade.Invited or PlayerTrade.Open }) {
            EmpLog.Debug("Not asking the host along to the quest zone of {@Peer}", peer);
            return false;
        }

        EmpLog.Information("Asking the host along to the quest zone of {@Peer}", peer);
        _lastQuestAsked = blueprint.QuestUid;

        var box = Dialog.YesNo("emp_quest_follow_ask".Loc(taker.Name),
            () => AnswerQuestAsk(true),
            () => AnswerQuestAsk(false));
        _questAsk = (request, peer, box, UnityEngine.Time.realtimeSinceStartup + QuestAskSeconds);

        SendDeltaTo(peer.Id, new QuestFollowDelta {
            Kind = QuestFollowDelta.Asked,
            Name = pc.Name,
        });
        return true;
    }

    /// <summary>
    ///     The host and that player stand on the same map, and the host knows the quest and who gave it. <br />
    ///     Hunts, harvests and concerts. What is delivered or played is counted by the game that has the quest in
    ///     its log (QuestManager.Get&lt;QuestHarvest&gt;, Get&lt;QuestMusic&gt;), the taker's: a delivery and a
    ///     tune are replayed in every game (InvOwnerOnProcessDelta, CharaTaskDelta), so the host's count there
    ///     too, and the host reads the total the taker tells it (AccompaniedQuest). <br />
    ///     Not a defense: its horn acts on the copy of the zone of whoever blows it, its waves are only counted
    ///     where the zone runs, and the reward is paid by the wave reached in the taker's game. It stays the
    ///     taker's alone, as before
    /// </summary>
    private bool CanRunQuestZoneFor(ISteamNetPeer peer, LeaseZoneBlueprint blueprint, out Chara taker)
    {
        taker = null!;
        if (_zone is not { IsRegion: false, IsInstance: false } || pc.isDead || !_settled.Contains(peer.Id) ||
            !ActiveRemoteCharas.TryGetValue(peer.Id, out var chara) || _map.FindChara(blueprint.GiverUid) is null ||
            !PersonalQuestLogs.TryGetValue(chara.uid, out var log) || !log.TryGetValue(blueprint.QuestUid, out var data)) {
            return false;
        }

        taker = chara;
        return new LZ4Bytes { Bytes = data }.Decompress<Quest>() is QuestSubdue or QuestHarvest or QuestMusic;
    }

    private void UpdateQuestAsk()
    {
        if (_questAsk is not { } ask) {
            return;
        }

        // closed without a click
        if (ask.Box == null) {
            AnswerQuestAsk(false);
            return;
        }

        if (UnityEngine.Time.realtimeSinceStartup < ask.Deadline) {
            return;
        }

        _questAsk = null;
        ask.Box.Close();
        ResolveQuestAsk(ask.Request, ask.Peer, false, QuestFollowDelta.NoAnswer);
    }

    private void AnswerQuestAsk(bool along)
    {
        if (_questAsk is not { } ask) {
            return;
        }

        _questAsk = null;
        ResolveQuestAsk(ask.Request, ask.Peer, along, QuestFollowDelta.Declined);
    }

    private void ResolveQuestAsk(ZoneLeaseRequest request, ISteamNetPeer peer, bool along, int refusal)
    {
        if (along && TakeQuestZone(request, peer)) {
            return;
        }

        if (!along) {
            SendDeltaTo(peer.Id, new QuestFollowDelta {
                Kind = refusal,
                Name = pc.Name,
            });
        }

        // as before: that player holds the zone of its quest and runs it alone
        LeaseZone(request, peer);
    }

    /// <summary>
    ///     The host said yes: it makes the zone of that player's quest as the game does for its own, and enters
    ///     it. The player's request is void, it comes along like a player still loading this map
    /// </summary>
    private bool TakeQuestZone(ZoneLeaseRequest request, ISteamNetPeer peer)
    {
        // what was true when asking may not be anymore
        if (request.Blueprint is not { } blueprint || _accompanied is not null ||
            !CanRunQuestZoneFor(peer, blueprint, out var taker) || _map.FindChara(blueprint.GiverUid) is not { } giver) {
            return false;
        }

        // from here the zone reads that quest, see AccompaniedQuest
        _accompanied = (peer.Id, taker.uid, blueprint.QuestUid, 0, false);
        _accompaniedData = null;
        _accompaniedQuest = null;

        Zone? zone = null;
        try {
            // announced to everyone like a zone of the host's own quest (SpatialGenDelta): a client only loads
            // the map of a zone it knows
            zone = AccompaniedQuest(blueprint.QuestUid)?.CreateInstanceZone(giver);
        } catch (Exception ex) {
            EmpLog.Warning(ex, "Quest zone of player {CharaUid} could not be made on the host", taker.uid);
        }

        if (zone is null) {
            _accompanied = null;
            return false;
        }

        _accompanied = (peer.Id, taker.uid, blueprint.QuestUid, zone.uid, false);

        // back in town, everyone stands where the taker left from, as it would alone
        if (zone.instance is { } instance) {
            instance.x = taker.pos.x;
            instance.z = taker.pos.z;
        }

        // not left behind in town with the others, see LeavePlayersBehind
        _settled.Remove(peer.Id);
        pc.MoveZone(zone, ZoneTransition.EnterState.Center);

        if (pc.currentZone != zone) {
            // the move did not happen: nothing of it stays, that player goes alone as before
            EmpLog.Warning("Host could not enter quest zone {ZoneFullName}, leaving it to {@Peer}", zone.ZoneFullName, peer);
            _accompanied = null;
            _settled.Add(peer.Id);
            zone.Destroy();
            return false;
        }

        EmpLog.Information("Host comes along with {@Peer} to quest zone {ZoneFullName} {ZoneUid}",
            peer, zone.ZoneFullName, zone.uid);

        // the zone it made for itself becomes this one, by its number, see ElinNetClient.OnZoneLeaseDenied.
        // Sent before the announcement and the map of the zone reach it (frames later, see SpatialGenEvent and
        // ZoneActivateEvent)
        peer.Send(new ZoneLeaseDenied {
            ZoneUid = zone.uid,
            Reason = QuestFollowDelta.Coming,
        });
        return true;
    }

    /// <summary>
    ///     Net event: the player whose quest it is walks out of the zone the host runs for it. Everyone leaves,
    ///     by the host's own move
    /// </summary>
    internal void OnQuestTakerLeaves(int peerId)
    {
        if (_accompanied is not { } along || along.Leaving || along.PeerId != peerId ||
            _zone is not { instance: { } instance } zone || zone.uid != along.ZoneUid) {
            return;
        }

        EmpLog.Information("The taker of the quest leaves zone {ZoneFullName}, everyone does", zone.ZoneFullName);

        // kept: the town may have to be recalled first, see CanEnterNow
        _accompanied = (along.PeerId, along.CharaUid, along.QuestUid, along.ZoneUid, true);

        // not while that message is applied: the move, and what it takes from the bags, is the host's own doing
        CoroutineHelper.Deferred(() => {
            if (_accompanied is { } still && still.Leaving && _zone == zone) {
                pc.MoveZone(game.spatials.Find(instance.uidZone) ?? pc.homeZone);
            }
        });
    }

    /// <summary>
    ///     The host leaves the zone of a quest it came along to. <br />
    ///     Alone before the end (the quest still running, its taker standing there): the quest goes on, its taker
    ///     runs the zone from here and settles it when it leaves, as if it had come alone. <br />
    ///     Otherwise everyone leaves (the taker walked out, the quest is decided, or the taker is still loading
    ///     the zone and cannot keep it): the taker hears how it went and settles its quest in town, where the
    ///     host gives the reward once (see PersonalQuests.OnHostSettled, CompletePersonal). What leaving decides
    ///     (a harvest weighed, the crops not delivered taken back from every bag) is decided here, once
    /// </summary>
    /// <returns>the player keeping the zone, 0 when everyone leaves</returns>
    private int LeaveAccompaniedZone()
    {
        if (_accompanied is not { } along || _zone is not { instance: ZoneInstanceRandomQuest instance } zone ||
            zone.uid != along.ZoneUid) {
            return 0;
        }

        var connected = ActiveRemoteCharas.ContainsKey(along.PeerId);
        var alone = connected && _settled.Contains(along.PeerId) && !along.Leaving &&
                    instance.status == ZoneInstance.Status.Running;

        if (alone) {
            // its taker runs it from now on, from where the hunt stands, and it is gone when that player hands
            // it back
            if (ActiveRemoteCharas.TryGetValue(along.PeerId, out var taker)) {
                SendQuestZoneState(taker, zone);
            }

            // the bag of the one walking out of a harvest before the end is searched, the host's like a
            // player's (see DepartFromHostMap)
            if (zone.events.GetEvent<ZoneEventHarvest>() is not null) {
                TakeBackOwnCrops();
            }

            _questZones.Add(zone.uid);
        } else if (connected) {
            if (ActiveRemoteCharas.TryGetValue(along.PeerId, out var taker)) {
                try {
                    // a harvest is weighed and the crops not delivered are taken back from every bag of the
                    // party, as the game does for the one who took the quest: it is told, and it pays for it
                    using var told = MsgRelayContext.RedirectTo(taker);
                    using var standIn = PlayerStandIn.For(this, along.PeerId, taker);
                    foreach (var zoneEvent in zone.events.list.OfType<ZoneEventQuest>().ToList()) {
                        zoneEvent.OnLeaveZone();
                    }
                } catch (Exception ex) {
                    EmpLog.Warning(ex, "Leaving quest zone {ZoneFullName} failed for player {CharaUid}",
                        zone.ZoneFullName, along.CharaUid);
                }
            }

            // also to a taker still loading the zone: it comes along, and its quest must not stay open for ever
            SendDeltaTo(along.PeerId, new QuestFollowDelta {
                Kind = instance.status == ZoneInstance.Status.Success ? QuestFollowDelta.Won : QuestFollowDelta.Lost,
                Name = pc.Name,
                ZoneUid = zone.uid,
                QuestUid = along.QuestUid,
                Giver = instance.uidClient,
            });
        }

        EmpLog.Information("Host leaves quest zone {ZoneFullName} of player {CharaUid}, alone {Alone}, status {Status}",
            zone.ZoneFullName, along.CharaUid, alone, instance.status);

        // nothing of that quest is left for the game to run or to settle when the host moves
        zone.events.list.RemoveAll(e => e is ZoneEventQuest);
        instance.uidQuest = 0;
        _accompanied = null;

        return alone ? along.PeerId : 0;
    }

    private static void TakeBackOwnCrops()
    {
        var taken = new List<Thing>();
        foreach (var member in CompanionHelper.CompanionsOf(pc).Prepend(pc)) {
            member.things.Foreach(t => {
                if (t.GetBool(115) && EClass.rnd(2) != 0) {
                    taken.Add(t);
                }
            });
        }

        if (taken.Count == 0) {
            return;
        }

        Msg.Say("harvest_confiscate", taken.Count.ToString());
        foreach (var thing in taken) {
            thing.Destroy();
        }

        EClass.player.ModKarma(-1);
    }

    /// <summary>
    ///     The same to every player standing on this quest zone with the host: a defense's horns are offered from what
    ///     the event holds (its wave, its rounds), which only ticks here (hunt 6 item 1)
    /// </summary>
    internal void SendQuestZoneStateHere()
    {
        if (IsZoneSession || _zone is not { IsInstance: true } zone) {
            return;
        }

        foreach (var chara in ActiveRemoteCharas.Values.Where(c => c.currentZone == zone).ToList()) {
            SendQuestZoneState(chara, zone);
        }
    }

    /// <summary>
    ///     What the quest events of the zone hold here, for the copy of the zone its taker made itself, see
    ///     ElinNetClient.OnQuestZoneState
    /// </summary>
    private void SendQuestZoneState(Chara taker, Zone zone)
    {
        var peerId = ActiveRemoteCharas.FirstOrDefault(pair => pair.Value == taker).Key;
        SendDeltaTo(peerId, new QuestFollowDelta {
            Kind = QuestFollowDelta.ZoneState,
            Name = pc.Name,
            ZoneUid = zone.uid,
            Events = LZ4Bytes.Create(zone.events.list.Where(e => e is ZoneEventQuest).ToList()),
        });
    }

    /// <summary>
    ///     The game fails the quest when the local player dies in its zone: here that is the taker, not the host
    /// </summary>
    internal void OnDeathInQuestZone(Chara dead, ZoneInstance.Status before)
    {
        if (_accompanied is not { } along || _zone is not { instance: ZoneInstanceRandomQuest instance } zone ||
            zone.uid != along.ZoneUid) {
            return;
        }

        if (dead.IsPC) {
            instance.status = before;
        } else if (ActiveRemoteCharas.TryGetValue(along.PeerId, out var taker) && dead == taker) {
            instance.status = ZoneInstance.Status.Fail;
        }
    }

    /// <summary>
    ///     While the host's game is saved: the zone it came along to holds no quest. Returns what puts it back
    /// </summary>
    internal Action? HideAccompaniedQuest()
    {
        if (_accompanied is not { } along ||
            game.spatials.Find(along.ZoneUid) is not { instance: ZoneInstanceRandomQuest instance } zone) {
            return null;
        }

        var events = zone.events.list.Where(e => e is ZoneEventQuest).ToList();
        var questUid = instance.uidQuest;
        zone.events.list.RemoveAll(e => e is ZoneEventQuest);
        instance.uidQuest = 0;

        return () => {
            zone.events.list.AddRange(events);
            instance.uidQuest = questUid;
        };
    }

    /// <summary>
    ///     That player is gone, or the session ends (no peer): the question about it is void, and the zone the
    ///     host runs for it is a plain one (its quest is dropped at no cost when it connects again, see
    ///     PersonalQuests.Restore)
    /// </summary>
    private void ForgetQuestCompanion(ISteamNetPeer? peer)
    {
        if (_questAsk is { } ask && (peer is null || ask.Peer.Id == peer.Id)) {
            _questAsk = null;
            if (ask.Box != null) {
                ask.Box.Close();
            }
        }

        if (_accompanied is not { } along || (peer is not null && along.PeerId != peer.Id)) {
            return;
        }

        _accompanied = null;
        if (core?.game?.spatials?.Find(along.ZoneUid) is { instance: ZoneInstanceRandomQuest instance } zone) {
            zone.events.list.RemoveAll(e => e is ZoneEventQuest);
            instance.uidQuest = 0;
        }
    }

    private bool CanEnterNow(Zone zone, ZoneTransition transition)
    {
        // going somewhere else than the zone being recalled: the host gave up on it, and must not be pulled
        // there when it comes back later
        if (_pendingHostMove is { } waiting && waiting.Zone != zone) {
            ZoneLeaseState.Imported.Remove(waiting.Zone.uid);
            _pendingHostMove = null;
        }

        if (zone.IsRegion) {
            return true;
        }

        var holder = _leases.FirstOrDefault(kv => kv.Value.ContainsKey(zone.uid));
        if (holder.Value is null) {
            return true;
        }

        var peer = Socket.Peers.FirstOrDefault(p => p.Id == holder.Key);
        if (peer is null) {
            _leases.Remove(holder.Key);
            return true;
        }

        // entering now would load the host copy and lose what the client did there
        EmpLog.Information("Recalling zone {ZoneFullName} from {@Peer} before entering",
            zone.ZoneFullName, peer);

        _pendingHostMove = (zone, transition);
        peer.Send(new ZoneLeaseRecall {
            ZoneUid = zone.uid,
            Soft = CanRecallSoftly(zone),
        });
        EmpPop.Information("emp_travel_recalling".lang(), peer);
        return false;
    }

    /// <summary>
    ///     Net event: Client wants to travel to a zone the host is not in
    /// </summary>
    private void OnZoneLeaseRequest(ZoneLeaseRequest request, ISteamNetPeer peer)
    {
        // the zone of a quest, and the host stands next to that player: it is asked along first
        if (!AskAlongToQuestZone(request, peer)) {
            LeaseZone(request, peer);
        }
    }

    private void LeaseZone(ZoneLeaseRequest request, ISteamNetPeer peer)
    {
        var zone = request.Blueprint is { } blueprint && GetPeerDenyReason(peer) is null
            ? CreateClientZone(blueprint)
            : game.spatials.Find(request.ZoneUid);

        // a land closed to that player: it stays where it is and hears why (Teams.Allowed, host box "Land rules")
        if (Teams.EntryDenied(PlayerChara(peer.Id), zone) is { } closed) {
            EmpLog.Information("Denied zone lease {ZoneFullName} to {@Peer}: {Reason}", request.ZoneFullName, peer, closed);
            peer.Send(new ZoneLeaseDenied {
                ZoneUid = request.ZoneUid,
                Reason = closed,
            });
            return;
        }

        // another player simulates the zone: join it as a guest of its zone session
        if (zone is { IsRegion: false } && GetPeerDenyReason(peer) is null && zone != _zone &&
            FindZoneHolder(zone.uid, peer) is { } holder) {
            if (!MayHost(holder, peer, out var denial)) {
                peer.Send(new ZoneLeaseDenied {
                    ZoneUid = request.ZoneUid,
                    Reason = denial,
                });
                return;
            }

            GrantGuestLease(zone, request, peer, holder);
            return;
        }

        if ((GetPeerDenyReason(peer) ?? GetLeaseDenyReason(zone, request, peer)) is { } reason) {
            EmpLog.Information("Denied zone lease {ZoneFullName} to {@Peer}: {Reason}",
                request.ZoneFullName, peer, reason);

            peer.Send(new ZoneLeaseDenied {
                ZoneUid = request.ZoneUid,
                Reason = reason,
                // a zone that player made itself has another number there, see ElinNetClient.OnZoneLeaseDenied
                HostZoneUid = reason == "emp_travel_host_zone" ? zone?.uid ?? 0 : 0,
            });
            return;
        }

        // GetLeaseDenyReason refuses an unknown zone
        if (zone is null) {
            return;
        }

        // its game reads the date we send: it would regenerate the cave at the door, see KeepAlive
        if (HasPlayerIn(zone)) {
            KeepAlive(zone);
        }

        // before the lease is held: a map that cannot be read (disk) left the zone held by nobody, for good, and the
        // player waiting (reliability audit 2026-10-27, #4)
        Dictionary<string, LZ4Bytes>? map;
        try {
            map = zone.IsRegion || !zone.isGenerated
                ? null
                : ZoneLeaseState.CollectMap(zone);
        } catch (Exception ex) {
            EmpLog.Warning(ex, "Map of zone {ZoneFullName} could not be read, lease denied to {@Peer}", zone.ZoneFullName, peer);
            peer.Send(new ZoneLeaseDenied {
                ZoneUid = request.ZoneUid,
                Reason = "emp_travel_invalid",
            });
            return;
        }

        // a guest moving on is no longer one, see HandOverZone
        _guests.Remove(peer.Id);
        var rangeStart = ReserveLease(peer, zone);

        EmpLog.Information("Leased zone {ZoneFullName} to {@Peer}, uid range from {UidRangeStart}, map {HasMap}",
            zone.ZoneFullName, peer, rangeStart, map is not null);

        peer.Send(new ZoneLeaseGrant {
            ZoneUid = zone.uid,
            RequestedUid = request.ZoneUid,
            UidRangeStart = rangeStart,
            QuestUidRangeStart = ReserveQuestUids(),
            ZoneState = ZoneLeaseState.GetState(zone),
            IdCurrentSubset = zone.idCurrentSubset,
            Branch = LeasedBase.Pack(zone),
            Name = LeasedBase.Name(zone),
            BaseElements = LeasedBase.Elements(zone),
            Map = map,
        });
    }

    /// <summary>
    ///     Net event: Client flushed its last actions on the host map and left it
    /// </summary>
    private void OnZoneLeaseAck(ZoneLeaseAck ack, ISteamNetPeer peer)
    {
        if (_pendingGuests.TryGetValue(peer.Id, out var guest) && guest.ZoneUid == ack.ZoneUid) {
            DepartFromHostMap(peer);
            SendGuestRequest(peer, guest.ZoneUid, guest.HolderId, ack.Stood, ack.Arrival);
            return;
        }

        if (!_leases.TryGetValue(peer.Id, out var zones) || !zones.ContainsKey(ack.ZoneUid)) {
            EmpLog.Warning("Player {@Peer} acknowledged zone {ZoneUid} without holding its lease",
                peer, ack.ZoneUid);
            return;
        }

        if (!DepartFromHostMap(peer)) {
            return;
        }

        peer.Send(new ZoneLeaseDepart {
            ZoneUid = ack.ZoneUid,
        });
    }

    /// <summary>
    ///     Take the player off the host map, false if it already left
    /// </summary>
    private bool DepartFromHostMap(ISteamNetPeer peer)
    {
        _settled.Remove(peer.Id);

        if (!_departed.Add(peer.Id)) {
            return false;
        }

        _departedAt[peer.Id] = UnityEngine.Time.realtimeSinceStartup;

        // walking out of a harvest alone, before the one who took the quest: its bag is searched now, as the
        // game does on the way out (ZoneEventHarvest.OnLeaveZone), the taker's will be when it leaves
        if (_zone is { instance: ZoneInstanceRandomQuest } questZone && questZone.events.GetEvent<ZoneEventHarvest>() is not null &&
            ActiveRemoteCharas.TryGetValue(peer.Id, out var visitor)) {
            var taken = new List<Thing>();
            foreach (var member in CompanionHelper.CompanionsOf(visitor).Prepend(visitor)) {
                member.things.Foreach(t => {
                    if (t.GetBool(115) && EClass.rnd(2) != 0) {
                        taken.Add(t);
                    }
                });
            }

            if (taken.Count > 0) {
                using var told = MsgRelayContext.RedirectTo(visitor);
                using var standIn = PlayerStandIn.For(this, peer.Id, visitor);
                Msg.Say("harvest_confiscate", taken.Count.ToString());
                foreach (var thing in taken) {
                    thing.Destroy();
                }

                EClass.player.ModKarma(-1);
            }
        }

        // apply the player's last actions on the host map now and send their results back
        // (stack merges, rebinds...) before it stops listening, see ZoneLeaseDepart
        WorldStateDeltaProcess();
        // what it was handed and still holds out of any bag goes with its character (issue 48)
        ThingRequest.ReturnDanglingOf(peer.Id);
        Delta.RefreshBuffer();
        WorldStateDeltaUpdate();

        // like a disconnect, without closing the connection
        PendingRebind.ReleasePeer(peer.Id);

        if (States.Remove(peer.Id, out var state)) {
            Session.CurrentPlayers.Remove(state);
        }

        if (ActiveRemoteCharas.Remove(peer.Id, out var remoteChara)) {
            TakeMinionsAlong(remoteChara);
            RemoveRemoteChara(remoteChara);
            TakeCompanionsAlong(remoteChara);
        }

        Broadcast(SessionPlayersSnapshot.Create());

        EmpLog.Information("Player {@Peer} left the host map",
            peer);
        return true;
    }

    /// <summary>
    ///     The client holding the zone, if another connected player
    /// </summary>
    /// <summary>
    ///     Security audit 2026-10-27, item 8 (docs_zone_sessions.md): a player holding a map is the host of those who
    ///     visit it, their characters, bags and lives included, and a modified game abuses that. Where strangers come
    ///     in (TrustEnforced) a player is placed only under a holder it trusts already: its teammate (not the
    ///     Wanderers), or one the host vouched for (an admin, or let in with "allow"). Logged otherwise
    /// </summary>
    private bool MayHost(ISteamNetPeer holder, ISteamNetPeer visitor) => MayHost(holder, visitor, out _);

    /// <summary>
    ///     The holder is trusted by the host's own lists to hold that visitor: an admin or a player let in with
    ///     "allow", or a teammate (not the Wanderers)
    /// </summary>
    private bool TrustsHolder(ISteamNetPeer holder, UserData visitor)
    {
        if (Admins.ContainsKey(holder.User) || Members.ContainsKey(holder.User)) {
            return true;
        }

        var team = SavedRemoteCharas.TryGetValue(holder.User, out var held) ? Teams.TeamOf(held) : Teams.Wanderers;
        return team != Teams.Wanderers && SavedRemoteCharas.TryGetValue(visitor, out var visiting) && Teams.TeamOf(visiting) == team;
    }

    /// <summary>
    ///     Strangers of one holder at most (council 2026-10-28, Q4): a busy map is told "full", the visitor stays where it is
    /// </summary>
    internal const int StrangerVisitorCap = 10;

    private int VisitorsOf(ISteamNetPeer holder) =>
        _guests.Values.Count(g => g.HolderId == holder.Id) + _pendingGuests.Values.Count(g => g.HolderId == holder.Id);

    /// <param name="denial">the text id to tell the visitor when it is refused</param>
    private bool MayHost(ISteamNetPeer holder, ISteamNetPeer visitor, out string denial)
    {
        denial = "emp_travel_stranger_map";
        if (TrustsHolder(holder, visitor.User)) {
            return true;
        }

        // the host box "Strangers may visit a player's map": land rules were checked before, the holder's cap is here
        if (EmpConfig.Server.StrangerVisits.Value) {
            if (VisitorsOf(holder) < StrangerVisitorCap) {
                return true;
            }

            denial = "emp_travel_map_full";
            EmpLog.Information("Refused {@Visitor} on the map of {@Holder}: {Cap} visitors already", visitor, holder, StrangerVisitorCap);
            return false;
        }

        var enforce = ElinDelta.TrustEnforced;
        EmpLog.Warning(enforce ? "Refused {@Visitor} on the map of {@Holder}: not its teammate, nor vouched for" :
            "Would have refused {@Visitor} on the map of {@Holder}: not its teammate, nor vouched for", visitor, holder);
        return !enforce;
    }

    private ISteamNetPeer? FindZoneHolder(int zoneUid, ISteamNetPeer asking)
    {
        foreach (var (peerId, zones) in _leases) {
            if (peerId != asking.Id && zones.ContainsKey(zoneUid)) {
                return Socket.Peers.FirstOrDefault(p => p.Id == peerId);
            }
        }

        return null;
    }

    /// <summary>
    ///     Lease the zone to the player, returns the first card uid it may allocate
    /// </summary>
    private int ReserveLease(ISteamNetPeer peer, Zone zone)
    {
        // above the host counter and above any range handed out and still in use
        var floor = _leases.Values.SelectMany(z => z.Values).DefaultIfEmpty(0).Max();
        var rangeStart = Math.Max(Math.Max(game.cards.uidNext, floor), LeaseRangeFloor[FloorCards]) + LeaseUidHeadroom;
        LeaseRangeFloor[FloorCards] = rangeStart;

        if (!_leases.TryGetValue(peer.Id, out var zones)) {
            zones = _leases[peer.Id] = [];
        }

        zones[zone.uid] = rangeStart;
        return rangeStart;
    }

    /// <summary>
    ///     The owner of a zone left it or dropped: the players visiting it stay, the first one simulates it
    ///     from now on and the others join its zone session. Unless the host is on its way in, then they come back
    /// </summary>
    /// <param name="dropped">the owner dropped: what is known of its guests is its last checkpoint</param>
    /// <param name="left">what the owner handed back when it left on purpose</param>
    private void HandOverZone(int zoneUid, int holderId, bool dropped = false, ZoneLeaseRelease? left = null)
    {
        var guests = new List<ISteamNetPeer>();
        foreach (var (guestId, at) in _guests.ToList()) {
            if (at.ZoneUid != zoneUid || at.HolderId != holderId) {
                continue;
            }

            _guests.Remove(guestId);
            if (_departed.Contains(guestId) && Socket.Peers.FirstOrDefault(p => p.Id == guestId) is { } guest) {
                guests.Add(guest);
            }
        }

        // on their way to the old owner, it will not expect them anymore
        foreach (var (guestId, at) in _pendingGuests.ToList()) {
            if (at.ZoneUid == zoneUid && at.HolderId == holderId &&
                Socket.Peers.FirstOrDefault(p => p.Id == guestId) is { } guest) {
                RefuseGuest(guest, zoneUid);
            }
        }

        if (guests.Count == 0) {
            return;
        }

        if (_pendingHostMove?.Zone.uid == zoneUid || game.spatials.Find(zoneUid) is not { } zone) {
            foreach (var guest in guests) {
                guest.Send(new ZoneLeaseRecall {
                    ZoneUid = zoneUid,
                });
            }

            return;
        }

        var heir = guests[0];
        var rangeStart = ReserveLease(heir, zone);

        // the copy its owner just handed back is the reference (we hold it now, and so will everyone who loads
        // this zone later), not what stands on the screen of the heir: it goes along with its numbers, loaded
        // there only when they differ, see ElinNetClient.AdoptHostCopy. Not after a drop: what we hold then is
        // an old checkpoint, the copy of the heir is the newer one
        var map = Session.Rules.AutoResync && zone != _zone && left is { MapSums: not null } ? left.Map : null;
        var sums = map is null ? null : left!.MapSums;

        EmpLog.Information("Zone {ZoneFullName} handed over to {@Peer}, uid range from {UidRangeStart}, owner copy sent along {HasMap}: {Sums}",
            zone.ZoneFullName, heir, rangeStart, map is not null, ZoneLeaseState.TellSums(sums ?? []));

        heir.Send(new ZoneLeaseGrant {
            ZoneUid = zoneUid,
            RequestedUid = zoneUid,
            UidRangeStart = rangeStart,
            QuestUidRangeStart = ReserveQuestUids(),
            ZoneState = ZoneLeaseState.GetState(zone),
            IdCurrentSubset = zone.idCurrentSubset,
            Branch = LeasedBase.Pack(zone),
            Name = LeasedBase.Name(zone),
            BaseElements = LeasedBase.Elements(zone),
            Handoff = true,
            Map = map,
            MapSums = sums,
        });

        foreach (var guest in guests.Skip(1)) {
            // one the heir may not host comes back to the host (security audit 2026-10-27, item 8)
            if (!MayHost(heir, guest)) {
                guest.Send(new ZoneLeaseRecall {
                    ZoneUid = zoneUid,
                });
                continue;
            }

            _pendingGuests[guest.Id] = (zoneUid, heir.Id);
            SendGuestRequest(guest, zoneUid, heir.Id, stays: true, stale: dropped);
        }
    }

    /// <summary>
    ///     Guest peer id -> the map it asked to join and the number its game knows it by, while the player holding
    ///     that map is asked first, see GrantGuestLease
    /// </summary>
    private readonly Dictionary<int, (int ZoneUid, int RequestedUid)> _guestAsks = [];

    private void GrantGuestLease(Zone zone, ZoneLeaseRequest request, ISteamNetPeer peer, ISteamNetPeer holder)
    {
        _guests.Remove(peer.Id);
        _pendingGuests[peer.Id] = (zone.uid, holder.Id);
        _guestAsks[peer.Id] = (zone.uid, request.ZoneUid);

        EmpLog.Information("Player {@Peer} asks to join zone {ZoneFullName} held by {@Holder}",
            peer, zone.ZoneFullName, holder);

        // the holder is asked first, the guest still where it is: refused, it stays there. It used to leave its place
        // first, and a refusal brought it back with the whole world (RefuseGuest, then SendRejoin). Asked again once
        // the guest left its place, with where it stands, see OnZoneLeaseAck
        SendGuestRequest(peer, zone.uid, holder.Id);
    }

    /// <summary>
    ///     The holder expects the guest: the guest leaves its current place (ack or release), then the holder is told
    ///     where it arrives
    /// </summary>
    private void SendGuestGrant(ISteamNetPeer guest, Zone zone, int requestedUid)
    {
        guest.Send(new ZoneLeaseGrant {
            ZoneUid = zone.uid,
            RequestedUid = requestedUid,
            UidRangeStart = 0,
            ZoneState = ZoneLeaseState.GetState(zone),
            IdCurrentSubset = zone.idCurrentSubset,
            Guest = true,
        });
    }

    /// <param name="stood">the tile the guest says it stands on, on that very map</param>
    /// <param name="arrival">the way it walks in by</param>
    /// <param name="stays">it never left that map and says nothing: the tile the last upload of its character has</param>
    /// <param name="stale">that upload is an old one</param>
    private void SendGuestRequest(ISteamNetPeer guest, int zoneUid, int holderId, Position? stood = null,
        ZoneArrival? arrival = null, bool stays = false, bool stale = false)
    {
        var holder = Socket.Peers.FirstOrDefault(p => p.Id == holderId);
        var chara = SavedRemoteCharas.TryGetValue(guest.User, out var uid) ? game.cards.globalCharas.Find(uid) : null;

        if (holder is null || chara is null) {
            RefuseGuest(guest, zoneUid);
            return;
        }

        holder.Send(new ZoneGuestRequest {
            ZoneUid = zoneUid,
            GuestUser = guest.User,
            Chara = LZ4Bytes.Create(chara),
            Companions = CompanionHelper.CompanionsOf(chara).Select(c => LZ4Bytes.Create(c)).ToList(),
            // ponytail: after a holder dropped this is the tile of its last checkpoint, the guest may have walked since
            Stood = stood ?? (stays ? (Position?)chara.pos : null),
            StoodStale = stale,
            Arrival = arrival,
        });
    }

    /// <summary>
    ///     Net event: the zone holder expects the guest, or refuses it
    /// </summary>
    private void OnZoneGuestReady(ZoneGuestReady ready, ISteamNetPeer holder)
    {
        var guest = Socket.Peers.FirstOrDefault(p =>
            (ulong)p.User == ready.GuestUser &&
            _pendingGuests.TryGetValue(p.Id, out var pending) && pending.ZoneUid == ready.ZoneUid);

        if (guest is null) {
            EmpLog.Warning("Zone guest {RemoteIdentity} is gone", ready.GuestUser);
            return;
        }

        if (!ready.Accepted) {
            RefuseGuest(guest, ready.ZoneUid);
            return;
        }

        // the first answer, the guest still where it was: now it goes
        if (_guestAsks.Remove(guest.Id, out var ask)) {
            if (game.spatials.Find(ask.ZoneUid) is not { } zone) {
                RefuseGuest(guest, ready.ZoneUid);
                return;
            }

            SendGuestGrant(guest, zone, ask.RequestedUid);
            return;
        }

        _guests[guest.Id] = _pendingGuests[guest.Id];
        _visitedHolder[guest.User] = _guests[guest.Id].HolderId;
        _pendingGuests.Remove(guest.Id);

        guest.Send(new ZoneLeaseDepart {
            ZoneUid = ready.ZoneUid,
            Guest = new() {
                HostUser = holder.User,
                Port = ready.Port,
                VirtualPort = ready.VirtualPort,
                Local = ready.Local,
            },
        });

        EmpLog.Information("Player {@Peer} joins the zone session of {@Holder}",
            guest, holder);
    }

    /// <summary>
    ///     The guest already left its place, it comes back to the host
    /// </summary>
    private void RefuseGuest(ISteamNetPeer guest, int zoneUid)
    {
        _pendingGuests.Remove(guest.Id);
        // still where it was (refused at the first question): it only hears no, see ElinNetClient.OnZoneLeaseDenied
        _guestAsks.Remove(guest.Id);

        EmpLog.Information("Zone {ZoneUid} refused player {@Peer}, it returns to the host",
            zoneUid, guest);

        guest.Send(new ZoneLeaseDenied {
            ZoneUid = zoneUid,
            Reason = "emp_travel_occupied",
        });
    }

    /// <summary>
    ///     Net event: Client hands a leased zone back
    /// </summary>
    private void OnZoneLeaseRelease(ZoneLeaseRelease release, ISteamNetPeer peer)
    {
        if (!_departed.Contains(peer.Id)) {
            // a return asked by a player still on our map: its travel never went through (an answer lost, a throw
            // in the middle of it) and it waits for the world, for good if nothing answers (reliability audit
            // 2026-10-27, #3). Off the map, then back as any return
            if (release.Rejoin && !release.Checkpoint && ActiveRemoteCharas.ContainsKey(peer.Id)) {
                EmpLog.Warning("Player {@Peer} asks to come back while on the host map, sending it the world", peer);
                DepartFromHostMap(peer);
            } else {
                // stale or repeated release, the player is already back on the host map
                EmpLog.Warning("Player {@Peer} released zone {ZoneUid} while not away, ignoring",
                    peer, release.ZoneUid);
                return;
            }
        }

        if (release.Checkpoint) {
            ApplyCheckpoint(release, peer);
            return;
        }

        var handedBack = false;
        // the base of the zone it held, when that player may manage it: its residents come back with it
        var managed = -1;
        if (_leases.TryGetValue(peer.Id, out var zones) && zones.Remove(release.ZoneUid, out var rangeStart)) {
            handedBack = true;

            if (game.spatials.Find(release.ZoneUid) is { IsRegion: false } zone && !_questZones.Contains(zone.uid) &&
                ApplyLeasedZone(zone, release, peer)) {
                managed = zone.uid;
            }

            // only a range the client allocated in pushes the host counter
            if (release.UidNext > rangeStart) {
                game.cards.uidNext = Math.Max(game.cards.uidNext, Math.Min(release.UidNext, rangeStart + MaxLeaseUids));
            }

            // every range came back with what was used of it: nothing above the counter is in use anymore
            if (_leases.Values.All(z => z.Count == 0)) {
                LeaseRangeFloor[FloorCards] = 0;
            }
        } else if (release.ZoneUid != -1) {
            EmpLog.Warning("Player {@Peer} released zone {ZoneUid} without holding its lease, ignoring the zone",
                peer, release.ZoneUid);
        }

        // a guest hands nothing back, it is only leaving
        var wasVisitor = _guests.Remove(peer.Id);

        // before the zone is handed over: visitors are called back by it, and come back with the world
        var soft = handedBack && CanRejoinSoftly(release, peer);
        // slice 2: it walks into the map we stand on, see SendSoftReturn
        var softReturn = handedBack && !soft && CanReturnSoftly(release, peer);

        // the numbers given to its cards that waited for one, see ZoneSoftRejoin.Rebinds
        var rebound = new Dictionary<int, int>();
        var chara = ReplaceRemoteChara(peer.User, release.Chara, rebound: rebound, capGainsOf: wasVisitor ? peer.Id : -1);
        ReplaceCompanions(release.Companions, SavedRemoteCharas.TryGetValue(peer.User, out var ownerUid) ? ownerUid : 0, rebound);
        ReplaceResidents(release.Residents, ownerUid, managed);
        DropGoneResidents(release.GoneResidents, ownerUid);
        ReplaceGuestCharas(release, peer);

        // never next to the host when something tells where: it walks into the host's map by the way it took, or
        // the host is the one coming to the map it stands on (held or only visited) and it stays on its tile
        if (release.Rejoin && release.Arrival is { } arrival) {
            _returnSpots[peer.User] = (arrival.ZoneUid, null, arrival, UnityEngine.Time.unscaledTime + ReturnSpotSeconds, false);
        } else if (release.Rejoin && chara?.pos is { } stood) {
            _returnSpots[peer.User] = (release.StoodZoneUid, stood.Copy(), null, UnityEngine.Time.unscaledTime + ReturnSpotSeconds, false);
        } else {
            _returnSpots.Remove(peer.User);
        }

        if (handedBack) {
            // before looking for someone to take it over: there is nothing to take over
            DestroyQuestZone(release.ZoneUid);
            HandOverZone(release.ZoneUid, peer.Id, left: release);
        }

        if (release.Rejoin) {
            if (zones is { Count: > 0 }) {
                EmpLog.Warning("Player {@Peer} rejoins while still holding zones {ZoneUids}, dropping them",
                    peer, zones.Keys);

                foreach (var zoneUid in zones.Keys.ToArray()) {
                    DestroyQuestZone(zoneUid);
                }
            }

            _leases.Remove(peer.Id);
            _departed.Remove(peer.Id);
            _departedAt.Remove(peer.Id);
            _checkpointUidNext.Remove(peer.Id);
            // on its way to another player's map that never expected it: that answer, if it comes, is for nobody
            _pendingGuests.Remove(peer.Id);
            _guestAsks.Remove(peer.Id);

            if (chara is { isDead: false } && soft) {
                // no copy of the world: it stays on the map it handed back, see CompleteSoftRejoin
                RegisterSoftRejoin(chara, peer, release.ZoneUid, rebound);
            } else if (chara is { isDead: false } && softReturn) {
                // no copy of the world either: what it cannot know, then this one map
                SendSoftReturn(chara, peer, rebound);
            } else if (chara is not null) {
                EmpLog.Information("Player {@Peer} returns to the host zone",
                    peer);

                SendSaveProbe(chara, peer);
            }

            // the save is asked for, not made in this frame, so that players coming back in a row and the host's own
            // move cost one save. Until it is made (5 s, 30 s at most if a menu or a fight holds it) the map handed
            // back waits beside the save with the character (ZoneLeaseState.StageMap): a host crash meanwhile loses
            // that return whole, nothing of it is doubled or half there. Unticked AutoSave or dedicated server: saved
            // here, as soon as the game may
            if (!EClass.debug.ignoreAutoSave && !EmpAutoHost.RequestSave()) {
                EmpAutoHost.SaveFromMessage("a player came back");
            }
        }

        ResumePendingHostMove();
    }

    /// <summary>
    ///     Progress of a client still away: its zone and character, the lease stays
    /// </summary>
    private void ApplyCheckpoint(ZoneLeaseRelease checkpoint, ISteamNetPeer peer)
    {
        if (!_leases.TryGetValue(peer.Id, out var zones) || !zones.ContainsKey(checkpoint.ZoneUid)) {
            EmpLog.Warning("Player {@Peer} sent a checkpoint for zone {ZoneUid} it does not hold",
                peer, checkpoint.ZoneUid);
            return;
        }

        var managed = game.spatials.Find(checkpoint.ZoneUid) is { IsRegion: false } zone && ApplyLeasedZone(zone, checkpoint, peer)
            ? checkpoint.ZoneUid
            : -1;

        // the client keeps allocating in its range, the host counter only moves on release or disconnect
        var start = zones[checkpoint.ZoneUid];
        _checkpointUidNext[peer.Id] = Math.Min(Math.Max(checkpoint.UidNext, start), start + MaxLeaseUids);

        ReplaceRemoteChara(peer.User, checkpoint.Chara);
        ReplaceCompanions(checkpoint.Companions, SavedRemoteCharas.TryGetValue(peer.User, out var ownerUid) ? ownerUid : 0);
        ReplaceResidents(checkpoint.Residents, ownerUid, managed);
        DropGoneResidents(checkpoint.GoneResidents, ownerUid);
        ReplaceGuestCharas(checkpoint, peer);

        EmpLog.Debug("Checkpoint of player {@Peer} in zone {ZoneUid}",
            peer, checkpoint.ZoneUid);
    }

    /// <summary>
    ///     Player -> where it stands the next time it is put on that map: the tile it stood on there (the map
    ///     is reloaded under it, or it dropped), or the way it walks in by. By identity: a guest of a zone session
    ///     is announced before it connects, see RegisterGuest. Stale: the tile is an old one, good for the player
    ///     for want of better, not for its companions
    /// </summary>
    private readonly Dictionary<ulong, (int ZoneUid, Point? Pos, ZoneArrival? Arrival, float Until, bool Stale)> _returnSpots = [];

    // long enough for a game that reloads a big world before it stands on the map
    private const float ReturnSpotSeconds = 300f;

    /// <summary>
    ///     Net event: the player does not take the lease it was granted
    /// </summary>
    private void OnZoneLeaseDecline(ZoneLeaseDecline decline, ISteamNetPeer peer)
    {
        // only a visit of that very map: a player declining a map it was handed meanwhile is still on its way to the
        // one it asked for
        if (_pendingGuests.TryGetValue(peer.Id, out var going) && going.ZoneUid == decline.ZoneUid) {
            _pendingGuests.Remove(peer.Id);
            _guestAsks.Remove(peer.Id);
        }

        if (!_leases.TryGetValue(peer.Id, out var zones) || !zones.Remove(decline.ZoneUid)) {
            return;
        }

        if (zones.Count == 0) {
            _leases.Remove(peer.Id);
        }

        EmpLog.Information("Player {@Peer} declined the lease of zone {ZoneUid}", peer, decline.ZoneUid);
        DestroyQuestZone(decline.ZoneUid);
        ResumePendingHostMove();
    }

    private void ResumePendingHostMove()
    {
        if (_pendingHostMove is not { } move || _leases.Values.Any(z => z.ContainsKey(move.Zone.uid))) {
            return;
        }

        _pendingHostMove = null;

        EmpLog.Information("Zone {ZoneFullName} handed back, host enters",
            move.Zone.ZoneFullName);

        pc.MoveZone(move.Zone, move.Transition);
    }

    /// <returns>true when the base of the zone came back too (that player may manage it): see ReplaceResidents</returns>
    private bool ApplyLeasedZone(Zone zone, ZoneLeaseRelease release, ISteamNetPeer holder)
    {
        if (zone == _zone) {
            // host walked in meanwhile and simulates it, host copy wins
            EmpLog.Warning("Zone {ZoneFullName} is active on host, discarding the client copy",
                zone.ZoneFullName);
            return false;
        }

        if (release.Map is not null) {
            if (zone.map is not null) {
                // drop the stale in-memory copy, next activation loads the files
                // its cards leave the cache too, the client copies carry the same uids
                ForgetCachedMap(zone.map);
                zone.UnloadMap();
            }

            // in the save with the next save of the host's game, which holds the characters that go with it
            ZoneLeaseState.StageMap(zone, release.Map);
        }

        ZoneLeaseState.ApplyState(zone, release.ZoneState, release.IdCurrentSubset);
        ZoneLeaseState.ApplyFortuneRoll(zone, release.FortuneRoll);

        // its base, simulated there alone (lease audit 2026-10-29, #2 and #4), and what it earned, paid once (#5)
        var managed = ManagesLeasedBase(zone, holder);
        if (managed) {
            LeasedBase.Apply(zone, release.Branch, release.BaseElements, release.Name);
        } else if (zone.branch is not null && release.Branch is not null) {
            EmpLog.Information("The base {ZoneFullName} handed back by {@Peer} is not its to manage, the host keeps its own",
                zone.ZoneFullName, holder);
        }

        LeasedBase.PayIncome(zone, release.Income);

        // the date came with that copy; before the save and the move of the host that follow a recall
        if (HasPlayerIn(zone)) {
            KeepAlive(zone);
        }

        // recalled because the host walks in while a player is there, see BossFleePatch
        if (_pendingHostMove is { } joining && joining.Zone.uid == zone.uid) {
            ZoneLeaseState.Imported.Add(zone.uid);
        }

        // the client just left, no catch-up simulation owed
        zone.lastActive = world.date.GetRaw();

        EmpLog.Information("Applied client copy of zone {ZoneFullName}, map {HasMap}, generated {IsGenerated}, base {Base}",
            zone.ZoneFullName, release.Map is not null, zone.isGenerated, managed && release.Branch is not null);
        return managed && zone.branch is not null;
    }

    /// <summary>
    ///     The player holding that zone may change its base: a land of no team or of its team (and its rule), and the
    ///     host does not keep the bases to itself. The rules a player on the host's map meets (PolicyStateDelta,
    ///     BaseRequestDelta)
    /// </summary>
    private bool ManagesLeasedBase(Zone zone, ISteamNetPeer holder)
    {
        return zone.branch is not null && !Session.Rules.HostManagesBase &&
               SavedRemoteCharas.TryGetValue(holder.User, out var uid) && game.cards.globalCharas.Find(uid) is { } chara &&
               Teams.CanTouch(chara, zone);
    }

    /// <summary>
    ///     Swap the host copy of the player character for the one simulated by the client
    /// </summary>
    /// <param name="register">zone session: take the uploaded uid as the saved chara of that player</param>
    /// <param name="rebound">filled with the numbers given to its cards that waited for one: old -> new</param>
    /// <param name="capGainsOf">a visitor's own upload (its peer id): the gold it gained since the host's copy counts
    ///     against the hourly gain cap like any gold it earned (public servers), the rest is cut off</param>
    private Chara? ReplaceRemoteChara(UserData user, LZ4Bytes data, bool register = false, Dictionary<int, int>? rebound = null,
        int capGainsOf = -1)
    {
        var uploaded = data.Decompress<Chara>();

        if (register) {
            SavedRemoteCharas[user] = uploaded.uid;
        }

        if (!SavedRemoteCharas.TryGetValue(user, out var uid)) {
            EmpLog.Warning("Player {RemoteIdentity} has no saved remote chara", user);
            return null;
        }

        var old = game.cards.globalCharas.Find(uid);

        if (uploaded.uid != uid) {
            EmpLog.Warning("Player {RemoteIdentity} uploaded chara {UploadedUid}, expected {Uid}, keeping host copy",
                user, uploaded.uid, uid);
            return old;
        }

        if (capGainsOf >= 0 && old is not null && ElinDelta.TrustEnforced) {
            foreach (var currency in CardModCurrencyDelta.CurrencyIds) {
                var gain = (long)uploaded.GetCurrency(currency) - old.GetCurrency(currency);
                if (gain > 0 && CardModCurrencyDelta.GainRoom(capGainsOf, currency, gain) is var allowed && allowed < gain) {
                    EmpLog.Warning("Cut {Cut} {CurrencyId} of the upload of {RemoteIdentity}: over its hourly gain", gain - allowed, currency, user);
                    uploaded.ModCurrency((int)(allowed - gain), currency);
                }
            }
        }

        // a god's gifts given on this map are counted on our copy, those given away on its own: both kept, and its
        // game hears of ours (GitHub issue 51, see RemoteGodGiftPatch.Merge)
        var behind = Patches.RemoteGodGiftPatch.Merge(uploaded, old);
        if (behind.Count > 0 && Socket.Peers.FirstOrDefault(p => Equals(p.User, user)) is { } owner) {
            foreach (var (religion, rank) in behind) {
                SendDeltaTo(owner.Id, new GodGiftDelta {
                    Religion = religion,
                    Rank = rank,
                });
            }
        }

        // stale entries at its uids (copies received earlier) would get it renumbered when cached again
        ForgetCachedCard(uploaded);

        if (old is not null) {
            ForgetCachedCard(old);
            // branches hold object references, the new copy joins again through MakeAlly
            EClass.Home.FindBranch(old)?.RemoveMemeber(old);
            game.cards.globalCharas.Remove(old);
        }

        uploaded.SetBool(CINT.IsPC, false);
        // its own game's copy of the party is led by whoever was the host's character there: the one here leads it,
        // so that a later swap of the local character moves it along (PartyRepair.Repoint)
        if (uploaded.party is { } copy && copy != pc.party && copy.uidLeader != pc.uid) {
            copy.leader = pc;
        }

        // not in any host map until it rejoins, the zone it left must not pull it back in
        uploaded.currentZone = null;
        game.cards.globalCharas.Add(uploaded);

        // client side ability tokens and cards still waiting for a host uid are not real host cards
        InvPlaceAbilityDelta.InvalidateFakeAbilityCard(uploaded);
        foreach (var thing in uploaded.things.Flatten().ToList()) {
            if (PendingUid.IsPending(thing.uid)) {
                var pending = thing.uid;
                game.cards.AssignUID(thing);
                rebound?[pending] = thing.uid;
            }
        }

        EmpLog.Debug("Replaced remote chara {Uid} of player {RemoteIdentity}",
            uid, user);

        return uploaded;
    }

    /// <summary>
    ///     Players visiting the zone are simulated by its owner, their characters come with its uploads
    /// </summary>
    private void ReplaceGuestCharas(ZoneLeaseRelease release, ISteamNetPeer holder)
    {
        foreach (var (user, chara) in release.GuestCharas ?? []) {
            // a guest that left since (back here or elsewhere) brought a newer copy itself
            if (Socket.Peers.FirstOrDefault(p => (ulong)p.User == user) is { } guest &&
                (!_guests.TryGetValue(guest.Id, out var at) || at.HolderId != holder.Id)) {
                continue;
            }

            // gone meanwhile: only a visitor of that very holder
            if (!_visitedHolder.TryGetValue(user, out var visited) || visited != holder.Id) {
                EmpLog.Warning("Player {@Peer} sent the character of {User}, who never visited its map: refused", holder, user);
                continue;
            }

            // a stranger's map does not decide a visitor's character, bag or gold (council 2026-10-28, Q4): only the
            // visitor's own upload does (the holder of another team is the authority of its map, not of the visitor)
            if (ElinDelta.TrustEnforced && !TrustsHolder(holder, user)) {
                EmpLog.Information("Player {@Peer} sent the character of {User}, who is not of its team: only its own upload counts", holder, user);
                continue;
            }

            ReplaceRemoteChara(user, chara);

            if (release.GuestCompanions?.TryGetValue(user, out var companions) is true &&
                SavedRemoteCharas.TryGetValue(user, out var ownerUid)) {
                ReplaceCompanions(companions, ownerUid);
            }
        }
    }

    private static void ForgetCachedCard(Card card)
    {
        // by number, whatever object holds it: the copy uploaded by a player replaces the host's under the same
        // number, and must find the place free (CardCache.Add would renumber it otherwise, and nobody would
        // recognise that player's character anymore)
        CardCache.Remove(card.uid);

        foreach (var thing in card.things) {
            ForgetCachedCard(thing);
        }
    }

    private static void ForgetCachedMap(Map map)
    {
        foreach (var thing in map.things) {
            ForgetCachedCard(thing);
        }

        foreach (var chara in map.charas) {
            if (!chara.IsGlobal) {
                ForgetCachedCard(chara);
            }
        }
    }

    /// <summary>
    ///     Zone created on the client (world map field, dungeon floor...): create it here too,
    ///     the host assigns the uid and the client renumbers its copy
    /// </summary>
    private Zone? CreateClientZone(LeaseZoneBlueprint blueprint)
    {
        var parent = game.spatials.Find(blueprint.ParentUid);
        if (parent is null || !sources.zones.map.ContainsKey(blueprint.Id)) {
            EmpLog.Warning("Cannot create client zone {ZoneId}, parent {ParentUid} unknown",
                blueprint.Id, blueprint.ParentUid);
            return null;
        }

        // the copy of the world that player left with may be older than a zone created since (the next floor
        // of a dungeon another player already went down to): the same place is the same zone
        var lv = blueprint.ZoneState.Length > LvIndex ? blueprint.ZoneState[LvIndex] : 0;
        if (blueprint.Instance) {
            return CreateQuestZone(blueprint, parent);
        }

        if (parent.children.Find(c => c is Zone && c.id == blueprint.Id && c.x == blueprint.X && c.y == blueprint.Y &&
                                      c.lv == lv) is Zone existing) {
            EmpLog.Information("Client zone {ZoneFullName} already exists as uid {ZoneUid}",
                existing.ZoneFullName, existing.uid);
            return existing;
        }

        if (SpatialGen.Create(blueprint.Id, parent, true, blueprint.X, blueprint.Y, blueprint.Icon) is not Zone zone) {
            return null;
        }

        ZoneLeaseState.ApplyState(zone, blueprint.ZoneState, blueprint.IdCurrentSubset);

        if (parent is Region region) {
            region.elomap.SetZone(zone.x, zone.y, zone, true);
        }

        EmpLog.Information("Created client zone {ZoneFullName} as uid {ZoneUid}",
            zone.ZoneFullName, zone.uid);

        return zone;
    }

    /// <summary>
    ///     Zones of quests held by players, see <see cref="LeaseZoneBlueprint.Instance" />
    /// </summary>
    private readonly HashSet<int> _questZones = [];

    internal bool IsLeased(int zoneUid)
    {
        return _leases.Values.Any(zones => zones.ContainsKey(zoneUid));
    }

    /// <summary>
    ///     Destroying a zone destroys its floors without asking: one of them may be where a player is
    /// </summary>
    internal bool HasLeasedFloor(Zone top)
    {
        foreach (var zones in _leases.Values) {
            foreach (var zoneUid in zones.Keys) {
                if (game.spatials.Find(zoneUid) is { } leased && leased != top && leased.GetTopZone() == top) {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    ///     A player holds a part of that dungeon, its top or any floor
    /// </summary>
    internal bool IsHeld(Zone zone)
    {
        var top = zone.GetTopZone() ?? zone;
        return IsLeased(top.uid) || HasLeasedFloor(top);
    }

    /// <summary>
    ///     Somebody is in that dungeon: a player holds a part of it, the host stands in it, or is on its way into
    ///     the part a player just handed back
    /// </summary>
    private bool HasPlayerIn(Zone zone)
    {
        var top = zone.GetTopZone() ?? zone;
        return IsHeld(zone) || (_zone?.GetTopZone() ?? _zone) == top || (_pendingHostMove?.Zone.GetTopZone() ?? _pendingHostMove?.Zone) == top;
    }

    /// <summary>
    ///     The game expires a dungeon nobody is in: regenerated at the door with every floor destroyed
    ///     (Zone.Activate, RegenerateOnEnter), or destroyed by the next save (Zone.CanDestroy). Somebody is.
    ///     Never a zone without a date: those never expire
    /// </summary>
    internal void KeepAlive(Zone zone)
    {
        foreach (var z in new[] { zone, zone.GetTopZone() ?? zone }) {
            if (z.dateExpire != 0 && world.date.IsExpired(z.dateExpire)) {
                z.dateExpire = world.date.GetRaw() + 1440 * z.ExpireDays;
            }
        }
    }

    /// <summary>
    ///     The zone of a quest a player took: only a place holder here, so the lease has something to hold.
    ///     Never reused (two players each get their own), not on the world map (it sits on the tile of the town
    ///     the quest comes from), not announced to the other players
    /// </summary>
    private Zone? CreateQuestZone(LeaseZoneBlueprint blueprint, Spatial parent)
    {
        Zone? zone;
        SpatialGenEvent.Quiet = true;
        try {
            zone = SpatialGen.Create(blueprint.Id, parent, true, blueprint.X, blueprint.Y, blueprint.Icon) as Zone;
        } finally {
            SpatialGenEvent.Quiet = false;
        }

        if (zone is null) {
            return null;
        }

        ZoneLeaseState.ApplyState(zone, blueprint.ZoneState, blueprint.IdCurrentSubset);

        // as in the game: a zone with an instance is not a place of the world map, destroying it leaves the
        // town it sits on alone, and one nobody holds anymore is cleaned up by the next save
        zone.instance = new() {
            uidZone = scene.elomap.GetZone(blueprint.X, blueprint.Y)?.uid ?? 0,
        };
        _questZones.Add(zone.uid);

        EmpLog.Information("Created quest zone {ZoneFullName} as uid {ZoneUid}", zone.ZoneFullName, zone.uid);
        return zone;
    }

    /// <summary>
    ///     The player who held the zone of a quest left it: nothing of it is kept
    /// </summary>
    private void DestroyQuestZone(int zoneUid)
    {
        if (!_questZones.Remove(zoneUid) || game.spatials.Find(zoneUid) is not { } zone || zone == _zone) {
            return;
        }

        EmpLog.Information("Quest zone {ZoneFullName} {ZoneUid} is over", zone.ZoneFullName, zoneUid);
        zone.Destroy();
    }

    private string? GetPeerDenyReason(ISteamNetPeer peer)
    {
        if (!EmpConfig.Server.IndependentTravel.Value) {
            return "emp_travel_disabled";
        }

        if (!ActiveRemoteCharas.ContainsKey(peer.Id) && !IsAway(peer)) {
            // not joined yet
            return "emp_travel_invalid";
        }

        return null;
    }

    private string? GetLeaseDenyReason(Zone? zone, ZoneLeaseRequest request, ISteamNetPeer peer)
    {
        if (zone is null || zone.ZoneFullName != request.ZoneFullName) {
            return "emp_travel_invalid";
        }

        // (not the world map: each player walks its own copy of it, the host's included, GitHub issue 50)
        if (zone == _zone && !zone.IsRegion) {
            return "emp_travel_host_zone";
        }

        // several players may roam the world map, each on a local copy
        if (!zone.IsRegion && _leases.Any(kv => kv.Key != peer.Id && kv.Value.ContainsKey(zone.uid))) {
            return "emp_travel_occupied";
        }

        return null;
    }

    private void ReleaseLeaseOnDisconnect(ISteamNetPeer peer)
    {
        ForgetQuestCompanion(peer);

        // dropped while standing here: it comes back on its tile, as a game loads where it was saved
        if (_settled.Contains(peer.Id) && core.IsGameStarted && _zone is { } here &&
            SavedRemoteCharas.TryGetValue(peer.User, out var droppedUid) &&
            game.cards.globalCharas.Find(droppedUid)?.pos is { IsValid: true } last) {
            _returnSpots[peer.User] = (here.uid, last.Copy(), null, float.MaxValue, false);
        } else {
            _returnSpots.Remove(peer.User);
        }

        _settled.Remove(peer.Id);
        _departed.Remove(peer.Id);
        _departedAt.Remove(peer.Id);
        _pendingGuests.Remove(peer.Id);
        _guestAsks.Remove(peer.Id);
        _guests.Remove(peer.Id);
        _softRejoins.Remove(peer.Id);

        // cards of the last checkpoint are in the save now, the host must not allocate their uids
        if (_checkpointUidNext.Remove(peer.Id, out var uidNext)) {
            game.cards.uidNext = Math.Max(game.cards.uidNext, uidNext);
        }

        if (_leases.Remove(peer.Id, out var zones) && zones.Count > 0) {
            EmpLog.Warning("Player {@Peer} disconnected while away in zones {ZoneUids}, " +
                           "keeping its last checkpoint",
                peer, zones.Keys);

            // a dropped link: its automatic return puts it back on the map it held, see ReturnToAwayZone
            if (EmpDisconnectInfo.IsLinkLost(Router.DisconnectReason)) {
                _awayAt[peer.User] = (zones.Keys.First(), UnityEngine.Time.realtimeSinceStartup + LinkLostGraceSeconds);
            } else {
                _awayAt.Remove(peer.User);
            }

            foreach (var zoneUid in zones.Keys) {
                DestroyQuestZone(zoneUid);
                HandOverZone(zoneUid, peer.Id, true);
            }

            if (!EClass.debug.ignoreAutoSave) {
                EmpAutoHost.SaveFromMessage("a player away dropped");
            }
        }

        ResumePendingHostMove();
    }

    // player -> the map it held alone when its link dropped, until when it may come back there
    private readonly Dictionary<ulong, (int ZoneUid, float Until)> _awayAt = [];

    /// <summary>
    ///     A player whose link dropped while it held a map alone comes back there, as a game loads where it was:
    ///     it used to land on the host's map instead (ride2 cut_region, 2026-10-26). Not when that map is gone (a
    ///     quest map), is the host's now, or another player holds it: then the host's map, as before
    /// </summary>
    private void ReturnToAwayZone(ISteamNetPeer peer)
    {
        if (!_awayAt.Remove(peer.User, out var away)) {
            // the owner of a server's world, the first time it joins after the exchange of characters (ServerAvatar):
            // it comes back where its character was, as a player does after a lost link. Once: the zone is forgotten
            var table = ServerAvatarTable;
            if (!ServerAvatar.Active || table[AvatarOwnerZone] == 0 || (ulong)table[AvatarOwnerUser] != (ulong)peer.User) {
                return;
            }

            away = ((int)table[AvatarOwnerZone], float.MaxValue);
            table[AvatarOwnerZone] = 0;
        }

        if (UnityEngine.Time.realtimeSinceStartup > away.Until ||
            game.spatials.Find(away.ZoneUid) is not { } zone || zone == _zone ||
            (!zone.IsRegion && _leases.Any(kv => kv.Key != peer.Id && kv.Value.ContainsKey(zone.uid)))) {
            return;
        }

        EmpLog.Information("Player {@Peer} goes back to {ZoneFullName}, where its link dropped", peer, zone.ZoneFullName);
        SendDeltaTo(peer.Id, new TeamSpawnDelta { ZoneUid = zone.uid });
    }
}
