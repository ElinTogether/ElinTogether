using ElinTogether.Net;
using MessagePack;
using UnityEngine;

namespace ElinTogether.Models;

[MessagePackObject]
public class InvOwnerOnProcessDelta : ElinDelta
{
    [Key(1)]
    public required RemoteCard Parent { get; init; }

    [Key(2)]
    public required RemoteCard Thing { get; init; }

    [Key(3)]
    public required RemoteCard Dest { get; init; }

    protected override void OnApply(ElinNetBase net)
    {
        if (Parent.Find() is not { } parent
            || Thing.Find() is not Thing { isDestroyed: false } thing
            || Dest.Find() is not { } dest) {
            return;
        }

        if (thing.parent is not null && thing.parent != parent) {
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

        if (net.IsHost) {
            net.Delta.AddRemote(this);
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

        destInv?._OnProcess(thing);
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