using ElinTogether.Net;
using MessagePack;

namespace ElinTogether.Models;

[MessagePackObject]
public class CardChargeDelta : ElinDelta
{
    [Key(0)]
    public required RemoteCard Card { get; init; }

    [Key(1)]
    public required int Charges { get; init; }

    protected override void OnApply(ElinNetBase net)
    {
        // host to client only
        if (net.IsHost) {
            return;
        }

        if (Card.Find() is not { isDestroyed: false } card) {
            return;
        }

        card.c_charges = Charges;
        if (card is Thing thing) {
            LayerInventory.SetDirty(thing);
        }
    }
}