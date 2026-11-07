using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using ElinTogether.Helper;
using ElinTogether.Models;
using ElinTogether.Net.Steam;

namespace ElinTogether.Net;

/// <summary>
///     Host box "Large crowds" (PLAN_100_players stage 1, item 2): inside the host map each player is sent the
///     characters near its own at the snapshot's rate and their steps and acts as they come, the farther ones once a
///     second in the snapshot only. Its own character, its companions, its team and whoever it trades, duels or fights
///     with are always near. The full snapshot (every 4 s) still names everyone: a character out of range is never
///     taken off a guest's map by WorldStateSnapshot.RemoveLeftOverCharas, and its first partial entry back in range
///     puts it right.
///     At 100 players (perf 2026-10-29) what each player's part is made of is worked out once per tick for the whole
///     map, each entry is serialized once and the messages assembled from those bytes (ListSplice, checked against the
///     serializer's own), players sent the same entries share one message, and the characters near a player are found
///     by blocks of tiles instead of measuring every character for every player. Same entries, same order, same bytes
/// </summary>
internal partial class ElinNetHost
{
    internal const int CrowdNearTiles = 24;

    // the far ones in every 5th partial snapshot: once a second at 5 Hz
    private const int CrowdFarEvery = 5;

    // a block of tiles is one wider than the range: whatever is within it of a tile is in that tile's block or the
    // 8 around it (Fov.Distance truncates the root: 24 means dx² + dz² < 625)
    private const int CrowdBlock = CrowdNearTiles + 1;

    // player -> character uid -> the key of the entry last sent to it (WorldStateSnapshot.ChangeTracker.Key)
    private readonly Dictionary<int, Dictionary<int, int>> _crowdSent = [];

    // player -> who is always near for it, from the last snapshot tick (the delta flush reads it at 50 Hz)
    private readonly Dictionary<int, CrowdView> _crowdViews = [];

    private int _crowdZoneUid;

    private static readonly NetMetric CrowdSnapshotBuilt = NetMetrics.Named("crowd", "snapshot_built");
    private static readonly NetMetric CrowdSnapshotShared = NetMetrics.Named("crowd", "snapshot_shared");
    private static readonly NetMetric CrowdDeltaBuilt = NetMetrics.Named("crowd", "delta_built");
    private static readonly NetMetric CrowdDeltaShared = NetMetrics.Named("crowd", "delta_shared");

    // the entries of this tick: keys, whether a player's, slot by uid, serialized bytes, blocks of tiles
    private readonly Dictionary<long, List<int>> _crowdGrid = [];
    private readonly Stack<List<int>> _crowdGridPool = [];
    private readonly Dictionary<int, int> _entrySlot = [];
    private int[] _entryKey = [];
    private int[] _entryStateKey = [];
    private bool[] _entryPlayer = [];
    private int[] _entryMark = [];
    private byte[]?[] _entryParts = [];
    private int _entryStamp;

    private readonly List<int> _crowdCandidates = [];
    private readonly List<(int Index, int Key, int Distance)> _crowdChanged = [];
    private readonly List<int> _crowdKept = [];
    private readonly List<byte[]> _crowdParts = [];
    private readonly SameListBytes _sameSnapshots = new();
    private readonly ListSplice.Guard _snapshotGuard = new("crowd snapshot");

    // the deltas of one flush: what decides who gets them, serialized bytes
    private DeltaInterest[] _deltaInterest = [];
    private byte[]?[] _deltaParts = [];
    private readonly SameListBytes _sameDeltas = new();
    private readonly ListSplice.Guard _deltaGuard = new("crowd delta");
    private ListSplice? _deltaSplice;

    // views by the uid of their player's character and by team, for RefreshCrowdViews
    private readonly Dictionary<int, CrowdView> _viewsByUid = [];
    private readonly Dictionary<int, CrowdView> _viewsByTeam = [];

    private sealed class CrowdView
    {
        internal Chara? Self;
        internal readonly HashSet<int> Always = [];

        // the next view of the same character (none in a sound game) and of the same team, RefreshCrowdViews
        internal CrowdView? NextSame;
        internal CrowdView? NextTeam;

        internal bool Near(int uid, int x, int z)
        {
            return Self is null || Always.Contains(uid) || Self.pos.Distance(x, z) <= CrowdNearTiles;
        }
    }

    private struct DeltaInterest
    {
        // 0 everyone, 1 a step, 2 a plain fight or wait that a far player skips
        internal byte Kind;
        internal int Uid;
        internal int ActorUid;
        internal int X;
        internal int Z;
        internal bool HasTarget;
        internal int Target;
        internal bool HasAt;
        internal int AtX;
        internal int AtZ;
    }

    private static bool Crowds => WorldStateSnapshot.ChangeTracker.Crowds;

    /// <summary>
    ///     The snapshot of this tick, one per player on the host map. <paramref name="all" /> has every character
    /// </summary>
    private void SendCrowdSnapshot(WorldStateSnapshot all)
    {
        var tick = Session.Tick;
        var full = all.ZoneUid != _crowdZoneUid || tick % WorldStateSnapshot.ChangeTracker.CrowdFullEvery == 0;
        _crowdZoneUid = all.ZoneUid;
        var farSlot = tick % CrowdFarEvery == 0;

        var charas = new Dictionary<int, Chara>();
        foreach (var chara in _map.charas) {
            charas[chara.uid] = chara;
        }

        RefreshCrowdViews(charas);
        PrepareEntries(all.CharaSnapshots, charas);
        _sameSnapshots.Clear();

        var entries = all.CharaSnapshots;
        byte[]? fullBytes = null;
        byte[]? lean = null;
        ListSplice? splice = null;
        foreach (var peer in Socket.Peers) {
            if (!IsPlaying(peer)) {
                // still in its handshake, its game would drop it (IsPlaying); once joined it starts from a full one
                _crowdSent.Remove(peer.Id);
                continue;
            }

            if (IsOffHostMap(peer)) {
                // back on the map, it starts from a full one
                _crowdSent.Remove(peer.Id);
                peer.Send(lean ??= Socket.Serializer.Serialize(all.Lean()));
                continue;
            }

            var isNew = !_crowdSent.TryGetValue(peer.Id, out var sent);
            if (full || isNew) {
                sent ??= _crowdSent[peer.Id] = [];
                sent.Clear();
                var view = _crowdViews.GetValueOrDefault(peer.Id);
                for (var i = 0; i < entries.Length; i++) {
                    sent[entries[i].Owner.Uid] = KeyAt(i, entries[i], view);
                }

                peer.Send(fullBytes ??= Socket.Serializer.Serialize(all));
                continue;
            }

            PartialFor(peer.Id, entries, sent!, farSlot);
            peer.Send(PartialBytes(all, ref splice));
        }
    }

    /// <summary>
    ///     Once per tick for the whole map: each entry's keys, whether it is a player's, its slot, its block
    /// </summary>
    private void PrepareEntries(ImmutableArray<CharaStateSnapshot> entries, Dictionary<int, Chara> charas)
    {
        var n = entries.Length;
        if (_entryKey.Length < n) {
            var size = System.Math.Max(n, _entryKey.Length * 2);
            _entryKey = new int[size];
            _entryStateKey = new int[size];
            _entryPlayer = new bool[size];
            _entryMark = new int[size];
            _entryParts = new byte[size][];
        }

        foreach (var list in _crowdGrid.Values) {
            list.Clear();
            _crowdGridPool.Push(list);
        }

        _crowdGrid.Clear();
        _entrySlot.Clear();
        _entryStamp = 0;
        for (var i = 0; i < n; i++) {
            var entry = entries[i];
            var uid = entry.Owner.Uid;
            var player = charas.TryGetValue(uid, out var chara) && chara.IsPlayer;
            _entrySlot[uid] = i;
            _entryPlayer[i] = player;
            _entryKey[i] = WorldStateSnapshot.ChangeTracker.Key(entry);
            _entryStateKey[i] = player ? WorldStateSnapshot.ChangeTracker.StateKey(entry) : 0;
            _entryMark[i] = 0;
            _entryParts[i] = null;

            var block = BlockKey(BlockCoord(entry.Pos.X), BlockCoord(entry.Pos.Z));
            if (!_crowdGrid.TryGetValue(block, out var inBlock)) {
                inBlock = _crowdGrid[block] = _crowdGridPool.Count > 0 ? _crowdGridPool.Pop() : [];
            }

            inBlock.Add(i);
        }
    }

    private static int BlockCoord(int tile)
    {
        return tile >= 0 ? tile / CrowdBlock : -1 - (-1 - tile) / CrowdBlock;
    }

    private static long BlockKey(int bx, int bz)
    {
        return ((long)bx << 32) | (uint)bz;
    }

    /// <summary>
    ///     A player near this one moves on that player's game through its CharaMoveDelta: its tile is left out of the
    ///     key (item 1). A far one gets no step: its tile is what tells where it went
    /// </summary>
    private int KeyAt(int i, CharaStateSnapshot entry, CrowdView? view)
    {
        return _entryPlayer[i] && (view?.Near(entry.Owner.Uid, entry.Pos.X, entry.Pos.Z) ?? true)
            ? _entryStateKey[i]
            : _entryKey[i];
    }

    /// <summary>
    ///     The entries of this player's partial snapshot, into <see cref="_crowdKept" /> (indices into this tick's)
    /// </summary>
    private void PartialFor(int peerId, ImmutableArray<CharaStateSnapshot> entries, Dictionary<int, int> sent, bool farSlot)
    {
        var view = _crowdViews.GetValueOrDefault(peerId);
        var candidates = _crowdCandidates;
        candidates.Clear();
        if (farSlot || view?.Self is not { } self) {
            for (var i = 0; i < entries.Length; i++) {
                candidates.Add(i);
            }
        } else {
            // the only ones that can be near: the blocks around it and those always near, in the snapshot's order
            // as when every entry was looked at
            var stamp = ++_entryStamp;
            var bx = BlockCoord(self.pos.x);
            var bz = BlockCoord(self.pos.z);
            for (var dx = -1; dx <= 1; dx++) {
                for (var dz = -1; dz <= 1; dz++) {
                    if (!_crowdGrid.TryGetValue(BlockKey(bx + dx, bz + dz), out var inBlock)) {
                        continue;
                    }

                    foreach (var i in inBlock) {
                        _entryMark[i] = stamp;
                        candidates.Add(i);
                    }
                }
            }

            foreach (var uid in view.Always) {
                if (_entrySlot.TryGetValue(uid, out var i) && _entryMark[i] != stamp) {
                    _entryMark[i] = stamp;
                    candidates.Add(i);
                }
            }

            candidates.Sort();
        }

        var changed = _crowdChanged;
        changed.Clear();
        foreach (var i in candidates) {
            var entry = entries[i];
            var uid = entry.Owner.Uid;
            var near = view?.Near(uid, entry.Pos.X, entry.Pos.Z) ?? true;
            if (!farSlot && !near) {
                // not marked as sent: it goes in the next far slot if it still differs
                continue;
            }

            var key = _entryPlayer[i] && near ? _entryStateKey[i] : _entryKey[i];
            if (sent.TryGetValue(uid, out var was) && was == key) {
                continue;
            }

            var distance = view?.Self is not { } me || view.Always.Contains(uid) ? 0 : me.pos.Distance(entry.Pos.X, entry.Pos.Z);
            changed.Add((i, key, distance));
        }

        // the budget per player and tick (item 3): the nearest first, the others stay unsent and differ next tick
        if (changed.Count > CrowdEntryBudget) {
            changed.Sort((a, b) => a.Distance.CompareTo(b.Distance));
            changed.RemoveRange(CrowdEntryBudget, changed.Count - CrowdEntryBudget);
        }

        _crowdKept.Clear();
        foreach (var (i, key, _) in changed) {
            sent[entries[i].Owner.Uid] = key;
            _crowdKept.Add(i);
        }
    }

    /// <summary>
    ///     The partial snapshot of <see cref="_crowdKept" />: one message for every player sent the same entries
    /// </summary>
    private byte[] PartialBytes(WorldStateSnapshot all, ref ListSplice? splice)
    {
        if (_sameSnapshots.Find(_crowdKept, out var hash) is { } known) {
            CrowdSnapshotShared.Add(known.Length);
            return known;
        }

        var entries = all.CharaSnapshots;
        byte[]? bytes = null;
        if (!_snapshotGuard.Broken) {
            try {
                splice ??= ListSplice.Of(Socket.Serializer.Serialize(all.With(ImmutableArray<CharaStateSnapshot>.Empty)), 2);
                _crowdParts.Clear();
                foreach (var i in _crowdKept) {
                    _crowdParts.Add(_entryParts[i] ??= SteamNetSerializer.SerializePart(entries[i]));
                }

                bytes = splice.Build(_crowdParts);
                if (_snapshotGuard.WantsCheck) {
                    var reference = Socket.Serializer.Serialize(all.With(Picked(entries, _crowdKept)));
                    _snapshotGuard.Check(bytes, reference);
                    if (_snapshotGuard.Broken) {
                        bytes = reference;
                    }
                }
            } catch (System.Exception ex) {
                _snapshotGuard.Fail(ex);
                bytes = null;
            }
        }

        bytes ??= Socket.Serializer.Serialize(all.With(Picked(entries, _crowdKept)));
        _sameSnapshots.Add(hash, _crowdKept, bytes);
        CrowdSnapshotBuilt.Add(bytes.Length);
        return bytes;
    }

    private static ImmutableArray<CharaStateSnapshot> Picked(ImmutableArray<CharaStateSnapshot> entries, List<int> indices)
    {
        var kept = ImmutableArray.CreateBuilder<CharaStateSnapshot>(indices.Count);
        foreach (var i in indices) {
            kept.Add(entries[i]);
        }

        return kept.MoveToImmutable();
    }

    // entries of a partial snapshot per player and tick at most: a crowd that all moves at once costs a player that
    // many entries a tick (about 1.5 KB), the farthest wait for the next one
    private const int CrowdEntryBudget = 48;

    // player -> each line of the player list it was last sent (Index -> key), the map sums it was sent, when it got
    // anything and when it got a whole list
    private readonly Dictionary<int, PlayersSent> _playersSent = [];

    private sealed class PlayersSent
    {
        internal readonly Dictionary<int, int> Lines = [];
        internal int Sums;
        internal float At;
        internal float WholeAt;
    }

    // a whole list now and then: a line lost on the way, or a game that missed one, is put right
    private const float PlayersWholeSeconds = 30f;

    // the player list again even unchanged after that long: the pings it shows, and NetDesync compares on each list
    // (3 in a row: a gap is told within 18 s instead of 6)
    private const float PlayersResendSeconds = 6f;

    /// <summary>
    ///     The player list (every 2 s) to those on the host map whose last one differs, or is older than
    ///     <see cref="PlayersResendSeconds" />: with 100 players it was a third of all that was sent while idle
    /// </summary>
    private void SendPlayersOnChange(SessionPlayersSnapshot players)
    {
        var now = UnityEngine.Time.realtimeSinceStartup;
        var lines = new Dictionary<int, int>(players.Current.Length);
        foreach (var s in players.Current) {
            lines[s.Index] = LineKey(s);
        }

        var sums = SumsKey(players.Sums);
        byte[]? whole = null;
        foreach (var peer in Socket.Peers) {
            // (still in its handshake: its game would drop it, and once joined it is sent the list at once)
            if (!IsPlaying(peer) || IsOffHostMap(peer)) {
                _playersSent.Remove(peer.Id);
                continue;
            }

            if (!_playersSent.TryGetValue(peer.Id, out var sent) || now - sent.WholeAt >= PlayersWholeSeconds) {
                sent ??= _playersSent[peer.Id] = new();
                sent.Lines.Clear();
                foreach (var (index, key) in lines) {
                    sent.Lines[index] = key;
                }

                sent.Sums = sums;
                sent.At = sent.WholeAt = now;
                peer.Send(whole ??= Socket.Serializer.Serialize(players));
                continue;
            }

            var changed = players.Current.Where(s => !sent.Lines.TryGetValue(s.Index, out var k) || k != lines[s.Index]).ToArray();
            var gone = sent.Lines.Keys.Where(i => !lines.ContainsKey(i)).ToArray();
            // nothing new: still a word now and then, NetDesync compares the map sums on each list
            if (changed.Length == 0 && gone.Length == 0 && sent.Sums == sums && now - sent.At < PlayersResendSeconds) {
                continue;
            }

            foreach (var s in changed) {
                sent.Lines[s.Index] = lines[s.Index];
            }

            foreach (var i in gone) {
                sent.Lines.Remove(i);
            }

            sent.Sums = sums;
            sent.At = now;
            peer.Send(new SessionPlayersSnapshot {
                Current = [..changed],
                Sums = players.Sums,
                Partial = true,
                Gone = gone.Length > 0 ? gone : null,
            });
        }
    }

    // a line of the list: what the panel shows, the pings to 25 ms and the qualities to 10% (they move every time)
    private static int LineKey(NetPeerState s)
    {
        var hash = new System.HashCode();
        hash.Add(s.Index);
        hash.Add((ulong)s.User);
        hash.Add(s.CharaUid);
        hash.Add(s.Speed);
        hash.Add(s.LastAct);
        hash.Add(s.Turbo);
        hash.Add(s.Talking);
        hash.Add(s.Idle);
        hash.Add(s.AvgPingMsWire / 25);
        hash.Add(s.ConnectionQualityLocalWire / 10);
        hash.Add(s.ConnectionQualityRemoteWire / 10);
        return hash.ToHashCode();
    }

    private static int SumsKey(MapSums? sums)
    {
        if (sums is null) {
            return 0;
        }

        var hash = new System.HashCode();
        hash.Add(sums.ZoneUid);
        hash.Add(sums.Charas);
        hash.Add(sums.CharaMix);
        hash.Add(sums.Things);
        hash.Add(sums.ThingMix);
        hash.Add(sums.Locals);
        hash.Add(sums.LocalMix);
        var bags = 0;
        foreach (var (uid, mix) in sums.Bags ?? []) {
            bags ^= System.HashCode.Combine(uid, mix);
        }

        hash.Add(bags);
        return hash.ToHashCode();
    }

    private void RefreshCrowdViews(Dictionary<int, Chara> charas)
    {
        // once per tick for the whole map: the player behind each character, and each player's team
        var behind = new Dictionary<int, int>();
        foreach (var (uid, chara) in charas) {
            if (PlayerKarma.PlayerBehind(chara) is { } owner && owner.uid != uid) {
                behind[uid] = owner.uid;
            }
        }

        var teams = new Dictionary<int, int>();
        if (NetSession.Instance.Rules.UseTeams) {
            foreach (var player in ActiveRemoteCharas.Values) {
                teams[player.uid] = Teams.TeamOf(player.uid);
            }

            teams[pc.uid] = Teams.TeamOf(pc.uid);
        }

        _viewsByUid.Clear();
        _viewsByTeam.Clear();
        foreach (var peer in Socket.Peers) {
            if (!_crowdViews.TryGetValue(peer.Id, out var view)) {
                view = _crowdViews[peer.Id] = new();
            }

            view.Always.Clear();
            view.NextSame = view.NextTeam = null;
            view.Self = ActiveRemoteCharas.GetValueOrDefault(peer.Id) is { IsInActiveMap: true } self ? self : null;
            if (view.Self is not { } me) {
                continue;
            }

            view.Always.Add(me.uid);
            view.NextSame = _viewsByUid.GetValueOrDefault(me.uid);
            _viewsByUid[me.uid] = view;
            if (teams.GetValueOrDefault(me.uid) is var team and not Teams.Wanderers) {
                view.NextTeam = _viewsByTeam.GetValueOrDefault(team);
                _viewsByTeam[team] = view;
            }
        }

        // each character once: to the view of the player behind it, of that player's team, and of whom it fights
        // (was each character once per player)
        foreach (var (uid, chara) in charas) {
            var of = behind.GetValueOrDefault(uid, uid);
            for (var view = _viewsByUid.GetValueOrDefault(of); view is not null; view = view.NextSame) {
                view.Always.Add(uid);
            }

            if (teams.GetValueOrDefault(of) is var team and not Teams.Wanderers) {
                for (var view = _viewsByTeam.GetValueOrDefault(team); view is not null; view = view.NextTeam) {
                    view.Always.Add(uid);
                }
            }

            if (chara.enemy is { } enemy) {
                for (var view = _viewsByUid.GetValueOrDefault(enemy.uid); view is not null; view = view.NextSame) {
                    if (view.Self == enemy) {
                        view.Always.Add(uid);
                    }
                }
            }
        }

        foreach (var view in _viewsByUid.Values) {
            for (var same = view; same is not null; same = same.NextSame) {
                var me = same.Self!;
                if (me.enemy is { } enemy) {
                    same.Always.Add(enemy.uid);
                }

                if (PlayerDuel.FoeOf(me.uid) is var foe and not 0) {
                    same.Always.Add(foe);
                }

                if (PlayerTrade.PartnerOf(me) is { } partner) {
                    same.Always.Add(partner.uid);
                }
            }
        }

        if (_crowdViews.Count > Socket.Peers.Count) {
            var here = new HashSet<int>(Socket.Peers.Select(p => p.Id));
            var gone = _crowdViews.Keys.Where(id => !here.Contains(id)).ToList();

            foreach (var id in gone) {
                _crowdViews.Remove(id);
                _crowdSent.Remove(id);
                _playersSent.Remove(id);
            }
        }
    }

    /// <summary>
    ///     As SendByMap, with each player on the host map sent only the steps and acts near it
    /// </summary>
    private void SendCrowdDeltas(List<ElinDelta> deltas)
    {
        PrepareDeltas(deltas);
        _sameDeltas.Clear();

        byte[]? all = null;
        byte[]? lean = null;
        var leanBuilt = false;
        foreach (var peer in Socket.Peers) {
            if (!IsPlaying(peer)) {
                continue; // still in its handshake, its game would drop it (IsPlaying)
            }

            if (IsOffHostMap(peer)) {
                if (!leanBuilt) {
                    leanBuilt = true;
                    lean = deltas.Where(ElinNetClient.ReachesAwayPlayer).ToList() is { Count: > 0 } away
                        ? Socket.Serializer.Serialize(new WorldStateDeltaList { DeltaList = away })
                        : null;
                }

                if (lean is not null) {
                    peer.Send(lean);
                }

                continue;
            }

            if (!CrowdDeltas(deltas.Count, peer)) {
                peer.Send(all ??= Socket.Serializer.Serialize(new WorldStateDeltaList { DeltaList = deltas }));
            } else if (_crowdKept.Count > 0) {
                peer.Send(KeptDeltaBytes(deltas));
            }
        }
    }

    /// <summary>
    ///     Once per flush: what decides whether a player is sent each delta
    /// </summary>
    private void PrepareDeltas(List<ElinDelta> deltas)
    {
        var n = deltas.Count;
        if (_deltaInterest.Length < n) {
            var size = System.Math.Max(n, _deltaInterest.Length * 2);
            _deltaInterest = new DeltaInterest[size];
            _deltaParts = new byte[size][];
        }

        for (var i = 0; i < n; i++) {
            _deltaParts[i] = null;
            _deltaInterest[i] = deltas[i] switch {
                CharaMoveDelta move => new() { Kind = 1, Uid = move.Owner.Uid, X = move.Pos.X, Z = move.Pos.Z },
                // only plain fighting and waiting: other acts change the map on replay (fire, webs, a thrown potion)
                // or carry state nothing else sends (riding)
                CharaActPerformDelta { Tool: null } act when SkippableFar(act.ActId) && act.Owner.Find() is Chara actor => new() {
                    Kind = 2,
                    Uid = act.Owner.Uid,
                    ActorUid = actor.uid,
                    X = actor.pos.x,
                    Z = actor.pos.z,
                    HasTarget = act.TargetCard is not null,
                    Target = act.TargetCard?.Uid ?? 0,
                    HasAt = act.Pos is not null,
                    AtX = act.Pos?.X ?? 0,
                    AtZ = act.Pos?.Z ?? 0,
                },
                _ => default,
            };
        }
    }

    /// <summary>
    ///     The steps and acts of this flush that a player on the host map is sent, into <see cref="_crowdKept" />;
    ///     false when all of them. Its own steps come back to it as well without this box (its game drops them,
    ///     CharaMoveDelta.OnApply)
    /// </summary>
    private bool CrowdDeltas(int count, ISteamNetPeer peer)
    {
        if (_crowdViews.GetValueOrDefault(peer.Id) is not { Self: { } me } view) {
            return false;
        }

        _crowdKept.Clear();
        var dropped = false;
        for (var i = 0; i < count; i++) {
            ref var interest = ref _deltaInterest[i];
            var drop = interest.Kind switch {
                1 => interest.Uid == me.uid || !view.Near(interest.Uid, interest.X, interest.Z),
                2 => FarActor(ref interest, view),
                _ => false,
            };

            if (drop) {
                dropped = true;
            } else {
                _crowdKept.Add(i);
            }
        }

        return dropped;
    }

    private static bool SkippableFar(int actId)
    {
        return actId is ABILITY.ActWait or ABILITY.ActMelee or ABILITY.ActRanged or ABILITY.ActKick;
    }

    private static bool FarActor(ref DeltaInterest act, CrowdView view)
    {
        if (view.Always.Contains(act.Uid) || act.HasTarget && view.Always.Contains(act.Target)) {
            return false;
        }

        return !view.Near(act.ActorUid, act.X, act.Z) &&
               (!act.HasAt || !view.Near(0, act.AtX, act.AtZ));
    }

    /// <summary>
    ///     The deltas of <see cref="_crowdKept" />: one message for every player sent the same ones, each delta
    ///     serialized once
    /// </summary>
    private byte[] KeptDeltaBytes(List<ElinDelta> deltas)
    {
        if (_sameDeltas.Find(_crowdKept, out var hash) is { } known) {
            CrowdDeltaShared.Add(known.Length);
            return known;
        }

        byte[]? bytes = null;
        if (!_deltaGuard.Broken) {
            try {
                _deltaSplice ??= ListSplice.Of(Socket.Serializer.Serialize(new WorldStateDeltaList()), 0);
                _crowdParts.Clear();
                foreach (var i in _crowdKept) {
                    _crowdParts.Add(_deltaParts[i] ??= SteamNetSerializer.SerializePart<ElinDelta>(deltas[i]));
                }

                bytes = _deltaSplice.Build(_crowdParts);
                if (_deltaGuard.WantsCheck) {
                    var reference = Socket.Serializer.Serialize(new WorldStateDeltaList { DeltaList = PickedDeltas(deltas) });
                    _deltaGuard.Check(bytes, reference);
                    if (_deltaGuard.Broken) {
                        bytes = reference;
                    }
                }
            } catch (System.Exception ex) {
                _deltaGuard.Fail(ex);
                bytes = null;
            }
        }

        bytes ??= Socket.Serializer.Serialize(new WorldStateDeltaList { DeltaList = PickedDeltas(deltas) });
        _sameDeltas.Add(hash, _crowdKept, bytes);
        CrowdDeltaBuilt.Add(bytes.Length);
        return bytes;
    }

    private List<ElinDelta> PickedDeltas(List<ElinDelta> deltas)
    {
        var kept = new List<ElinDelta>(_crowdKept.Count);
        foreach (var i in _crowdKept) {
            kept.Add(deltas[i]);
        }

        return kept;
    }
}
