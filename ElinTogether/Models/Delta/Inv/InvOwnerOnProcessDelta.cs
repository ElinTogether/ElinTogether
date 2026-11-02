using System;
using System.Collections.Generic;
using ElinTogether.Helper;
using ElinTogether.Net;
using ElinTogether.Patches;
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

        /// <summary>
        ///     Rune or plug (InvOwnerMod): Dest is the rune or plug, Thing the item that takes it
        /// </summary>
        Mod = 3,
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

    /// <summary>
    ///     Offering on an altar: the dice the offerer rolled, so that the duel of faiths ends the same in every game
    ///     (0 from an older peer: each game rolls its own)
    /// </summary>
    [Key(10)]
    public int Seed { get; init; }

    /// <summary>
    ///     Reconstruction: the deepest level the reader reached, its own (see OwnSettings), which the rebuilt item's
    ///     level starts from; the game reads the host's (audit 2026-10-23). Null from an older peer: the host's
    /// </summary>
    [Key(11)]
    public int? Deepest { get; init; }

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

        if (OwnerType == RemoteInvOwnerType.Mod) {
            ApplyMod(net, thing);
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
            // InvOwnerBlend: a client's game plays the dip itself, but its ModNum(-1) of the potion, water or dye
            // (a host card) does nothing there: the host uses it up (GitHub issue 61, blessed water never spent)
            case TraitDrink or TraitDye or TraitDreamBug:
                if (net is ElinNetHost blendHost && dest is Thing { isDestroyed: false } blend &&
                    blendHost.ActiveRemoteCharas.TryGetValue(OriginPeer, out var blender) &&
                    NotReachable(blender, Dest) is null && blend.trait.CanBlend(thing)) {
                    using var blendSimulate = Simulate();
                    blend.ModNum(-1);
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
            case TraitAltar altar: {
                // a guest's right click on its bag ("Offer") asks the host for the thing first (ThingRequest): the
                // host takes it out of the bag and holds it nowhere until that guest's game puts it somewhere. The
                // guest offers it from its bag, which the host is never told: back there here first, silently (the
                // guest has it there), or the offering was dropped below and the thing came back 10 s later
                // (ThingRequest.InvalidateDangling, issue #35)
                if (net is ElinNetHost backHost && thing.parent is null && parent is not null &&
                    backHost.ActiveRemoteCharas.TryGetValue(OriginPeer, out var asker) && parent.GetRootCard() == asker) {
                    parent.AddThing(thing, false);
                }

                if (thing.GetRootCard() is not Chara { IsRemotePlayer: true } offerer) {
                    return;
                }

                if (net is ElinNetHost offerHost) {
                    if (!offerHost.ActiveRemoteCharas.TryGetValue(OriginPeer, out var offerSender) ||
                        offerSender != offerer) {
                        EmpLog.Warning("Refusing offering of {Uid} from peer {PeerIndex}",
                            thing.uid, OriginPeer);
                        return;
                    }

                    offerHost.Delta.AddRemote(this);
                }

                // the host plays it as its own gesture, so that what it makes (the reforged artifact) is announced
                using var simulate = Simulate(net.IsHost);
                AltarDice.Next = Seed;
                altar.OnOffer(offerer, thing);
                return;
            }
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

        // the sender already paid the bill from its own purse
        PaidByRemote = net.IsHost && OriginPeer != 0 && dest.trait is TraitTaxChest;
        try {
            destInv._OnProcess(thing);
        } finally {
            PaidByRemote = false;
        }
    }

    /// <summary>
    ///     Set while the host repeats the payment of a bill another player made
    /// </summary>
    internal static bool PaidByRemote { get; private set; }

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
        using var __ = MsgRelayContext.Suppress(net.IsHost);

        var actRef = EffectRefId is null ? default : new ActRef { n1 = EffectRefId };
        var power = Mathf.Clamp(EffectPower, 1, 1000);

        // the game puts the rebuilt item on whoever is not "the player", in place of what it wore
        using var reader = effectId == EffectId.Reconstruction && thing.GetRootCard() is Chara { IsRemotePlayer: true } who
            ? RemoteCraft.AsCrafter(who)
            : null;
        var ownDeepest = player.stats.deepest;
        if (reader is not null && Deepest is { } deepest) {
            player.stats.deepest = Math.Max(deepest, 0);
        }

        try {
            ActEffect.Proc(effectId, power, (BlessedState)EffectState, thing.GetRootCard(), thing, actRef);
        } finally {
            player.stats.deepest = ownDeepest;
        }

        // InvOwnerChangeMaterial._OnProcess
        if (effectId == EffectId.ChangeMaterial && thing.trait is TraitGodStatue god) {
            god.OnChangeMaterial();
        }

        if (Consume?.Find() is Thing { isDestroyed: false } consume) {
            consume.ModNum(-1);
        }
    }

    /// <summary>
    ///     InvOwnerMod._OnProcess. Nothing syncs the runes and sockets of an item, so every game puts it on
    ///     itself (deterministic), and only the host uses the rune up: a client cannot, its stack change is
    ///     what the host's CardModNumDelta brings
    /// </summary>
    private void ApplyMod(ElinNetBase net, Thing thing)
    {
        if (Dest?.Find() is not Thing { isDestroyed: false } mod || mod.trait is not TraitMod traitMod) {
            return;
        }

        if (net is ElinNetHost host) {
            // the sender's own rune and item, and what the window's own guide lets through
            if (!host.ActiveRemoteCharas.TryGetValue(OriginPeer, out var owner) ||
                thing.GetRootCard() != owner || mod.GetRootCard() != owner ||
                !new InvOwnerMod(mod).ShouldShowGuide(thing)) {
                EmpLog.Warning("Refusing mod {ModUid} on {Uid} from peer {PeerIndex}",
                    mod.uid, thing.uid, OriginPeer);
                return;
            }

            host.Delta.AddRemote(this);
        }

        // only the player who used it, the others get the result
        if (thing.GetRootCard() is Chara { IsPC: true }) {
            Msg.Say("modded", thing, mod);
            SE.Play(traitMod is TraitRune ? "intonation" : "reloaded");
            pc.PlayEffect(traitMod is TraitRune ? "intonation" : "identify");
        }

        using var _ = Simulate(net.IsHost);

        if (traitMod is TraitRune) {
            thing.AddRune(mod);
            if (net.IsHost) {
                mod.ModNum(-1);
            }
        } else if (thing.sockets is not null) {
            // the plug is destroyed by the host alone
            thing.ApplySocket(traitMod.source.id, mod.encLV, net.IsHost ? mod : null);
        }

        LayerInventory.SetDirty(thing);
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
        // fueled
        using var __ = MsgRelayContext.RedirectTo(OriginPeer);
        trait.Refuel(fuel);
    }

    private void ApplyRecycle(ElinNetHost host, TraitRecycle recycle, Thing thing)
    {
        using var _ = Simulate();

        var receiver = ResolveReceiver(host);
        using var __ = MsgRelayContext.RedirectTo(OriginPeer);
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