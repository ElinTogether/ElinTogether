using System.Collections.Generic;
using System.Reflection;
using ElinTogether.API.SourceValidation;
using ElinTogether.Elements;
using ElinTogether.Helper;
using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

[HarmonyPatch]
internal static class CharaTaskProgressEvents
{
    internal static IEnumerable<MethodBase> TargetMethods()
    {
        return OverrideMethodComparer
            .FindAllOverrides(typeof(AIProgress), nameof(AIProgress.OnProgressBegin));
    }

    [HarmonyPrefix]
    internal static void OnProgressBegin(AIProgress __instance)
    {
        if (NetSession.Instance.Connection is not { } connection) {
            return;
        }

        if (__instance.owner is not { } owner) {
            return;
        }

        if (__instance.parent?.GetType() is not { } actType ||
            !ActMappingValidator.Default.ActToIdMapping.TryGetValue(actType, out var actId)) {
            return;
        }

        if (__instance is not DelegateProgress) {
            if (connection.IsClient) {
                // we can only complete remote progress with delta
                __instance.progress = HeldProgress.Held;
            }

            // for host, run it only when remote players run it
            if (owner.ai is GoalRemote) {
                __instance.progress = HeldProgress.Held;
                return;
            }
        }

        connection.Delta.AddRemote(new CharaProgressBeginDelta {
            Owner = owner,
            Pos = owner.pos,
            MaxProgress = __instance.MaxProgress,
            ActId = actId,
        });
    }
}