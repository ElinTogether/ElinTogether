using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using ElinTogether.Helper;
using ElinTogether.Net;
using ElinTogether.Patches;
using MessagePack;

namespace ElinTogether.Models;

[MessagePackObject]
public class WorldStateSnapshot : EClass
{
    private static Dictionary<int, int> _missingLeftOverCharas = [];

    private static readonly Dictionary<int, CharaStateSnapshot> _entries = [];


    [Key(0)]
    public required int ServerTick { get; init; }

    [Key(1)]
    public required ImmutableArray<int> GameDate { get; init; }

    [Key(2)]
    public required ImmutableArray<CharaStateSnapshot> CharaSnapshots { get; init; }

    [Key(3)]
    public required int GlobalUidNext { get; init; }

    [Key(4)]
    public required int SharedSpeed { get; init; }

    /// <summary>
    ///     The zone of the characters whose own <see cref="CharaStateSnapshot.CurrentZoneUid" /> is 0: written once
    ///     here instead of in every entry (efficiency audit 2026-10-27)
    /// </summary>
    [Key(5)]
    public int ZoneUid { get; init; }

    /// <summary>
    ///     Only the characters whose state changed since the last snapshot to everyone; every
    ///     <see cref="ChangeTracker.FullEvery" />th one (<see cref="ChangeTracker.CrowdFullEvery" />th with the host box
    ///     "Large crowds", which also leaves the players' own steps out of the partial ones), and the first on a new
    ///     map, has them all (efficiency audit
    ///     2026-10-27: most characters of a map stand still, and every one went to every player 5 times a second).
    ///     A character missing from a partial one is not gone: only a full one says who left
    /// </summary>
    [Key(6)]
    public bool Partial { get; init; }

    [ElinPreLoad]
    private static void ClearSweepStrikes(GameIOContext context)
    {
        _missingLeftOverCharas = [];
    }

    /// <summary>
    ///     What the snapshots to everyone last said of each character, for the next one to carry only what changed
    /// </summary>
    internal sealed class ChangeTracker
    {
        // 1 s at 5 Hz
        internal const int FullEvery = 5;

        // 4 s at 5 Hz, host box "Large crowds" (PLAN_100_players stage 1): a player's own steps already reach
        // everyone as CharaMoveDelta, the full one is the check that nothing drifted
        internal const int CrowdFullEvery = 20;

        // (the rules, not the file: a zone session keeps the world's)
        internal static bool Crowds => NetSession.Instance.Rules.LargeCrowds;

        private Dictionary<int, int> _sent = [];
        private Dictionary<int, int> _next = [];
        private int _zoneUid;

        /// <summary>
        ///     Whether the entry goes into this snapshot, and remember it for the next one
        /// </summary>
        internal bool Keep(CharaStateSnapshot snapshot, bool full, bool player = false)
        {
            var key = player ? StateKey(snapshot) : Key(snapshot);
            var uid = snapshot.Owner.Uid;
            _next[uid] = key;
            return full || !_sent.TryGetValue(uid, out var sent) || sent != key;
        }

        /// <summary>
        ///     <see cref="Keep(CharaStateSnapshot, bool, bool)" /> read from the character itself, before any entry is
        ///     made: a character that stands still costs no entry (perf batch 2026-11-03: every character of the map
        ///     got one 5 times a second, to be dropped by most partial snapshots)
        /// </summary>
        internal bool Keep(Chara chara, int zoneUid, bool full, bool player = false)
        {
            var zone = chara.currentZone.uid;
            if (zone == zoneUid) {
                zone = 0;
            }

            var pos = chara.pos;
            var hunger = CharaStateSnapshot.HungerOf(chara);
            var key = player
                ? StateKey(zone, chara.hp, chara.isDead, chara.hostility, chara.c_originalHostility, chara.c_uidMaster, chara.c_minionType, hunger)
                : Key(pos.x, pos.z, zone, chara.hp, chara.isDead, chara.hostility, chara.c_originalHostility, chara.c_uidMaster,
                    chara.c_minionType, hunger);
            var uid = chara.uid;
            _next[uid] = key;
            return full || !_sent.TryGetValue(uid, out var sent) || sent != key;
        }

        internal bool Begin(int zoneUid, int tick)
        {
            var full = zoneUid != _zoneUid || tick % (Crowds ? CrowdFullEvery : FullEvery) == 0;
            _zoneUid = zoneUid;
            _next.Clear();
            return full;
        }

        internal void End()
        {
            (_sent, _next) = (_next, _sent);
        }

        internal static int Key(CharaStateSnapshot s)
        {
            return Key(s.Pos.X, s.Pos.Z, s.CurrentZoneUid, s.Hp, s.IsDead, s.Hostility, s.OriginalHostility, s.UidMaster, s.MinionType, s.Hunger);
        }

        private static int Key(int x, int z, int zone, int hp, bool dead, Hostility hostility, Hostility original, int master,
            MinionType minion, int? hunger)
        {
            return HashCode.Combine(HashCode.Combine(x, z, zone, hp, dead), hostility, original, master, minion, hunger);
        }

        /// <summary>
        ///     All but the tile: what a partial one carries of a player whose steps reach the other side anyway
        /// </summary>
        internal static int StateKey(CharaStateSnapshot s)
        {
            return StateKey(s.CurrentZoneUid, s.Hp, s.IsDead, s.Hostility, s.OriginalHostility, s.UidMaster, s.MinionType, s.Hunger);
        }

        private static int StateKey(int zone, int hp, bool dead, Hostility hostility, Hostility original, int master, MinionType minion,
            int? hunger)
        {
            return HashCode.Combine(HashCode.Combine(zone, hp, dead), hostility, original, master, minion, hunger);
        }
    }

    /// <param name="tracker">For a snapshot to everyone: only what changed since the last one, see <see cref="Partial" /></param>
    internal static WorldStateSnapshot Create(ChangeTracker? tracker = null)
    {
        var selfState = NetSession.Instance.Self;
        if (selfState != null) {
            selfState.Speed = pc.Stub_get_Speed();
        }

        var zoneUid = _zone.uid;
        var full = tracker?.Begin(zoneUid, NetSession.Instance.Tick) ?? true;
        // (kept from one snapshot to the next, emptied each time: the host's main thread only)
        var snapshots = _entries;
        snapshots.Clear();
        foreach (var chara in _map.charas) {
            // (a server's stand-in is nobody for the guests: it is not told to them, see ServerAvatar)
            if (!chara.IsInActiveMap || ServerAvatar.Is(chara)) {
                continue;
            }

            if (tracker is not null && !tracker.Keep(chara, zoneUid, full, ChangeTracker.Crowds && chara.IsPlayer)) {
                continue;
            }

            var snapshot = CharaStateSnapshot.Create(chara);
            if (snapshot.CurrentZoneUid == zoneUid) {
                snapshot.CurrentZoneUid = 0;
            }

            snapshots[chara.uid] = snapshot;
        }

        tracker?.End();

        // (the players' own state, LastAct, speed, turbo, is not attached any more: no game reads it here, the host
        // keeps it from each guest's own report, and it was a quarter of every snapshot to everyone, efficiency audit
        // 2026-10-27)

        return new() {
            ServerTick = NetSession.Instance.Tick,
            GameDate = [..game.world.date.raw],
            CharaSnapshots = [..snapshots.Values],
            GlobalUidNext = game.cards.uidNext,
            SharedSpeed = NetSession.Instance.SharedSpeed,
            ZoneUid = zoneUid,
            Partial = !full,
        };
    }

    /// <summary>
    ///     The same tick for a player away from the host map: its game drops the characters of a map it is not on
    /// </summary>
    internal WorldStateSnapshot Lean()
    {
        return new() {
            ServerTick = ServerTick,
            GameDate = GameDate,
            CharaSnapshots = ImmutableArray<CharaStateSnapshot>.Empty,
            GlobalUidNext = GlobalUidNext,
            SharedSpeed = SharedSpeed,
            ZoneUid = ZoneUid,
            Partial = Partial,
        };
    }

    /// <summary>
    ///     The same tick with only these entries, a partial one (host box "Large crowds": what one player is sent)
    /// </summary>
    internal WorldStateSnapshot With(ImmutableArray<CharaStateSnapshot> entries)
    {
        return new() {
            ServerTick = ServerTick,
            GameDate = GameDate,
            CharaSnapshots = entries,
            GlobalUidNext = GlobalUidNext,
            SharedSpeed = SharedSpeed,
            ZoneUid = ZoneUid,
            Partial = true,
        };
    }

    public void ApplyReconciliation()
    {
        if (NetSession.Instance.Connection is not ElinNetClient client) {
            return;
        }

        foreach (var snapshot in CharaSnapshots) {
            if (snapshot.CurrentZoneUid == 0) {
                snapshot.CurrentZoneUid = ZoneUid;
            }
        }

        client.Delta.AddLocal(new DynamicDelta {
            Action = _ => {
                // 1
                WorldDateAdvanceDelta.SetClientDate([..GameDate]);

                // 2
                foreach (var snapshot in CharaSnapshots) {
                    snapshot.ApplyReconciliation();
                }

                // (only a full snapshot names everyone on the map)
                if (!Partial) {
                    RemoveLeftOverCharas();
                }

                // 3
                game.cards.uidNext = GlobalUidNext;

                // 4
                // (always: the host sends 0 when the shared speed is off, a value kept from before stayed; upstream 403f6fb)
                NetSession.Instance.SharedSpeed = SharedSpeed;
            },
        });
    }

    private void RemoveLeftOverCharas()
    {
        var uids = new HashSet<int>(CharaSnapshots.Select(s => s.Owner.Uid));
        if (!uids.Contains(pc.uid)) {
            _missingLeftOverCharas = [];
            return;
        }

        var missing = new Dictionary<int, int>();
        List<Chara>? leftovers = null;
        foreach (var chara in _map.charas) {
            if (chara.IsPC || chara.IsRemotePlayer || PendingUid.IsPending(chara.uid) || uids.Contains(chara.uid)) {
                continue;
            }

            var count = _missingLeftOverCharas.GetValueOrDefault(chara.uid) + 1;
            if (count < 2) {
                missing[chara.uid] = count;
                continue;
            }

            leftovers ??= [];
            leftovers.Add(chara);
        }

        _missingLeftOverCharas = missing;

        if (leftovers is null) {
            return;
        }

        foreach (var chara in leftovers) {
            _zone.RemoveCard(chara);
        }
    }
}