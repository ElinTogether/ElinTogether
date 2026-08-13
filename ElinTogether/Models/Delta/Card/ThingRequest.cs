using System;
using System.Collections.Generic;
using System.Linq;
using ElinTogether.Net;
using MessagePack;

namespace ElinTogether.Models;

[MessagePackObject]
public class ThingRequest : ElinDelta
{
    private static readonly Dictionary<int, (Action<Thing>, Action?)> _callbackList = [];
    private static readonly Dictionary<int, (WeakReference<Thing> thing, Card? origin, DateTime since)> _dangling = [];
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
                    var (onSuccess, onFail) = value;
                    if (thing is not null) {
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

        var origin = thing.parent as Card;
        var result = thing.Split(Num);
        result.parent?.RemoveCard(result);
        CardCache.KeepAlive(result);
        RecordDangling(result, origin);

        Thing = result;
        Respond(net, result);
    }

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
        _callbackList[Id] = (onSuccess, onFail);
    }

    internal static void Clear()
    {
        _callbackList.Clear();
        _dangling.Clear();
        _nextId = 0;
    }

    private static void RecordDangling(Thing result, Card? origin)
    {
        _dangling[result.uid] = (new(result), origin, DateTime.Now);
    }

    internal static void InvalidateDangling()
    {
        if (_dangling.Count == 0 || NetSession.Instance.Connection is not ElinNetHost) {
            return;
        }

        foreach (var (uid, entry) in _dangling.ToArray()) {
            if (!entry.thing.TryGetTarget(out var dangling) || dangling.isDestroyed || dangling.parent is not null) {
                _dangling.Remove(uid);
                continue;
            }

            if (DateTime.Now - entry.since < _danglingTimeout) {
                continue;
            }

            EmpLog.Warning("ThingRequest dangling of {Uid} was never dango dongo'd, returning to {ParentUid}",
                uid, entry.origin?.uid ?? -1);

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