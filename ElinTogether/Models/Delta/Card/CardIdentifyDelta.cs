using System;
using ElinTogether.Net;
using MessagePack;

namespace ElinTogether.Models;

[MessagePackObject]
public class CardIdentifyDelta : ElinDelta
{
    [Key(0)]
    public required RemoteCard Card { get; init; }

    [Key(1)]
    public required int State { get; init; }

    [Key(2)]
    public required byte Source { get; init; }

    protected override void OnApply(ElinNetBase net)
    {
        if (Card.Find() is not Thing { isDestroyed: false } thing) {
            return;
        }

        if (net is ElinNetHost host) {
            // client wants to identify thingy
            if (thing.GetRootCard() is not Chara root ||
                !host.ActiveRemoteCharas.TryGetValue(OriginPeer, out var owner) || root != owner) {
                EmpLog.Warning("Refusing identify of {Uid} from peer {PeerIndex}",
                    thing.uid, OriginPeer);
                return;
            }

            if (!Enum.IsDefined(typeof(IDTSource), (int)Source)) {
                return;
            }

            using var _ = Simulate();
            thing.Identify(false, (IDTSource)Source);
            return;
        }

        // host says ok
        if (thing.c_IDTState == State) {
            return;
        }

        thing.c_IDTState = State;
        LayerInventory.SetDirty(thing);
    }
}