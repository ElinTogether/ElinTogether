using ElinTogether.Helper;
using System;
using System.Collections.Generic;
using System.Linq;
using ElinTogether.Net;
using ElinTogether.Patches;
using MessagePack;

namespace ElinTogether.Models;

[MessagePackObject]
public class ThingRequest : ElinDelta
{
    private static readonly Dictionary<int, (Action<Thing>, Action?, DateTime Asked)> _callbackList = [];
    private static readonly Dictionary<int, (WeakReference<Thing> thing, Card? origin, DateTime since, int peer)> _dangling = [];
    private static readonly TimeSpan _danglingTimeout = TimeSpan.FromSeconds(10);

    private static int _nextId;

    // client replaying local ... simulation?
    public static bool IsReplayingIntent;

    [Key(0)]
    public required int Id { get; init; }

    [Key(1)]
    public required RemoteCard? Thing { get; set; }

    [Key(2)]
    public required int Num { get; init; }

    protected override void OnApply(ElinNetBase net)
    {
        var thing = Thing?.Find() as Thing;
        if (net.IsClient) {
            if (_callbackList.Remove(Id, out var value)) {
                IsReplayingIntent = true;
                try {
                    var (onSuccess, onFail, asked) = value;
                    if (thing is not null && DateTime.Now - asked > _answerTimeout) {
                        EmpLog.Warning("ThingRequest {RequestId} answered after {Seconds:F1}s, not taken: the host puts {Uid} back",
                            Id, (DateTime.Now - asked).TotalSeconds, thing.uid);
                        onFail?.Invoke();
                    } else if (thing is not null && net is ElinNetClient { IsInTransfer: true }) {
                        // answered while we leave the host map: it puts that back in our bag before our character
                        // goes (ReturnDanglingOf), held here it would be in no bag of the next world (issue 48)
                        EmpLog.Warning("ThingRequest {RequestId} answered while leaving, not taken: the host puts {Uid} back",
                            Id, thing.uid);
                        onFail?.Invoke();
                    } else if (thing is not null) {
                        onSuccess(thing);
                    } else {
                        onFail?.Invoke();
                    }
                } finally {
                    IsReplayingIntent = false;
                }
            } else {
                EmpLog.Warning("ThingRequest {RequestId} response has no pending callback, dropped",
                    Id);
            }

            return;
        }

        if (thing is null || thing.parent is null) {
            EmpLog.Warning("Rejecting ThingRequest {RequestId} from peer {PeerIndex}, uid {Uid} unresolved or parentless",
                Id, OriginPeer, Thing?.Uid ?? -1);
            Respond(net, null);
            return;
        }

        // seen in a shop or a chest a moment ago, since then in the bag of another player
        if (net is ElinNetHost host &&
            thing.GetRootCard() is Chara { IsPlayer: true } holder &&
            holder != host.ActiveRemoteCharas.GetValueOrDefault(OriginPeer)) {
            EmpLog.Warning("Rejecting ThingRequest {RequestId} from peer {PeerIndex}, uid {Uid} is held by player {HolderUid}",
                Id, OriginPeer, thing.uid, holder.uid);
            Respond(net, null);
            return;
        }

        // a chest or the ground of another team's land (Teams)
        if (net is ElinNetHost land && land.ActiveRemoteCharas.TryGetValue(OriginPeer, out var taker) && !Teams.CanTake(taker, thing)) {
            EmpLog.Warning("Rejecting ThingRequest {RequestId} from peer {PeerIndex}, uid {Uid} belongs to another team",
                Id, OriginPeer, thing.uid);
            Respond(net, null);
            return;
        }

        // the bank or the delivery box of another team (Teams keep their own): only the sender's own and the world's
        if (net is ElinNetHost boxKeeper && boxKeeper.ActiveRemoteCharas.TryGetValue(OriginPeer, out var owner) && !TeamBoxes.CanTake(owner, thing)) {
            EmpLog.Warning("Rejecting ThingRequest {RequestId} from peer {PeerIndex}, uid {Uid} is in another team's box",
                Id, OriginPeer, thing.uid);
            // (its own team's box, which its rank may not take from: it is told why)
            if (TeamBoxes.OwnerOf(thing.GetRootCard(), out _) is > 0 and var team && team == Teams.TeamOf(owner.uid)) {
                TeamRanks.Denied(owner.uid);
            }

            Respond(net, null);
            return;
        }

        // a limited item of a shop: no more than what is left of it for that player (its own game says no first,
        // when it knows; see LimitedStockPatch)
        var num = Math.Min(Num, thing.Num);
        if (net is ElinNetHost shop && shop.ActiveRemoteCharas.TryGetValue(OriginPeer, out var asker)) {
            num = LimitedStockPatch.Left(thing, asker, num);
            // (a team's box: no more than the taker's rank may still take today)
            num = TeamRanks.ClampWithdraw(asker, thing, num);
        }

        if (num <= 0) {
            Respond(net, null);
            return;
        }

        // a chest of a resident: the guest's game takes the karma but its guards and witnesses are here
        if (net is ElinNetHost thief && thief.ActiveRemoteCharas.TryGetValue(OriginPeer, out var robber)) {
            PlayerKarmaPatch.OnTakeFromResident(thief, robber, thing);
        }

        var origin = thing.parent as Card;
        // the stack may have shrunk since the requester counted it: asking for more than is left would make
        // a copy of what was asked for
        // (before it leaves: a limited item of a shop is laid again whole for the other players)
        var whole = net is ElinNetHost keeper && keeper.ActiveRemoteCharas.TryGetValue(OriginPeer, out var buyer)
            ? LimitedStockPatch.Whole(thing, num, buyer)
            : null;

        var result = thing.Split(num);
        result.parent?.RemoveCard(result);
        // at once: a throw from here on lost the thing, out of its chest and in nobody's hands (reliability audit
        // 2026-10-27, #11). Recorded, it goes back where it was if the asker never takes it
        RecordDangling(result, origin, OriginPeer);

        try {
#if DEBUG
            if (BenchThrowAfterTake) {
                throw new InvalidOperationException("bench: thrown after the thing was taken");
            }
#endif

            if (whole is not null) {
                // as this game's own gesture, so that the stack reaches the players like any change made here
                using var _ = Simulate();
                LimitedStockPatch.Lay(result == thing ? null : thing, whole);
            }

            CardCache.KeepAlive(result);
        } catch (Exception ex) {
            EmpLog.Warning(ex, "ThingRequest {RequestId} from peer {PeerIndex} failed after taking {Uid}, it goes back where it was",
                Id, OriginPeer, result.uid);
            Respond(net, null);
            return;
        }

        Thing = result;
        Respond(net, result);
    }

#if DEBUG
    // the bench makes the step after the take throw, bench/thing_request_check.py
    internal static bool BenchThrowAfterTake;
#endif

    /// <summary>
    ///     How long an answer of the host is waited for. Later, what it gave back is not taken here (a drag or a
    ///     purchase starting long after the click, the player elsewhere by then): the host puts it back where it was
    ///     after 10 s (InvalidateDangling), a little more than this. Never answered, a request was kept for good
    /// </summary>
    private static readonly TimeSpan _answerTimeout = TimeSpan.FromSeconds(8);

    private void Respond(ElinNetBase net, Thing? thing)
    {
        var response = new ThingRequest {
            Id = Id,
            Thing = thing is null ? null : RemoteCard.Create(thing, withData: true),
            Num = Num,
        };

        if (!net.SendDeltaTo(OriginPeer, response)) {
            EmpLog.Warning("ThingRequest {RequestId} response dropped, peer {PeerIndex} is gone",
                Id, OriginPeer);
        }
    }

    public static ThingRequest Create(Thing thing, int num)
    {
        var req = new ThingRequest {
            Id = _nextId++,
            Thing = thing,
            Num = num,
        };

        return req;
    }

    public ThingRequest Send()
    {
        NetSession.Instance.Connection!.Delta.AddRemote(this);
        return this;
    }

    public void Then(Action<Thing> onSuccess, Action? onFail = null)
    {
        // requests never answered (the host threw, the link changed) are not kept for good
        var now = DateTime.Now;
        foreach (var (id, (_, _, asked)) in _callbackList.ToArray()) {
            if (now - asked > TimeSpan.FromMinutes(1)) {
                _callbackList.Remove(id);
            }
        }

        _callbackList[Id] = (onSuccess, onFail, now);
    }

    internal static void Clear()
    {
        _callbackList.Clear();
        _dangling.Clear();
        _nextId = 0;
    }

    private static void RecordDangling(Thing result, Card? origin, int peer)
    {
        _dangling[result.uid] = (new(result), origin, DateTime.Now, peer);
    }

    internal static void InvalidateDangling()
    {
        ReturnDangling(null);
    }

    /// <summary>
    ///     What this player was handed and still holds out of any bag goes back where it was at once, before its
    ///     character leaves this map, is handed to another game or goes away (GitHub issue 48: gold split and held
    ///     while leaving was in no copy of the character). A game of this version puts it back itself first, see
    ///     HeldDrag
    /// </summary>
    internal static void ReturnDanglingOf(int peer)
    {
        ReturnDangling(peer);
    }

    private static void ReturnDangling(int? peer)
    {
        if (_dangling.Count == 0 || NetSession.Instance.Connection is not ElinNetHost) {
            return;
        }

        foreach (var (uid, entry) in _dangling.ToArray()) {
            if (!entry.thing.TryGetTarget(out var dangling) || dangling.isDestroyed || dangling.parent is not null) {
                _dangling.Remove(uid);
                continue;
            }

            if (peer is null ? DateTime.Now - entry.since < _danglingTimeout : entry.peer != peer) {
                continue;
            }

            EmpLog.Warning("ThingRequest dangling of {Uid} was never dango dongo'd, returning to {ParentUid} (peer {PeerIndex} leaving {Leaving})",
                uid, entry.origin?.uid ?? -1, entry.peer, peer is not null);

            using (Simulate()) {
                if (entry.origin is { isDestroyed: false } origin) {
                    origin.AddThing(dangling);
                } else {
                    _zone.AddCard(dangling, pc.pos);
                }
            }

            _dangling.Remove(uid);
        }
    }
}