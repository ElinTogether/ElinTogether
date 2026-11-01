using System.Collections.Generic;
using ElinTogether.Helper;
using ElinTogether.Net;
using MessagePack;

namespace ElinTogether.Models;

[MessagePackObject]
public class CardAddThingDelta : ElinDelta
{
    [Key(0)]
    public required RemoteCard Thing { get; init; }

    [Key(1)]
    public required RemoteCard Parent { get; set; }

    [Key(2)]
    public required bool TryStack { get; init; }

    [Key(3)]
    public required int DestInvX { get; init; }

    [Key(4)]
    public required int DestInvY { get; init; }

    /// <summary>
    ///     Into the shipping box: chara uid of the player shipping it, see ShippingHelper
    /// </summary>
    [Key(5)]
    public int Shipper { get; set; }

    internal override string? OnHostPermit(ElinNetHost host, Chara? sender)
    {
        // (already where it is put: the gift or trade that moved it was checked on its own, this is the sender's game
        // telling the same move after it)
        if (Thing.Find() is Thing { } moved && Parent.Find() is { } into && moved.GetRootCard() == into.GetRootCard()) {
            return null;
        }

        return NoCharacter(sender) ?? NotReachable(sender, Thing) ?? NotReachable(sender, Parent);
    }

    internal override void OnHostRefused(ElinNetHost host)
    {
        SendBack(host, Thing);
    }

    protected override void OnApply(ElinNetBase net)
    {
        var found = Thing.Find();
        if (found is { isDestroyed: true } && Thing.Data is not null && net.IsClient) {
            // destroyed here only (a copy stacked what its player did not): the host sent it whole, made again
            CardCache.Remove(Thing.Uid);
            found = Thing.Find();
        }

        if (found is not Thing { isDestroyed: false } thing) {
            EmpLog.Warning("Dropping {DeltaType} from peer {PeerIndex}, uid {Uid} cannot be resolved here",
                nameof(CardAddThingDelta), OriginPeer, Thing.Uid);
            return;
        }

        if (Parent.Find() is not { isDestroyed: false } parent) {
            EmpLog.Warning("Dropping {DeltaType} from peer {PeerIndex}, parent uid {Uid} cannot be resolved here",
                nameof(CardAddThingDelta), OriginPeer, Parent.Uid);
            return;
        }

        if (net is ElinNetHost host && OriginPeer != 0 &&
            thing.GetRootCard() is Chara { IsPlayer: true } holder &&
            holder != host.ActiveRemoteCharas.GetValueOrDefault(OriginPeer)) {
            EmpLog.Warning("Refusing {DeltaType} from peer {PeerIndex}, uid {Uid} is held by player {HolderUid}",
                nameof(CardAddThingDelta), OriginPeer, Thing.Uid, holder.uid);
            Rebind(net, Thing, thing);
            return;
        }

        // an installed thing on the way to Vernis's mine is nobody's to take, nor the furniture of Vernis not claimed
        // (MineWay; build mode's deconstruct tool is open there)
        if (net is ElinNetHost && FromPeer && (MineWay.ProtectsCard(thing) || MineWay.IsTownFurniture(thing))) {
            SendBack(net, Thing);
            return;
        }

        if (ShippingHelper.IsShippingBox(parent)) {
            // the host knows who sent it, everyone tags it before stacking (goods of two players never merge)
            if (net is ElinNetHost shippingHost && OriginPeer != 0 &&
                shippingHost.ActiveRemoteCharas.GetValueOrDefault(OriginPeer) is { } sender) {
                Shipper = ShippingHelper.Enabled ? sender.uid : 0;

                // travelling: the box here is a copy, the goods go to the real host
                if (shippingHost.ForwardShippingDeposit(thing, Shipper)) {
                    return;
                }
            }

            thing.SetInt(ShippingHelper.ShipperKey, Shipper);
        }

        // the bank and the delivery box of a zone session are copies too: what a guest puts in goes to the real
        // ones, it would be lost with the copy
        if (net is ElinNetHost boxHost && OriginPeer != 0 && ShippingHelper.OtherWorldBox(parent) is var box and not 0 &&
            boxHost.ForwardShippingDeposit(thing, 0, box)) {
            return;
        }

        // Teams keep their own bank and delivery box: what a player puts in the world's goes to its owner's, chosen
        // here from the sender, and nobody puts anything in another owner's (the others are told where it landed)
        if (net is ElinNetHost boxKeeper && OriginPeer != 0 && TeamBoxes.On) {
            if (TeamBoxes.Target(boxKeeper, OriginPeer, parent) is not { } landing) {
                EmpLog.Warning("Refusing {DeltaType} from peer {PeerIndex}, the box of uid {Uid} is another team's",
                    nameof(CardAddThingDelta), OriginPeer, parent.uid);
                Rebind(net, Thing, thing);
                return;
            }

            if (landing != parent) {
                parent = landing;
                Parent = landing;
            }
        }

        // gold a player puts in the bank: a line for everyone (the withdrawals are told by WorldBoxPatch)
        if (net is ElinNetHost { IsZoneSession: false } bank && OriginPeer != 0 && thing.id == "money" &&
            ShippingHelper.OtherWorldBox(parent) == ShippingHelper.BoxBank &&
            bank.ActiveRemoteCharas.GetValueOrDefault(OriginPeer) is { } depositor) {
            BillPayDelta.TellBank(bank, depositor.NameSimple, thing.Num, true);
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

            // a player handing gear to a companion or resident (the trade window, a gift): the game puts it on at
            // once (InvOwner.Transaction.Process, Chara.GiveGift call TryEquip). A guest's game did that on its
            // own copy only (its equips of others are not told), so here the gear lay loose in the bag, and the
            // wearer's idle AI put it away in a shared chest (TryPutSharedItems): it vanished from the guest's
            // companion a moment later (issue #38). The same call here, told to everyone
            if (net is ElinNetHost && OriginPeer != 0 && parent is Chara { IsPlayer: false } wearer &&
                added is { isDestroyed: false } && added.parent == wearer) {
                if (!wearer.body.slots.Exists(s => s.thing == added)) {
                    // a stale flag would make vanilla skip it or toggle the slot it points to
                    added.c_equippedSlot = 0;
                }

                if (!added.isEquipped) {
                    using var _ = Simulate();
                    wearer.TryEquip(added);
                }
            }
        }
    }

    internal static void Rebind(ElinNetBase net, RemoteCard remote, Thing thing)
    {
        if (thing.parent is not Card parent) {
            return;
        }

        net.Delta.AddRemote(new CardAddThingDelta {
            Thing = remote,
            Parent = parent,
            TryStack = false,
            DestInvX = thing.invX,
            DestInvY = thing.invY,
        });
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