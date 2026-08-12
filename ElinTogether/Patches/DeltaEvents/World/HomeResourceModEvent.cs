using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

[HarmonyPatch]
internal static class HomeResourceModEvent
{
    [HarmonyPrefix]
    [HarmonyPatch(typeof(HomeResource), nameof(HomeResource.Mod))]
    internal static void OnMod(HomeResource __instance, int a)
    {
        if (a == 0) {
            return;
        }

        if (NetSession.Instance.Connection is not ElinNetClient client ||
            (ElinDelta.IsApplying && !ThingRequest.IsApplying)) {
            return;
        }

        if (EClass.Branch is not { } branch || __instance.branch != branch) {
            return;
        }

        // client only
        client.Delta.AddRemote(new BranchResourceModDelta {
            ZoneUid = EClass._zone.uid,
            ResourceType = (int)__instance.type,
            Amount = a,
        });
    }
}
