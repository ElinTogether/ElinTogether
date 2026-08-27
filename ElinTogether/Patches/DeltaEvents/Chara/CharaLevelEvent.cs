using System.Collections.Generic;
using System.Reflection;
using ElinTogether.Helper;
using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

[HarmonyPatch]
internal static class CharaLevelEvent
{
    internal static IEnumerable<MethodBase> TargetMethods()
    {
        return [
            AccessTools.PropertySetter(typeof(Card), nameof(Card.LV)),
            AccessTools.PropertySetter(typeof(Card), nameof(Card.exp)),
        ];
    }

    [HarmonyPrefix]
    internal static void OnSetLevelState(Card __instance, out (int Lv, int Exp) __state)
    {
        __state = (__instance.LV, __instance.exp);
    }

    // client only
    [HarmonyPostfix]
    internal static void OnSetLevelEnd(Card __instance, (int Lv, int Exp) __state)
    {
        if (NetSession.Instance.Connection is not ElinNetClient client || ElinDelta.IsRemoteStateLanding) {
            return;
        }

        if (__instance is not Chara { IsPC: true } chara ||
            (chara.LV == __state.Lv && chara.exp == __state.Exp)) {
            return;
        }

        client.Delta.AddRemote(new CharaLevelDelta {
            Owner = chara,
            Level = chara.LV,
            Exp = chara.exp,
        });
    }
}

[HarmonyPatch(typeof(Card), nameof(Card.AddExp))]
internal static class CardAddExpMirrorGuard
{
    [HarmonyPrefix]
    internal static bool OnAddExp(Card __instance)
    {
        if (NetSession.Instance.Connection is null) {
            return true;
        }

        return __instance is not Chara { IsRemotePlayer: true };
    }
}

[HarmonyPatch(typeof(Card), nameof(Card.LevelUp))]
internal static class CardLevelUpMirrorGuard
{
    [HarmonyPrefix]
    internal static bool OnLevelUp(Card __instance)
    {
        if (NetSession.Instance.Connection is null) {
            return true;
        }

        return __instance is not Chara { IsRemotePlayer: true };
    }
}