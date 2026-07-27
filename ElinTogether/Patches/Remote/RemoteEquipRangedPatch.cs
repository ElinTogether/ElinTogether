using ElinTogether.Helper;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

[HarmonyPatch]
internal class RemoteEquipRangedPatch
{
    [HarmonyPrefix]
    [HarmonyPatch(typeof(Chara), nameof(Chara.TryEquipRanged), [])]
    internal static bool OnClientTryEquipRanged(Chara __instance)
    {
        if (!NetSession.Instance.HasActiveConnection) {
            return true;
        }

        if (__instance.IsRemotePlayer) {
            if (!__instance.NetProfile.RemoteMainHand.TryGetTarget(out var held) &&
                !__instance.NetProfile.RemoteOffHand.TryGetTarget(out held)) {
                held = __instance.held as Thing;
            }

            if (held is { trait: TraitToolRange } ranged) {
                __instance.ranged = ranged;
                return false;
            }
        }

        return true;
    }
}