using System.Linq;
using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

[HarmonyPatch(typeof(GameDate), nameof(GameDate.ShipGoods))]
internal static class WorldShipGoodsEvent
{
    [HarmonyPrefix]
    internal static bool OnShipGoods(ref ShippingResult? __state)
    {
        __state = EClass.player.shippingResults.LastItem();
        // client can't initiate this
        return NetSession.Instance.Connection is not ElinNetClient;
    }

    [HarmonyPostfix]
    internal static void OnAfterShipGoods(ShippingResult? __state)
    {
        if (NetSession.Instance.Connection is not ElinNetHost host) {
            return;
        }

        if (EClass.player.shippingResults.LastItem() is not { } result || result == __state) {
            return;
        }

        var zone = EClass.game.spatials.Find(result.uidZone);
        host.Delta.AddRemote(new ShippingResultDelta {
            Ints = [..result.ints],
            ItemStrs = [..result.items.Select(item => item._strs)],
            ShipNum = EClass.player.stats.shipNum,
            ShipMoney = EClass.player.stats.shipMoney,
            BranchLv = zone?.branch?.lv ?? 0,
            BranchExp = zone?.branch?.exp ?? 0,
        });

        EmpLog.Debug("Shipping result {ShipIncome} {ShipItemCount} {ZoneUid}",
            result.GetIncome(), result.items.Count, result.uidZone);
    }
}