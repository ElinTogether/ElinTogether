using System.Collections.Generic;
using System.Reflection;
using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

[HarmonyPatch]
internal static class InvOwnerOnProcessEvent
{
    internal static IEnumerable<MethodBase> TargetMethods()
    {
        return [
            AccessTools.Method(typeof(InvOwnerDraglet), nameof(InvOwnerDraglet.OnProcess)),
            AccessTools.Method(typeof(InvOwnerHotbar), nameof(InvOwnerHotbar.OnProcess)),
        ];
    }

    [HarmonyPrefix]
    internal static void OnProcess(InvOwner __instance, Thing t)
    {
        if (NetSession.Instance.Connection is not { } connection) {
            return;
        }

        // crafter
        if (__instance is InvOwnerCraft) {
            return;
        }

        var refuel = __instance is InvOwnerRefuel;

        // host refuels locally, wait for CardChargeDelta
        if (connection.IsHost && refuel) {
            return;
        }

        // pending uids don't leave client
        var remote = PendingSplit.Split(t);
        if (connection.IsClient && PendingUid.IsPending(remote.Uid)) {
            EmpLog.Warning("Refusing on-process of pending thing {Uid}", remote.Uid);
            return;
        }

        var thing = remote.Uid == t.uid ? t : CardCache.Find(remote.Uid) as Thing;
        if (thing is null) {
            return;
        }

        if (connection.IsClient && !CardCache.Contains(thing)) {
            return;
        }

        var parent = thing.parent as Card;
        if (parent is null && !refuel) {
            return;
        }

        connection.Delta.AddRemote(new InvOwnerOnProcessDelta {
            Parent = parent,
            Thing = remote,
            Dest = __instance.owner,
            OwnerType = refuel
                ? InvOwnerOnProcessDelta.RemoteInvOwnerType.Refuel
                : InvOwnerOnProcessDelta.RemoteInvOwnerType.Unknown,
        });
    }
}

[HarmonyPatch(typeof(InvOwnerRefuel), nameof(InvOwnerRefuel._OnProcess))]
internal static class InvOwnerRefuelEvent
{
    [HarmonyPrefix]
    internal static bool OnRefuel()
    {
        // client refuel is simulated by CardChargeDelta
        return NetSession.Instance.Connection is not { IsClient: true } || ElinDelta.IsApplying;
    }
}

[HarmonyPatch(typeof(Trait), nameof(Trait.TryRefuel))]
internal static class TraitTryRefuelEvent
{
    [HarmonyPrefix]
    internal static bool OnTryRefuel()
    {
        // LayerDragGrid IsFuelEnough
        return NetSession.Instance.Connection is not { IsClient: true } || ElinDelta.IsApplying;
    }
}