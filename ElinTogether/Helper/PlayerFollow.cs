using System.Collections.Generic;
using System.Linq;
using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;
using UnityEngine;

namespace ElinTogether.Helper;

/// <summary>
///     "Follow" on another player's character: the local character walks after it with the game's own click-to-move
///     (AI_Goto on the character). Nothing new goes on the wire for that: the steps are the ones a click would make.
///     Any key or click, a blow, a hostile in sight, a window, dead or gone stop it, with a line (llm-council
///     2026-10-24, a host option). <br />
///     GitHub issue 75 (lead dev 2026-11-03, over the council's "never travels"), host box "Follow goes across maps":
///     when the leader leaves the map, the follower takes the same way out, by the game's own systems (it walks to the
///     leader's last tile, the stairs there or the leader's own move to the same zone, which is this player's own
///     travel: lease, rejoin, zone session, land rules), and walks after it on the world map. There its steps play its
///     own turns but do not move the date (RemoteTravelRegionPatch.DateMoves): the leader's steps do, so a trip made
///     together counts once. The leader's game tells its followers where it went (FollowTravelDelta). Without the box,
///     the leader leaving the map stops the follow as before
/// </summary>
[HarmonyPatch]
internal static class PlayerFollow
{
    // a leader tells this many followers at most
    private const int MaxFollowers = 8;

    // no word of where the leader went, or a leader not found where it went, for this long: lost
    private const float LostSeconds = 30f;

    // a map is a few hundred tiles wide at most: a tile past this is no tile
    private const int MaxTile = 2048;

    // "X follows you" is said once a minute per player at most
    private const float ToldFollowedSeconds = 60f;

    // a walk to the leader's way out gives up after this long, the move is made from where this player stands
    private const float ExitWalkSeconds = 30f;

    // a move to the leader's zone that did not happen is tried again after this long, a few times
    private const float MoveRetrySeconds = 8f;
    private const int MaxMoves = 3;

    private static int _leaderUid;
    private static string _leaderName = "";

    // the session link the follow was started on (a guest's Game is replaced when it comes back to the host's map:
    // the follow goes on in the new one)
    private static object? _link;
    private static int _zoneUid;
    private static int _hp;

    // the walk Follow started (a click of the player's own is not cancelled), and walks that got nowhere in a row
    private static AI_Goto? _walk;
    private static int _stuck;
    private static int _lastDist;

    // the click that chose Follow in the menu is not a "key to stop"
    private static int _armedFrame;

    // across maps: the leader's last tile on this map, its last tile on the world map, where it said it is
    private static Point? _exit;
    private static int _exitZone;
    private static Point? _regionAt;
    private static FollowTravelDelta? _where;
    private static float _offSince;
    private static float _seenSince;
    private static float _movedAt;
    private static int _moves;
    private static bool _toldTravel;

    // leader side: who follows this player (character uid -> name), on which session link
    private static readonly Dictionary<int, string> Followers = [];
    private static object? _followersLink;
    private static (int Zone, int X, int Z) _lastSent;
    private static readonly Dictionary<int, float> ToldFollowed = [];

    internal static bool Enabled => NetSession.Instance.Rules.AllowFollow;

    /// <summary>
    ///     The host's box "Follow goes across maps", which needs Follow and travel alone
    /// </summary>
    internal static bool Travels => NetSession.Instance.Rules is { AllowFollow: true, FollowTravel: true, AllowIndependentTravel: true };

    /// <summary>
    ///     For the test bridge: who the local character follows, its uid or 0
    /// </summary>
    internal static int LeaderUid => _leaderUid;

    /// <summary>
    ///     For the test bridge: how many players follow the local character across maps
    /// </summary>
    internal static int FollowerCount => Followers.Count;

    /// <summary>
    ///     This player walks the world map after the player it follows: the leader pays the trip's hours, this
    ///     player's steps only its own turns (issue 75)
    /// </summary>
    internal static bool CarriesTrip => _leaderUid != 0 && Travels && EClass._zone is { IsRegion: true };

    internal static void Start(Chara leader)
    {
        _leaderUid = leader.uid;
        _leaderName = leader.Name;
        _link = NetSession.Instance.Transport;
        _zoneUid = EClass._zone.uid;
        _hp = EClass.pc.hp;
        _walk = null;
        _stuck = 0;
        _exit = leader.pos.Copy();
        _exitZone = _zoneUid;
        _regionAt = null;
        _where = null;
        _offSince = 0f;
        _seenSince = 0f;
        _moves = 0;
        _toldTravel = false;
        _armedFrame = Time.frameCount + 2;
        Msg.Say("emp_follow_start".lang(leader.Name));
        if (Travels) {
            Send(new FollowTravelDelta { Kind = FollowTravelDelta.Start, FromUid = EClass.pc.uid, ToUid = leader.uid });
        }
    }

    internal static void Stop(string key = "emp_follow_stop")
    {
        if (_leaderUid == 0) {
            return;
        }

        var (uid, name) = (_leaderUid, _leaderName);
        _leaderUid = 0;
        if (_walk is not null && EClass.pc.ai == _walk) {
            EClass.pc.ai.Cancel();
        }

        _walk = null;
        _where = null;
        if (Travels) {
            Send(new FollowTravelDelta { Kind = FollowTravelDelta.Stop, FromUid = EClass.pc.uid, ToUid = uid });
        }

        Msg.Say(key.lang(name));
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(AM_Adv), nameof(AM_Adv._OnUpdateInput))]
    internal static void OnUpdate()
    {
        if (_leaderUid == 0) {
            return;
        }

        // (a follow from a session since left: nothing to say about it)
        if (NetSession.Instance.Transport is not { } link || !ReferenceEquals(link, _link)) {
            _leaderUid = 0;
            _where = null;
            return;
        }

        var pc = EClass.pc;
        var now = Time.realtimeSinceStartup;
        // (a guest alone on a map of its own has no Connection, only its link to the host: with the box on that is
        // where a follow goes; without it, the follow of before stops there)
        if (!Enabled || (NetSession.Instance.Connection is null && !Travels) || pc.isDead) {
            Stop();
            return;
        }

        // this player arrived on another map: with the box on, by following
        if (EClass._zone.uid != _zoneUid) {
            if (!Travels) {
                Stop("emp_follow_left");
                return;
            }

            _zoneUid = EClass._zone.uid;
            _walk = null;
            _stuck = 0;
            _moves = 0;
            _offSince = now;
            _hp = pc.hp;
            _armedFrame = Time.frameCount + 2;
        }

        // (a pad's stick moves by the axis, no key: walking on one's own stops it too)
        if (Time.frameCount > _armedFrame && (Input.anyKeyDown || EInput.leftMouse.down || EInput.rightMouse.down || EInput.axis != Vector2.zero)) {
            Stop();
            return;
        }

        // (the monsters roaming the world map are encounters, the leader's: no danger for one walking after it there,
        // bench 2026-11-04 "Danger!" at the first one in sight)
        if (pc.hp < _hp || EClass.ui.layers.Count > 0 || (!EClass._zone.IsRegion &&
            EClass._map.charas.Any(c => c.IsHostile(pc) && !c.isDead && c.Dist(pc) < 10 && pc.CanSee(c)))) {
            Stop("emp_follow_danger");
            return;
        }

        _hp = Mathf.Max(_hp, pc.hp);

        var leader = EClass._zone.IsRegion ? null : EClass._map.charas.Find(c => c.uid == _leaderUid);
        if (leader is not null && leader.currentZone == EClass._zone) {
            FollowHere(pc, leader, now);
            return;
        }

        if (!Travels || !Connected(_leaderUid)) {
            Stop("emp_follow_left");
            return;
        }

        if (_offSince == 0f) {
            _offSince = now;
        }

        FollowAway(pc, now);
    }

    /// <summary>
    ///     The leader is on this map: walk after it
    /// </summary>
    private static void FollowHere(Chara pc, Chara leader, float now)
    {
        if (leader.isDead) {
            Stop();
            return;
        }

        _exit = leader.pos.Copy();
        _exitZone = _zoneUid;
        _offSince = 0f;
        _moves = 0;
        if (_seenSince == 0f) {
            _seenSince = now;
        }

        // a word of a zone it left since (it came back): forgotten once it has been here a moment (the word of where
        // it goes may come before its character leaves this map)
        if (_where is { } where && where.ZoneUid != _zoneUid && now - _seenSince > 2f) {
            _where = null;
        }

        if (!pc.CanSee(leader) && pc.Dist(leader) > 12) {
            Stop("emp_follow_lost");
        } else if (pc.ai is NoGoal or GoalManualMove && pc.Dist(leader) > 2) {
            // a walk that ended no closer (no path: water, a wall) counts; a few in a row and the leader is out of reach
            var dist = pc.Dist(leader);
            _stuck = _walk is not null && dist >= _lastDist ? _stuck + 1 : 0;
            _lastDist = dist;
            if (_stuck >= 5) {
                Stop("emp_follow_lost");
                return;
            }

            // (the game's own click on a character: one step per turn, the path planned again each step)
            pc.SetAIImmediate(_walk = new AI_Goto(leader, 1));
        }
    }

    /// <summary>
    ///     The leader is elsewhere: on the world map walk to its tile; otherwise take its way out of this map
    /// </summary>
    private static void FollowAway(Chara pc, float now)
    {
        _seenSince = 0f;
        var here = EClass._zone;
        var where = _where is { } w && (w.ZoneUid != here.uid || here.IsRegion) ? w : null;

        // both on the world map, each on its own copy: walk where it is (its steps pay the hours)
        if (where is not null && here.IsRegion && where.ZoneUid == here.uid) {
            _offSince = now;
            if (!_toldTravel) {
                _toldTravel = true;
                Msg.Say("emp_follow_travel".lang(_leaderName));
            }

            var to = new Point(where.X, where.Z);
            if (!WalkTo(pc, to, 0) && pc.Dist(to) > 1) {
                Stop("emp_follow_lost");
            }

            return;
        }

        var exit = here.IsRegion ? _regionAt : _exitZone == here.uid ? _exit : null;

        // no word yet of where it went (or it is said to be here and is not): towards where it was seen last
        if (where is null) {
            if (now - _offSince > LostSeconds) {
                Stop("emp_follow_lost");
            } else if (exit is not null) {
                WalkTo(pc, exit, 1);
            }

            return;
        }

        // a move of ours waits for the host's answer (a lease, a map handed back, a big map loading): not a try
        if (TravelPending()) {
            _offSince = now;
            _movedAt = now;
            return;
        }

        // its way out: to the zone it went to when this game knows it, from next to its last tile; else the stairs on
        // that tile, taken by stepping onto them as the game does
        var dest = EClass.game.spatials.Find(where.ZoneUid) as Zone;
        var stairs = dest is not null || exit is null || here.IsRegion ? null : exit.FindThing<TraitNewZone>();
        if (exit is not null && now - _offSince < ExitWalkSeconds && WalkTo(pc, exit, stairs is null ? 1 : 0)) {
            return;
        }

        if (now - _movedAt < MoveRetrySeconds) {
            return;
        }

        if (++_moves > MaxMoves) {
            Stop("emp_follow_lost");
            return;
        }

        _movedAt = now;
        pc.ai?.Cancel();
        if (dest is null && stairs is not null && pc.pos.Equals(stairs.owner.pos)) {
            stairs.MoveZone();
        } else if (dest is not null) {
            EmpLog.Information("Follow: going after {Leader} to {ZoneFullName}", _leaderName, dest.ZoneFullName);
            pc.MoveZone(dest, new ZoneTransition {
                state = SafeState(where.State),
                x = where.EnterX,
                z = where.EnterZ,
            });
        } else {
            // a map this game does not know (a wilderness field the leader's game made): not followed
            EmpLog.Information("Follow: {Leader} went to zone {ZoneUid}, unknown here", _leaderName, where.ZoneUid);
            Stop("emp_follow_lost");
        }
    }

    /// <summary>
    ///     A walk to that point with the game's click-to-move: true while it goes on, false once there or out of reach
    /// </summary>
    private static bool WalkTo(Chara pc, Point to, int dist)
    {
        var now = pc.Dist(to);
        if (now <= dist) {
            return false;
        }

        if (_walk is not null && pc.ai == _walk && _walk.dest.Equals(to)) {
            return true;
        }

        if (pc.ai is not (NoGoal or GoalManualMove) && pc.ai != _walk) {
            return true;
        }

        _stuck = _walk is not null && pc.ai != _walk && now >= _lastDist ? _stuck + 1 : 0;
        _lastDist = now;
        if (_stuck >= 5) {
            return false;
        }

        pc.SetAIImmediate(_walk = new AI_Goto(to.Copy(), dist));
        return true;
    }

    private static bool TravelPending()
    {
        return NetSession.Instance.Transport switch {
            ElinNetHost host => host.IsTravelPending,
            ElinNetClient client => client.IsTravelPending,
            _ => false,
        };
    }

    /// <summary>
    ///     A leader's word of where it is, as far as it can be a place: a known state, tiles on a map
    /// </summary>
    private static bool Sane(FollowTravelDelta delta)
    {
        return delta.X is >= 0 and < MaxTile && delta.Z is >= 0 and < MaxTile && delta.EnterX is >= 0 and < MaxTile &&
               delta.EnterZ is >= 0 and < MaxTile && System.Enum.IsDefined(typeof(ZoneTransition.EnterState), delta.State);
    }

    // a teleport, a moongate or a fall of the leader is made as an ordinary arrival (the follower's game picks its spot)
    private static ZoneTransition.EnterState SafeState(int state)
    {
        var known = (ZoneTransition.EnterState)state;
        return known is ZoneTransition.EnterState.Teleport or ZoneTransition.EnterState.Moongate or ZoneTransition.EnterState.Fall
            ? ZoneTransition.EnterState.Auto
            : known;
    }

    /// <summary>
    ///     A connected player, on this map or another one
    /// </summary>
    private static bool Connected(int uid)
    {
        return NetSession.Instance.Transport switch {
            ElinNetHost host => host.PeerIds.Any(id => host.PlayerChara(id)?.uid == uid),
            ElinNetClient => NetSession.Instance.ServerRoster.Count == 0 || NetSession.Instance.ServerRoster.Exists(p => p.CharaUid == uid) ||
                             NetSession.Instance.CurrentPlayers.Exists(p => p.CharaUid == uid),
            _ => false,
        };
    }

    private static void Send(FollowTravelDelta delta)
    {
        switch (NetSession.Instance.Transport) {
            case ElinNetHost host:
                Relay(host, delta);
                break;
            case ElinNetClient up:
                // (straight to the host, also from a map of one's own)
                up.SendWhileAway(delta);
                break;
        }
    }

    internal static void Relay(ElinNetHost host, FollowTravelDelta delta)
    {
        // (a player away from the host's map included: it is still a peer of the host)
        var peers = host.PeerIds.Where(id => host.PlayerChara(id)?.uid == delta.ToUid).ToList();
        if (peers.Count == 0 || !host.SendDeltaTo(peers[0], delta)) {
            EmpLog.Debug("Follow message for {Uid}: that player is not connected", delta.ToUid);
        }
    }

    internal static void Receive(FollowTravelDelta delta)
    {
        if (!Travels) {
            return;
        }

        LeaderLink();
        switch (delta.Kind) {
            case FollowTravelDelta.Start:
                // only a player standing on this map, as the menu needs (no one far away learns where this player goes)
                var follower = EClass._map?.charas.Find(c => c.uid == delta.FromUid);
                if (follower is null || PlayerSocial.IsIgnoredUid(delta.FromUid) || Followers.ContainsKey(delta.FromUid) ||
                    Followers.Count >= MaxFollowers) {
                    return;
                }

                Followers[delta.FromUid] = follower.Name;
                _lastSent = default;
                var now = Time.realtimeSinceStartup;
                if (!ToldFollowed.TryGetValue(delta.FromUid, out var told) || now - told > ToldFollowedSeconds) {
                    ToldFollowed[delta.FromUid] = now;
                    Msg.Say("emp_follow_by".lang(follower.Name));
                }

                return;
            case FollowTravelDelta.Stop:
                Followers.Remove(delta.FromUid);
                return;
            case FollowTravelDelta.LeaderGone when delta.FromUid == _leaderUid:
                Stop("emp_follow_left");
                return;
            case FollowTravelDelta.Where when delta.FromUid == _leaderUid && Sane(delta):
                _where = delta;
                if (EClass.game?.spatials.Find(delta.ZoneUid) is { IsRegion: true }) {
                    _regionAt = new Point(delta.X, delta.Z);
                }

                return;
        }
    }

    // the followers belong to the session they were told in
    private static void LeaderLink()
    {
        if (!ReferenceEquals(_followersLink, NetSession.Instance.Transport)) {
            Followers.Clear();
            _followersLink = NetSession.Instance.Transport;
        }
    }

    /// <summary>
    ///     Leader side: where this player is now, to each of its followers
    /// </summary>
    private static void TellWhere()
    {
        LeaderLink();
        if (Followers.Count == 0 || EClass.pc is not { } pc || pc.currentZone is not { } zone || pc.pos is null) {
            return;
        }

        if (!Travels) {
            Followers.Clear();
            return;
        }

        var at = (zone.uid, pc.pos.x, pc.pos.z);
        if (at == _lastSent) {
            return;
        }

        _lastSent = at;
        var transition = EClass.player.lastTransition;
        foreach (var uid in Followers.Keys.ToList()) {
            if (!Connected(uid)) {
                Followers.Remove(uid);
                continue;
            }

            Send(new FollowTravelDelta {
                Kind = FollowTravelDelta.Where,
                FromUid = pc.uid,
                ToUid = uid,
                ZoneUid = zone.uid,
                X = pc.pos.x,
                Z = pc.pos.z,
                State = (int)(transition?.state ?? ZoneTransition.EnterState.Auto),
                EnterX = transition?.x ?? 0,
                EnterZ = transition?.z ?? 0,
            });
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Zone), nameof(Zone.Activate))]
    internal static void OnZoneActivated(Zone __instance)
    {
        if (Followers.Count > 0 && EClass.pc?.currentZone == __instance) {
            TellWhere();
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Chara), nameof(Chara._Move))]
    internal static void OnMoved(Chara __instance)
    {
        if (Followers.Count > 0 && __instance.IsPC && EClass._zone is { IsRegion: true }) {
            TellWhere();
        }
    }

    /// <summary>
    ///     A random encounter on the world map is the leader's, as for a party member: a follower walking after it does
    ///     not meet its own (it ended up alone in a field of its own, bench 2026-11-04)
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(Player), nameof(Player.EnterLocalZone), typeof(Point), typeof(ZoneTransition), typeof(bool), typeof(Chara))]
    internal static bool OnEnterLocalZone(bool encounter)
    {
        if (!encounter || !CarriesTrip) {
            return true;
        }

        EmpLog.Debug("Follow: a random encounter of the follower skipped");
        return false;
    }

    /// <summary>
    ///     A dead leader goes nowhere its followers should go (its home, a bartender)
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(Chara), nameof(Chara.Die))]
    internal static void OnDie(Chara __instance)
    {
        if (!__instance.IsPC || !__instance.isDead || Followers.Count == 0) {
            return;
        }

        LeaderLink();
        foreach (var uid in Followers.Keys.ToList()) {
            Send(new FollowTravelDelta { Kind = FollowTravelDelta.LeaderGone, FromUid = __instance.uid, ToUid = uid });
        }

        Followers.Clear();
    }
}
