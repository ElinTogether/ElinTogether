using ElinTogether.Net;
using MessagePack;

namespace ElinTogether.Models;

[MessagePackObject]
public class CardAddThingDelta : ElinDelta
{
    [Key(0)]
    public required RemoteCard Thing { get; init; }

    [Key(1)]
    public required RemoteCard Parent { get; init; }

    [Key(2)]
    public required bool TryStack { get; init; }

    [Key(3)]
    public required int DestInvX { get; init; }

    [Key(4)]
    public required int DestInvY { get; init; }

    protected override void OnApply(ElinNetBase net)
    {
        if (Thing.Find() is not Thing { isDestroyed: false } thing) {
            EmpLog.Warning("Dropping {DeltaType} from peer {PeerIndex}, uid {Uid} cannot be resolved here",
                nameof(CardAddThingDelta), OriginPeer, Thing.Uid);
            return;
        }

        if (Parent.Find() is not { isDestroyed: false } parent) {
            EmpLog.Warning("Dropping {DeltaType} from peer {PeerIndex}, parent uid {Uid} cannot be resolved here",
                nameof(CardAddThingDelta), OriginPeer, Parent.Uid);
            return;
        }

        if (net.IsHost) {
            net.Delta.AddRemote(this);
        }

        if (thing.parent != parent) {
            var added = parent.AddThing(thing, TryStack, DestInvX, DestInvY);
            if (added == thing) {
                if (DestInvX >= 0) {
                    added.invX = DestInvX;
                }

                if (DestInvY >= 0) {
                    added.invY = DestInvY;
                    if (DestInvY == 1) {
                        WidgetCurrentTool.dirty = true;
                    }
                }
            }

            EmpLog.Debug("Add thing {Uid} into parent {ParentUid}", thing.uid, parent.uid);
        }
    }

    protected override bool OnRefresh()
    {
        if (Thing.Find() is not Thing { isDestroyed: false } thing) {
            return false;
        }

        if (NetSession.Instance.IsHost) {
            Thing.Data = LZ4Bytes.Create(thing);
            Thing.Num = thing.Num;
        }

        return true;
    }
}