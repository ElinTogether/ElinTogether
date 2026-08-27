using ElinTogether.Helper;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

[HarmonyPatch(typeof(Card), nameof(Card.ModExpParty), typeof(int), typeof(int))]
internal static class RemoteModExpPartyPatch
{
    [HarmonyPrefix]
    internal static bool OnModExpParty(Card __instance)
    {
        if (NetSession.Instance.Connection is null) {
            return true;
        }

        return __instance is not Chara { IsRemotePlayer: true };
    }
}