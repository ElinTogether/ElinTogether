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

    public static readonly List<CharaStateSnapshot> CachedRemoteSnapshots = [];

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

    [ElinPreLoad]
    private static void ClearSweepStrikes(GameIOContext context)
    {
        _missingLeftOverCharas = [];
    }

    public static WorldStateSnapshot Create()
    {
        CachedRemoteSnapshots.Add(CharaStateSnapshot.CreateSelf());

        var selfState = NetSession.Instance.Self;
        if (selfState != null) {
            selfState.Speed = pc.Stub_get_Speed();
        }

        var snapshots = new Dictionary<int, CharaStateSnapshot>();
        foreach (var chara in _map.charas) {
            if (!chara.IsInActiveMap) {
                continue;
            }

            snapshots[chara.uid] = CharaStateSnapshot.Create(chara);
        }

        // attach remote state to client characters
        foreach (var remoteSnapshot in CachedRemoteSnapshots) {
            if (snapshots.TryGetValue(remoteSnapshot.Owner.Uid, out var snapshot)) {
                snapshot.State = remoteSnapshot.State;
            }
        }

        CachedRemoteSnapshots.Clear();

        return new() {
            ServerTick = NetSession.Instance.Tick,
            GameDate = [..game.world.date.raw],
            CharaSnapshots = [..snapshots.Values],
            GlobalUidNext = game.cards.uidNext,
            SharedSpeed = NetSession.Instance.SharedSpeed,
        };
    }

    public void ApplyReconciliation()
    {
        if (NetSession.Instance.Connection is not ElinNetClient client) {
            return;
        }

        client.Delta.AddLocal(new DynamicDelta {
            Action = _ => {
                // 1
                WorldDateAdvanceDelta.SetClientDate([..GameDate]);

                // 2
                foreach (var snapshot in CharaSnapshots) {
                    snapshot.ApplyReconciliation();
                }

                RemoveLeftOverCharas();

                // 3
                game.cards.uidNext = GlobalUidNext;

                // 4
                if (NetSession.Instance.Rules.UseSharedSpeed &&
                    SharedSpeed > 0f) {
                    NetSession.Instance.SharedSpeed = SharedSpeed;
                }
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