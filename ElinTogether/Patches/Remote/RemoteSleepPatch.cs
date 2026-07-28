using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

[HarmonyPatch]
internal static class RemoteSleepPatch
{
    [HarmonyPrefix]
    [HarmonyPatch(typeof(Chara), nameof(Chara.Sleep))]
    internal static bool OnPcSleep(Chara __instance)
    {
        // host only
        if (NetSession.Instance.IsHost || !__instance.IsPC) {
            return true;
        }

        EmpPop.Debug("Blocked sleeping as client");
        return false;
    }
}