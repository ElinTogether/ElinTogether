using System;
using System.Collections.Generic;
using System.Reflection;
using ElinTogether.Helper;
using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;
using UnityEngine;

namespace ElinTogether.Patches;

[HarmonyPatch]
internal class ElementChangedEvent
{
    private static int _dedupeFrame;
    private static readonly HashSet<(int Uid, int ElementId)> _createdInCurrentFrame = [];

    private static bool CreatedInCurrentFrame(int uid, int elementId)
    {
        if (Time.frameCount != _dedupeFrame) {
            _dedupeFrame = Time.frameCount;
            _createdInCurrentFrame.Clear();
        }

        return _createdInCurrentFrame.Add((uid, elementId));
    }

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
    internal static void OnSyncElementChange(ElementContainer __instance, int id, Element? __result, int[]? __state)
    {
        if (NetSession.Instance.Connection is not { } connection || ElinDelta.IsApplying) {
            return;
        }

        CoroutineHelper.Deferred(() => {
            if (!EClass.core.IsGameStarted || __instance.Card is not Chara chara) {
                return;
            }

            if (__result is null) {
                return;
            }

            var unowned = (connection.IsHost && chara.IsRemotePlayer) ||
                          (connection.IsClient && !chara.IsPC);

            if (__result.owner != __instance) {
                if (__result.owner is null && __state is not null && !unowned && CreatedInCurrentFrame(chara.uid, id)) {
                    EmpLog.Debug("Element {ElementId} removed on chara {Uid}",
                        id, chara.uid);
                    connection.Delta.AddRemote(new ElementChangeDelta {
                        Owner = chara,
                        Element = id,
                        Value = [0, 0, 0, 0],
                    });
                }
                return;
            }

            int[] current = [__result.vBase, __result.vExp, __result.vPotential, __result.vTempPotential];
            if (__state?.SequenceEqual(current) ?? current is [0, 0, 0, 0]) {
                return;
            }

            if (unowned) {
                EmpLog.Debug("Local element {ElementId} change on unowned chara {Uid}: {ElementValues}",
                    id, chara.uid, current);
                return;
            }

            if (!CreatedInCurrentFrame(chara.uid, id)) {
                return;
            }

            EmpLog.Debug("Element {ElementId} changed on chara {Uid}: {ElementValues}",
                id, chara.uid, current);
            connection.Delta.AddRemote(ElementChangeDelta.Create(chara, __result));
        });
    }
}