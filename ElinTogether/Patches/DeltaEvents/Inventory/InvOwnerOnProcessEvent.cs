using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ElinTogether.Helper;
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

        // effect draglet is a fake inv
        if (__instance is InvOwnerEffect effect) {
            OnProcessEffect(connection, effect, t);
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

    private static void OnProcessEffect(ElinNetBase connection, InvOwnerEffect effect, Thing t)
    {
        // client wait for host replay
        if (connection.IsClient && ElinDelta.IsApplying) {
            return;
        }

        EffectId effectId;
        string? refId = null;
        Thing? consume = null;
        switch (effect) {
            case InvOwnerIdentify identify:
                effectId = identify.superior ? EffectId.GreaterIdentify : EffectId.Identify;
                break;
            case InvOwnerEnchant enchant:
                effectId = enchant.armor
                    ? enchant.superior ? EffectId.EnchantArmorGreat : EffectId.EnchantArmor
                    : enchant.superior ? EffectId.EnchantWeaponGreat : EffectId.EnchantWeapon;
                break;
            case InvOwnerChangeMaterial material:
                effectId = material.idEffect;
                refId = material.mat?.alias;
                consume = material.consume;
                break;
            case InvOwnerChangeRarity rarity:
                effectId = rarity.idEffect;
                consume = rarity.consume;
                break;
            case InvOwnerUncurse:
                effectId = EffectId.Uncurse;
                break;
            case InvOwnerLighten:
                effectId = EffectId.Lighten;
                break;
            case InvOwnerReconstruction:
                effectId = EffectId.Reconstruction;
                break;
            default:
                return;
        }

        // pending uids don't leave client
        var remote = PendingSplit.Split(t);
        if (connection.IsClient && PendingUid.IsPending(remote.Uid)) {
            EmpLog.Warning("Refusing effect process of pending thing {Uid}", t.uid);
            return;
        }

        var thing = remote.Uid == t.uid ? t : CardCache.Find(remote.Uid) as Thing;
        if (thing is null || (connection.IsClient && !CardCache.Contains(thing))) {
            return;
        }

        if (consume is not null && (consume.isDestroyed || (connection.IsClient && !CardCache.Contains(consume)))) {
            consume = null;
        }

        connection.Delta.AddRemote(new InvOwnerOnProcessDelta {
            Parent = thing.parent as Card,
            Thing = remote,
            Dest = null,
            OwnerType = InvOwnerOnProcessDelta.RemoteInvOwnerType.Effect,
            Effect = (int)effectId,
            EffectPower = effect.power,
            EffectState = (int)effect.state,
            EffectRefId = refId,
            Consume = consume,
        });
    }
}

[HarmonyPatch]
internal static class InvOwnerEffectEvent
{
    internal static IEnumerable<MethodBase> TargetMethods()
    {
        return OverrideMethodComparer
            .FindAllOverrides(typeof(InvOwnerDraglet), nameof(InvOwnerDraglet._OnProcess), typeof(Thing))
            .Where(mi => typeof(InvOwnerEffect).IsAssignableFrom(mi.DeclaringType));
    }

    [HarmonyPrefix]
    internal static bool OnEffectProcess()
    {
        // client wait for delta
        return NetSession.Instance.Connection is not { IsClient: true } || ElinDelta.IsApplying;
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