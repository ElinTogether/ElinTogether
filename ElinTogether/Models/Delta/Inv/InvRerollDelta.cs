using ElinTogether.Helper;
using ElinTogether.Net;
using MessagePack;

namespace ElinTogether.Models;

[MessagePackObject]
public class InvRerollDelta : ElinDelta
{
    [Key(0)]
    public required RemoteCard ShopOwner { get; init; }

    [Key(1)]
    public ShopGates? Gates { get; set; }

    protected override void OnApply(ElinNetBase net)
    {
        if (ShopOwner.Find() is not { } shopOwner) {
            return;
        }

        var cost = shopOwner.trait.CostRerollShop;
        var inv = LayerInventory.listInv.Find(l => l.invs[0].owner.owner == shopOwner)?.invs[0];
        // the Mani statue's reroll is paid with the player's own blue capsule, not the town's influence
        var capsule = shopOwner.trait.CurrencyRerollShop == CurrencyType.BlueCapsule;
        if (net.IsClient) {
            if (!capsule) {
                EMono._zone.influence -= cost;
            }

            shopOwner.c_dateStockExpire = world.date.GetRaw(24 * shopOwner.trait.RestockDay);
            if (inv is null) {
                return;
            }

            inv.RefreshGrid();
            inv.Sort();
            SE.Dice();
            SE.Play("shop_open");

            return;
        }

        if (capsule) {
            // the capsule the sender's game took (InvRerollEvent) must have left its purse here first
            if (CardModCurrencyDelta.TakeSpent(OriginPeer, 1, currency: "drug_blue") < 1) {
                EmpLog.Warning("Refused gene reroll of {Uid} from peer {Peer}: no blue capsule paid", shopOwner.uid, OriginPeer);
                return;
            }
        } else {
            if (EMono._zone.influence < cost) {
                return;
            }

            EMono._zone.influence -= cost;
        }

        shopOwner.c_dateStockExpire = 0;
        using (Simulate())
        using (ShopStock.Opened(Gates, FromPeer && net is ElinNetHost host ? host.PlayerChara(OriginPeer) : null)) {
            shopOwner.trait.OnBarter(true);
        }

        net.Delta.AddRemote(this);

        if (inv is null) {
            return;
        }

        inv.RefreshGrid();
        inv.Sort();
        SE.Dice();
        SE.Play("shop_open");
    }
}