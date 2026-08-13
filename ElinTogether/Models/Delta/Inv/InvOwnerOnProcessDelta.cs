using System;
using System.Collections.Generic;
using ElinTogether.Net;
using MessagePack;
using UnityEngine;

namespace ElinTogether.Models;

[MessagePackObject]
public class InvOwnerOnProcessDelta : ElinDelta
{
    public enum RemoteInvOwnerType : byte
    {
        Unknown = 0,
        Refuel = 1,
        Effect = 2,
    }

    // draglet effect
    private static readonly HashSet<EffectId> _effectWhitelist = [
        EffectId.Identify,
        EffectId.GreaterIdentify,
        EffectId.EnchantArmor,
        EffectId.EnchantArmorGreat,
        EffectId.EnchantWeapon,
        EffectId.EnchantWeaponGreat,
        EffectId.Uncurse,
        EffectId.Lighten,
        EffectId.Reconstruction,
        EffectId.ChangeMaterial,
        EffectId.ChangeRarity,
    ];

    [Key(1)]
    public required RemoteCard? Parent { get; init; }

    [Key(2)]
    public required RemoteCard Thing { get; init; }

    [Key(3)]
    public required RemoteCard? Dest { get; init; }

    [Key(4)]
    public RemoteInvOwnerType OwnerType { get; init; }

    [Key(5)]
    public int Effect { get; init; }

    [Key(6)]
    public int EffectPower { get; init; }

    [Key(7)]
    public int EffectState { get; init; }

    [Key(8)]
    public string? EffectRefId { get; init; }

    [Key(9)]
    public RemoteCard? Consume { get; init; }

    protected override void OnApply(ElinNetBase net)
    {
        if (Thing.Find() is not Thing { isDestroyed: false } thing) {
            return;
        }

        // null parent is stale, from split
        var parent = Parent?.Find();
        if (Parent is not null && parent is null) {
            return;
        }

        if (thing.parent is not null && thing.parent != parent) {
            return;
        }

        // effect draglets has fake inv
        if (OwnerType == RemoteInvOwnerType.Effect) {
            ApplyEffect(net, thing);
            return;
        }

        if (Dest?.Find() is not { } dest) {
            return;
        }

        // refuel is dispatched by the type
        if (OwnerType == RemoteInvOwnerType.Refuel) {
            if (net is ElinNetHost refuelHost) {
                ApplyRefuel(refuelHost, thing, dest);
            }

            return;
        }

        // InvOwnerCraft.TryStartCraft
        if (dest.trait is TraitCrafter) {
            return;
        }

        switch (dest.trait) {
            case TraitRecycle recycle:
                if (net is ElinNetHost recycleHost) {
                    ApplyRecycle(recycleHost, recycle, thing);
                }

                return;
            case TraitGacha gacha:
                if (net is ElinNetHost gachaHost) {
                    ApplyGacha(gachaHost, gacha, thing);
                }

                return;
        }

        InvOwnerDraglet? destInv = null;
        switch (dest.trait) {
            case TraitChara:
                destInv = new InvOwnerGive(dest) {
                    chara = dest as Chara,
                };
                break;
            case TraitAltarChaos altarChaos:
                destInv = new InvOwnerChaosOffering(dest) {
                    altar = altarChaos,
                };
                break;
            case TraitAltar altar:
                destInv = new InvOwnerOffering(dest) {
                    altar = altar,
                };
                break;
            case TraitBank:
                destInv = new InvOwnerDeliver(dest) {
                    mode = InvOwnerDeliver.Mode.Bank,
                };
                break;
            case TraitFarmChest:
                destInv = new InvOwnerDeliver(dest) {
                    mode = InvOwnerDeliver.Mode.Crop,
                };
                break;
            case TraitTaxChest:
                destInv = new InvOwnerDeliver(dest) {
                    mode = InvOwnerDeliver.Mode.Tax,
                };
                break;
        }

        if (destInv is null) {
            return;
        }

        if (net.IsHost) {
            net.Delta.AddRemote(this);
        }

        destInv._OnProcess(thing);
    }

    private void ApplyEffect(ElinNetBase net, Thing thing)
    {
        var effectId = (EffectId)Effect;
        if (!_effectWhitelist.Contains(effectId) ||
            !Enum.IsDefined(typeof(BlessedState), EffectState)) {
            EmpLog.Warning("Refusing unlisted effect {EffectId} of {Uid} from peer {PeerIndex}",
                Effect, thing.uid, OriginPeer);
            return;
        }

        if (net is ElinNetHost host) {
            // only the peer thingies
            if (thing.GetRootCard() is not Chara root || !host.ActiveRemoteCharas.TryGetValue(OriginPeer, out var owner) ||
                root != owner) {
                EmpLog.Warning("Refusing effect {EffectId} of {Uid} from peer {PeerIndex}",
                    Effect, thing.uid, OriginPeer);
                return;
            }

            // consumed thingy
            if (Consume?.Find() is Thing t && t.GetRootCard() != owner) {
                EmpLog.Warning("Refusing effect consume {Uid} from peer {PeerIndex}",
                    t.uid, OriginPeer);
                return;
            }

            // bogus
            if (effectId == EffectId.ChangeMaterial &&
                (EffectRefId is null || !sources.materials.alias.ContainsKey(EffectRefId))) {
                EmpLog.Warning("Refusing effect {EffectId} of {Uid} from peer {PeerIndex}",
                    Effect, thing.uid, OriginPeer);
                return;
            }

            host.Delta.AddRemote(this);
        }

        using var _ = Simulate(net.IsHost);

        var actRef = EffectRefId is null ? default : new ActRef { n1 = EffectRefId };
        var power = Mathf.Clamp(EffectPower, 1, 1000);
        ActEffect.Proc(effectId, power, (BlessedState)EffectState, thing.GetRootCard(), thing, actRef);

        // InvOwnerChangeMaterial._OnProcess
        if (effectId == EffectId.ChangeMaterial && thing.trait is TraitGodStatue god) {
            god.OnChangeMaterial();
        }

        if (Consume?.Find() is Thing { isDestroyed: false } consume) {
            consume.ModNum(-1);
        }
    }

    private void ApplyRefuel(ElinNetHost host, Thing thing, Card dest)
    {
        using var _ = Simulate();

        var trait = dest.trait;
        var fuelValue = trait.GetFuelValue(thing);
        if (fuelValue <= 0) {
            EmpLog.Warning("Refusing refuel of {TargetUid} from peer {PeerIndex}, {CardId} is not fuel",
                dest.uid, OriginPeer, thing.id);
            return;
        }

        // client intent capped by stack and remaining capacity
        var room = (trait.MaxFuel - dest.c_charges) / fuelValue;
        var num = Mathf.Min(Thing.Num > 0 ? Thing.Num : thing.Num, thing.Num);
        num = Mathf.Min(num, room);
        if (num <= 0) {
            EmpLog.Debug("Refuel of {TargetUid} is already full, ignoring {Uid}", dest.uid, thing.uid);
            return;
        }

        // InvOwnerRefuel._OnProcess
        var fuel = thing.Split(num);
        trait.Refuel(fuel);
    }

    private void ApplyRecycle(ElinNetHost host, TraitRecycle recycle, Thing thing)
    {
        using var _ = Simulate();

        var receiver = ResolveReceiver(host);
        SE.Play("trash");
        Msg.Say("dump", thing, recycle.owner.Name);

        var amount = thing.Num * Mathf.Clamp(thing.GetPrice(CurrencyType.Money, false, PriceType.Tourism) / 100, 1, 100);
        amount = rndHalf(amount);
        if (thing.id == "1084") {
            amount *= 10;
        }

        if (amount != 0) {
            var ecopo = ThingGen.Create("ecopo").SetNum(amount / 10 + 1);
            _zone.AddCard(ecopo, receiver.pos);
        }

        switch (thing.id) {
            case "gene":
            case "gene_brain":
            case "1084":
                if (rnd(5) == 0 || debug.enable) {
                    recycle.owner.MakeEgg();
                }

                break;
        }

        thing.Destroy();
    }

    private void ApplyGacha(ElinNetHost host, TraitGacha gacha, Thing thing)
    {
        using var _ = Simulate();

        var receiver = ResolveReceiver(host);
        SE.Play("gacha");

        var ball = ThingGen.Create("gachaBall").SetNum(1);
        ball.refVal = (int)gacha.type;
        ball.things.DestroyAll();
        _zone.AddCard(ball, receiver.pos);

        thing.Destroy();
    }

    private Chara ResolveReceiver(ElinNetHost host)
    {
        return host.ActiveRemoteCharas.TryGetValue(OriginPeer, out var chara) ? chara : pc;
    }
}