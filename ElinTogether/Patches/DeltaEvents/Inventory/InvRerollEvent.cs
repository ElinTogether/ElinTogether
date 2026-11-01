using System.Linq;
using System.Reflection;
using ElinTogether.Helper;
using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

[HarmonyPatch]
internal static class InvRerollEvent
{
    internal static MethodInfo TargetMethod()
    {
        return typeof(UIInventory).GetNestedTypes(AccessTools.all)
            .SelectMany(t => t.GetMethods(AccessTools.all))
            .FirstOrDefault(mi => mi.Name == "<RefreshMenu>b__48")!;
    }

    [HarmonyPrefix]
    internal static bool OnInvReroll(object __instance, out InvRerollDelta? __state)
    {
        __state = null;

        if (NetSession.Instance.Connection is not { } connection) {
            return true;
        }

        var field = AccessTools.Field(__instance.GetType(), "_owner");
        var owner = field.GetValue(__instance) as Card;
        var cost = owner!.trait.CostRerollShop;
        // the Mani statue's gene reroll costs a blue capsule of the player's own, the shops' the town's influence
        // (UIInventory.RefreshMenu): the game's own button beeps when the player cannot pay
        var capsule = owner.trait.CurrencyRerollShop == CurrencyType.BlueCapsule;
        if (capsule ? !EMono.debug.enable && EMono.pc.GetCurrency("drug_blue") < cost : EMono._zone.influence < cost) {
            return true;
        }

        __state = new() {
            ShopOwner = owner,
            Gates = connection.IsClient ? ShopStock.Local() : null,
        };

        // a guest pays its capsule here, as the button does: the host checks that it left its purse (InvRerollDelta)
        if (capsule && connection.IsClient) {
            EMono.pc.ModCurrency(-1, "drug_blue");
        }

        return connection.IsHost;
    }

    [HarmonyPostfix]
    internal static void OnInvRerollEnd(InvRerollDelta? __state)
    {
        if (__state is not null) {
            NetSession.Instance.Connection?.Delta.AddRemote(__state);
        }
    }
}