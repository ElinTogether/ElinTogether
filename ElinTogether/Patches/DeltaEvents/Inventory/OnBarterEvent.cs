using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

[HarmonyPatch(typeof(Trait), nameof(Trait.OnBarter))]
internal class OnBarterEvent
{
    [HarmonyPrefix]
    internal static bool OnBarter(Trait __instance, out (OnBarterDelta? delta, ElinDelta.PatchScope scope) __state)
    {
        __state = default;

        if (NetSession.Instance.Connection is not { } connection) {
            return true;
        }

        var owner = __instance.owner;
        if (connection.IsClient && owner.things.Find("chest_merchant") is null) {
            // create a temp chest
            using var _ = PendingContext.Begin();
            var chest = ThingGen.Create("chest_merchant");
            chest.parent = owner;
            owner.things.Add(chest);
        }

        // restock is from host
        __state = (new() {
            ShopOwner = owner,
        }, ElinDelta.PatchScope.Simulate(connection.IsHost));

        return connection.IsHost;
    }

    [HarmonyPostfix]
    internal static void OnBarterEnd((OnBarterDelta? delta, ElinDelta.PatchScope scope) __state)
    {
        if (__state.delta is null) {
            return;
        }

        NetSession.Instance.Connection?.Delta.AddRemote(__state.delta);
    }

    [HarmonyFinalizer]
    internal static void OnBarterCleanup((OnBarterDelta? delta, ElinDelta.PatchScope scope) __state)
    {
        __state.scope.Exit();
    }
}

[HarmonyPatch(typeof(TraitVendingMachine), nameof(TraitVendingMachine.OnUse))]
internal static class TraitVendingMachineUseEvent
{
    [HarmonyPrefix]
    internal static bool OnVendingUse(TraitVendingMachine __instance, Chara c, ref bool __result)
    {
        if (NetSession.Instance.Connection is null || !ElinDelta.IsApplying || c.IsPC) {
            return true;
        }

        // replaying remote player use restock only
        __instance.OnBarter();
        __result = false;
        return false;
    }
}