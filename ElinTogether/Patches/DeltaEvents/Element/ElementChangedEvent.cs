using System;
using System.Collections.Generic;
using System.Reflection;
using ElinTogether.Helper;
using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

[HarmonyPatch]
internal class ElementChangedEvent
{
    internal static IEnumerable<MethodBase> TargetMethods()
    {
        return [
            AccessTools.Method(typeof(ElementContainer), nameof(ElementContainer.GetOrCreateElement), [typeof(int)]),
            AccessTools.Method(typeof(ElementContainer), nameof(ElementContainer.GetElement), [typeof(int)]),
            AccessTools.Method(typeof(ElementContainer), nameof(ElementContainer.CreateElement), [typeof(int)]),
        ];
    }

    [HarmonyPrefix]
    internal static void OnCheckElementChange(ElementContainer __instance, int id, out int[]? __state)
    {
        __state = null;
        if (__instance.dict.TryGetValue(id, out var element)) {
            __state = [element.vBase, element.vExp, element.vPotential, element.vTempPotential];
        }
    }

    [HarmonyPostfix]
    internal static void OnSyncElementChange(ElementContainer __instance, Element? __result, int[]? __state)
    {
        if (NetSession.Instance.Connection is not { } connection || ElinDelta.IsApplying) {
            return;
        }

        CoroutineHelper.Deferred(() => {
            if (!EClass.core.IsGameStarted || __instance.Card is not Chara chara) {
                return;
            }

            if (__result is null || __result.owner != __instance) {
                return;
            }

            if (__state is not null &&
                __state.SequenceEqual([__result.vBase, __result.vExp, __result.vPotential, __result.vTempPotential])) {
                return;
            }

            if ((connection.IsHost && chara.IsRemotePlayer) ||
                (connection.IsClient && !chara.IsPC)) {
                return;
            }

            connection.Delta.AddRemote(ElementChangeDelta.Create(chara, __result));
        });
    }
}