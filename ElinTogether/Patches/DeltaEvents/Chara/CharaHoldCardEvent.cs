using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

[HarmonyPatch(typeof(Chara), nameof(Chara.HoldCard))]
internal static class CharaHoldCardEvent
{
    [HarmonyPrefix]
    internal static void OnHoldCard(Chara __instance, Card t, ref int num)
    {
        if (NetSession.Instance.Connection is not ElinNetClient || __instance.IsPC) {
            return;
        }

        // just visual
        if (t.isThing && num > 0 && num < t.Num) {
            num = -1;
        }
    }
}