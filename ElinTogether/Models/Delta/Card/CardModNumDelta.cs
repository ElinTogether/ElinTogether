using ElinTogether.Net;
using MessagePack;

namespace ElinTogether.Models;

[MessagePackObject]
public class CardModNumDelta : ElinDelta
{
    [Key(0)]
    public required RemoteCard Card { get; init; }

    [Key(1)]
    public required int Num { get; init; }

    // SetNum -> ModNum, been overwritten by host value
    // TODO could fix this by swapping Elin internal code
    public static bool IsOverwriting { get; private set; }

    protected override void OnApply(ElinNetBase net)
    {
        if (Card.Find() is not { isDestroyed: false } card) {
            TaskCache.CancelClientAct(net, this, Card);
            return;
        }

        if (net.IsHost) {
            net.Delta.AddRemote(this);
        }

        EmpLog.Debug("Applying card num {Uid}: {CardNum} -> {NewCardNum}",
            card.uid, card.Num, Num);

        IsOverwriting = true;
        try {
            card.SetNum(Num);
        } finally {
            IsOverwriting = false;
        }

        // set num
        if (card is Thing { isDestroyed: false } thing) {
            LayerInventory.SetDirty(thing);
        }

        // crafter ui
        if (LayerDragGrid.Instance != null) {
            LayerDragGrid.Instance.Redraw();
        }
    }
}