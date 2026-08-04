using ElinTogether.Helper;
using ElinTogether.Net;
using MessagePack;

namespace ElinTogether.Models;

[MessagePackObject]
public class CharaPickThingDelta : ElinDelta
{
    public enum PickType : byte
    {
        Pick,
        PickOrDrop,
        TrySmoothPick,
    }

    [Key(0)]
    public required RemoteCard Owner { get; init; }

    [Key(1)]
    public required RemoteCard Thing { get; init; }

    [Key(2)]
    public required Position? Pos { get; init; }

    [Key(3)]
    public required PickType Type { get; init; }

    protected override void OnApply(ElinNetBase net)
    {
        // we do not apply to ourselves
        if (Owner.Find() is not Chara chara) {
            return;
        }

        if (Thing.Find() is not Thing { isDestroyed: false } thing) {
            TaskCache.CancelClientAct(net, this, Thing);
            return;
        }

        if (net.IsHost && thing.GetRootCard() is Chara holder && holder != chara && holder.IsPlayer) {
            TaskCache.CancelClientAct(net, this, Thing);
            return;
        }

        if (CharaProgressCompleteDelta.IsApplying) {
            if (net.IsClient && chara.IsRemotePlayer) {
                _zone.AddCard(thing);
                return;
            }
        } else if (chara.IsPC) {
            return;
        }

        // relay to clients
        if (net.IsHost && Type != PickType.Pick) {
            net.Delta.AddRemote(this);
        }

        switch (Type) {
            case PickType.Pick:
                chara.Pick(thing);
                // force add
                if (net.IsHost && !thing.isDestroyed && thing.parent == _zone) {
                    EmpLog.Warning("Mirror pick of {Uid} failed to store, forcing into chara {OwnerUid}",
                        thing.uid, chara.uid);
                    // clients must add thingy
                    using (Simulate()) {
                        chara.AddThing(thing);
                    }
                }

                if (!thing.isDestroyed) {
                    EmpLog.Debug("Chara {OwnerUid} picked {Uid}, now in parent {ParentUid}",
                        chara.uid, thing.uid, (thing.parent as Card)?.uid ?? -1);
                }

                break;
            case PickType.PickOrDrop:
                chara.PickOrDrop(Pos, thing);
                break;
            case PickType.TrySmoothPick:
                _map.TrySmoothPick(Pos, thing, chara);
                break;
        }

        if (thing.isDestroyed || thing.parent != _zone) {
            return;
        }

        net.Delta.AddRemote(new ZoneAddCardDelta {
            Card = Thing,
            Pos = thing.pos,
            ZoneUid = _zone.uid,
        });
    }
}