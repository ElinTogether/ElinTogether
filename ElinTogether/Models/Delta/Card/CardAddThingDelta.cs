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
        if (Thing.Find() is not Thing { isDestroyed: false } thing ||
            Parent.Find() is not { isDestroyed: false } parent) {
            return;
        }

        if (net.IsHost) {
            net.Delta.AddRemote(this);
        }

        if (thing.parent != parent) {
            parent.AddThing(thing, TryStack, DestInvX, DestInvY);
        }
    }
}