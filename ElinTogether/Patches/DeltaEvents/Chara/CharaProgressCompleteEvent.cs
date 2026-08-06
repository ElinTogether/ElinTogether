using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ElinTogether.API.SourceValidation;
using ElinTogether.Elements;
using ElinTogether.Helper;
using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

[HarmonyPatch]
internal static class CharaProgressCompleteEvent
{
    internal static List<ElinDelta> DeltaList = [];
    internal static Chara? Chara { get; private set; }
    internal static bool IsHappening { get; private set; }
    internal static AIAct? Action { get; private set; }

    internal static IEnumerable<MethodBase> TargetMethods()
    {
        return OverrideMethodComparer
            .FindAllOverrides(typeof(AIAct), nameof(AIAct.OnProgressComplete))
            .Where(mi => typeof(AIProgress).IsAssignableFrom(mi.DeclaringType) || mi.DeclaringType == typeof(TaskBuild));
    }

    [HarmonyPrefix]
    internal static bool OnProgressComplete(AIAct __instance)
    {
        if (NetSession.Instance.Connection is not { } connection || __instance.owner is null) {
            return true;
        }

        Chara = __instance.owner;
        Action = __instance;
        IsHappening = true;

        if (__instance is not TaskBuild taskBuild) {
            return true;
        }

        if (Chara.IsPC && !ElinDelta.IsApplying) {
            SendCharaBuildDelta(taskBuild);
        }

        return connection.IsHost || ElinDelta.IsApplying;
    }

    [HarmonyPostfix]
    internal static void OnProgressCompleteEnd(AIAct __instance)
    {
        Chara = null;
        Action = null;
        IsHappening = false;

        var captured = DeltaList;
        DeltaList = [];

        if (__instance.owner is null) {
            return;
        }

        if (__instance is TaskBuild) {
            if (NetSession.Instance.Connection is ElinNetHost buildHost) {
                foreach (var delta in captured) {
                    buildHost.Delta.AddRemote(delta);
                }
            }

            return;
        }

        // only host can complete progress
        if (NetSession.Instance.Connection is not ElinNetHost host || __instance is DelegateProgress) {
            return;
        }

        if (__instance.parent?.GetType() is not { } actType ||
            !ActMappingValidator.Default.ActToIdMapping.TryGetValue(actType, out var actId)) {
            return;
        }

        // due to randomness in max progress
        // remote needs to be notified that a remote task is completed before starting anew
        host.Delta.AddRemote(new CharaProgressCompleteDelta {
            Owner = __instance.owner,
            CompletedActId = actId,
            DeltaList = captured,
        });
    }

    internal static void SendCharaBuildDelta(TaskBuild taskBuild)
    {
        if (taskBuild.held is null) {
            return;
        }

        NetSession.Instance.Connection!.Delta.AddRemote(new CharaBuildDelta {
            Held = taskBuild.held,
            Owner = taskBuild.owner,
            Pos = taskBuild.pos,
            Dir = taskBuild.recipe._dir,
            Altitude = taskBuild.altitude,
            BridgeHeight = taskBuild.bridgeHeight,
        });
    }
}