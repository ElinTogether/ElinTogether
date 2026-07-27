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
            EmpLog.Warning("Dropping {DeltaType}, uid {Uid} cannot be resolved here",
                nameof(CardAddThingDelta), Thing.Uid);
            return;
        }

        if (Parent.Find() is not { isDestroyed: false } parent) {
            EmpLog.Warning("Dropping {DeltaType}, parent uid {Uid} cannot be resolved here",
                nameof(CardAddThingDelta), Parent.Uid);
            return;
        }

        if (net.IsHost) {
            net.Delta.AddRemote(this);
        }

        if (thing.parent != parent) {
            parent.AddThing(thing, TryStack, DestInvX, DestInvY);
        }
    }

    protected override bool OnRefresh()
    {
        if (Thing.Find() is not Thing { isDestroyed: false } thing) {
            return false;
        }

        if (CardGenDelta.WasCreatedThisFrame(thing.uid)) {
            Thing.Data = LZ4Bytes.Create(thing);
            Thing.Num = thing.Num;
        }

        return true;
    }
}