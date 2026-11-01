using ElinTogether.Helper;
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

    /// <summary>
    ///     A count this copy sets itself and tells the host (a stack the host owns refuses the game's own ModNum)
    /// </summary>
    internal static void SetNumHere(Card card, int num)
    {
        IsOverwriting = true;
        try {
            card.SetNum(num);
        } finally {
            IsOverwriting = false;
        }
    }

    /// <summary>
    ///     Counts this copy changes itself, told by the host and not told back (a gain the host cut short)
    /// </summary>
    internal static ScopeExit Overwriting()
    {
        var previous = IsOverwriting;
        IsOverwriting = true;
        return new() {
            OnExit = () => IsOverwriting = previous,
        };
    }

    internal override string? OnHostPermit(ElinNetHost host, Chara? sender)
    {
        // a thing in the bag of another player's companion (the host's own pets too) is that player's to spend; a
        // player's own bag is refused in OnApply whatever the box says
        return NoCharacter(sender) ??
               (Card.Find()?.GetRootCard() is Chara holder && PlayerKarma.PlayerBehind(holder) is not null &&
                !OwnsOrCompanion(sender!, holder)
                   ? $"the thing is held by {holder.uid}"
                   : null);
    }

    internal override void OnHostRefused(ElinNetHost host)
    {
        GiveBack(host);
    }

    /// <summary>
    ///     The sender's copy as the host has it: back where it is (made again if it went) and its count
    /// </summary>
    private void GiveBack(ElinNetBase net)
    {
        if (Card.Find() is not Thing { isDestroyed: false } thing) {
            return;
        }

        SendBack(net, Card);
        net.Delta.AddRemote(new CardModNumDelta {
            Card = thing,
            Num = thing.Num,
        });
    }

    protected override void OnApply(ElinNetBase net)
    {
        if (Card.Find() is not { isDestroyed: false } card) {
            TaskCache.CancelClientAct(net, this, Card);
            return;
        }

        if (net.IsHost) {
            // a character is never a stack: a client copy going away must not destroy the host one
            if (card is Chara) {
                EmpLog.Warning("Refusing {DeltaType} on chara {Uid} from peer {PeerIndex}",
                    nameof(CardModNumDelta), card.uid, OriginPeer);
                return;
            }

            // a client only ever says a card went away (CardDestroyEvent, Num 0): never more than there is, never
            // in the bag of another player
            var sender = (net as ElinNetHost)!.ActiveRemoteCharas.TryGetValue(OriginPeer, out var who) ? who : null;
            if (Num < 0 || Num > card.Num || card.GetRootCard() is Chara { IsPlayer: true } root && root != sender) {
                EmpLog.Warning("Refusing {DeltaType} {Uid}: {CardNum} -> {NewCardNum} from peer {PeerIndex}",
                    nameof(CardModNumDelta), card.uid, card.Num, Num, OriginPeer);
                GiveBack(net);
                return;
            }

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