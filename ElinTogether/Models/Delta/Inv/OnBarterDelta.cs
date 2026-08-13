using ElinTogether.Net;
using MessagePack;

namespace ElinTogether.Models;

[MessagePackObject]
public class OnBarterDelta : ElinDelta
{
    [Key(0)]
    public required RemoteCard ShopOwner { get; init; }

    protected override void OnApply(ElinNetBase net)
    {
        if (ShopOwner.Find() is not { } shopOwner) {
            return;
        }

        if (net.IsHost) {
            // resync host chest
            if (shopOwner.things.Find("chest_merchant") is { } stock) {
                net.Delta.AddRemote(new CardAddThingDelta {
                    Thing = RemoteCard.Create(stock),
                    Parent = shopOwner,
                    TryStack = false,
                    DestInvX = -1,
                    DestInvY = -1,
                });
            }

            // restock
            using var _ = Simulate();
            shopOwner.trait.OnBarter();
            return;
        }

        shopOwner.c_dateStockExpire = world.date.GetRaw(24 * shopOwner.trait.RestockDay);

        var inv = LayerInventory.listInv.Find(l => l.invs[0].owner.owner == shopOwner)?.invs[0];
        if (inv is null) {
            return;
        }

        var invOwnerShop = inv.owner;
        if (!invOwnerShop.Container.IsHostOwned) {
            // exclude temp chest
            var temp = invOwnerShop.Container.Thing;
            if (shopOwner.things.Find(t => t.id == "chest_merchant" && t != temp) is not { } chest) {
                EmpLog.Warning("Merchant chest of {Uid} is not resolved here, keeping temp chest",
                    shopOwner.uid);
                return;
            }

            // remove the temporary merchant chest
            shopOwner.things.Remove(temp);
            // replace it with the real merchant chest
            invOwnerShop.Container = chest;
        }

        inv.RefreshGrid();
        inv.Sort();
    }
}