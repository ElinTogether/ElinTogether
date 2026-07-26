using System;
using System.Collections.Generic;
using ElinTogether.Net;
using MessagePack;

namespace ElinTogether.Models;

[MessagePackObject]
public class ThingRequest : ElinDelta
{
    private static readonly Dictionary<int, (Action<Thing>, Action?)> _callbackList = [];

    private static int _nextId;

    public static new bool IsApplying;

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
                IsApplying = true;
                try {
                    var (onSuccess, onFail) = value;
                    if (thing is not null) {
                        onSuccess(thing);
                    } else {
                        onFail?.Invoke();
                    }
                } finally {
                    IsApplying = false;
                }
            }

            return;
        }

        if (thing is null || thing.parent is null) {
            Respond(net, null);
            return;
        }

        var result = thing.Split(Num);
        result.parent?.RemoveCard(result);
        CardCache.KeepAlive(result);

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
        _nextId = 0;
    }
}