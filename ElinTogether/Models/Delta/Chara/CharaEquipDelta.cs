using ElinTogether.Net;
using MessagePack;

namespace ElinTogether.Models;

[MessagePackObject]
public class CharaEquipDelta : ElinDelta
{
    [Key(0)]
    public required RemoteCard Owner { get; init; }

    [Key(1)]
    public required RemoteCard Thing { get; init; }

    [Key(2)]
    public required int SlotIndex { get; init; }

    [Key(3)]
    public required int SlotElementId { get; init; }

    [Key(4)]
    public required bool Equip { get; init; }

    internal override string? OnHostPermit(ElinNetHost host, Chara? sender)
    {
        return NoCharacter(sender) ?? NotOwned(sender, Owner, "wearer") ?? NotReachable(sender, Thing);
    }

    internal override void OnHostRefused(ElinNetHost host)
    {
        SendBack(host, Thing);
    }

    protected override void OnApply(ElinNetBase net)
    {
        if (Owner.Find() is not Chara chara || Thing.Find() is not Thing { isDestroyed: false } thing) {
            EmpLog.Warning("Dropping {DeltaType} from peer {PeerIndex}, owner {OwnerUid} or thing {Uid} unresolved",
                nameof(CharaEquipDelta), OriginPeer, Owner.Uid, Thing.Uid);
            return;
        }

        // a client equips itself, or one of its companions standing on its map (the game's ally window)
        Chara? sender = null;
        if (net is ElinNetHost host &&
            (!host.ActiveRemoteCharas.TryGetValue(OriginPeer, out sender) ||
             (sender != chara && (!OwnsOrCompanion(sender, chara) || chara.currentZone != sender.currentZone)))) {
            EmpLog.Warning("Refusing {DeltaType} from peer {PeerIndex}, owner {OwnerUid} is not the sender's",
                nameof(CharaEquipDelta), OriginPeer, chara.uid);
            return;
        }

        // what sits in someone else's bag is not this player's to put on (it may have just been traded away); the
        // sender's own bag is, for its companion: the game moves the thing from it inside Equip, and the move may
        // be told after the equip
        if (net is ElinNetHost && Equip && thing.GetRootCard() is Chara holder && holder != chara && holder != sender) {
            EmpLog.Warning("Refusing {DeltaType} from peer {PeerIndex}, thing {Uid} is held by {HolderUid}",
                nameof(CharaEquipDelta), OriginPeer, thing.uid, holder.uid);
            return;
        }

        if (net.IsHost) {
            net.Delta.AddRemote(this);
        }

        if (chara.IsPC) {
            return;
        }

        var slots = chara.body.slots;

        // the slot tells what is applied here, not c_equippedSlot: a thing added and equipped in the same tick
        // is serialised after the equip (CardAddThingDelta.OnRefresh) and arrives flagged while no slot holds it
        var held = slots.Find(s => s.thing == thing);

        if (!Equip) {
            if (held is not null) {
                chara.body.Unequip(held);
            } else if (thing.parent == chara) {
                thing.c_equippedSlot = 0;
            }

            return;
        }

        var slot = SlotIndex >= 0 && SlotIndex < slots.Count && slots[SlotIndex].elementId == SlotElementId
            ? slots[SlotIndex]
            : null;

        if (slot is null) {
            slot = slots.Find(s => s.elementId == SlotElementId && s.thing is null) ??
                   slots.Find(s => s.elementId == SlotElementId);
            EmpLog.Warning("Slot index {SlotIndex} drifted on chara {OwnerUid}, fallback by element {ElementId}",
                SlotIndex, chara.uid, SlotElementId);
        }

        if (slot is null) {
            EmpLog.Warning("Dropping {DeltaType}, no slot of element {ElementId} on chara {OwnerUid}",
                nameof(CharaEquipDelta), SlotElementId, chara.uid);
            return;
        }

        // vanilla Equip on the slot already holding the thing is a toggle
        if (held == slot) {
            return;
        }

        if (held is null) {
            // vanilla Equip trusts the flag and would unequip whatever sits in the slot it points to
            thing.c_equippedSlot = 0;
        }

        chara.body.Equip(thing, slot, false);
    }
}