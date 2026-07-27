using System;
using ElinTogether.API.SourceValidation;
using ElinTogether.Elements;
using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

[HarmonyPatch(typeof(AIAct), nameof(AIAct.Cancel))]
internal static class CharaTaskCancelEvent
{
    [HarmonyPrefix]
    internal static bool OnCancel(AIAct __instance)
    {
        if (__instance.owner is not { } owner || owner.ai.Current is not AIProgress current) {
            return true;
        }

        var prevent = false;
        var net = NetSession.Instance.Connection;
        switch (net) {
            case ElinNetHost when owner.ai is GoalRemote:
                break;
            case ElinNetClient when owner.IsPC:
                // client can only cancel progress with delta
                prevent = true;
                break;
            case ElinNetClient when owner.ai is GoalRemote:
                return false;
            default:
                return true;
        }

        if (current.parent?.GetType() is not { } actType ||
            !ActMappingValidator.Default.ActToIdMapping.TryGetValue(actType, out var actId)) {
            return true;
        }

        net.Delta.AddRemote(new CharaTaskCancelDelta {
            Owner = owner,
            ActId = actId,
        });

        return !prevent;
    }

    extension(AIAct aIAct)
    {
        [HarmonyReversePatch(HarmonyReversePatchType.Snapshot)]
        internal AIAct.Status Stub_Cancel()
        {
            throw new NotImplementedException("AIAct.Cancel");
        }
    }
}