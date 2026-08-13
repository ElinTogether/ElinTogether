using System.Collections.Generic;
using System.Reflection;
using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

[HarmonyPatch]
internal static class TraitUserPatch
{
    internal static IEnumerable<MethodBase> TargetMethods()
    {
        return [
            AccessTools.Method(typeof(TraitMaterialHammer), nameof(TraitMaterialHammer.OnUse), [typeof(Chara)]),
            AccessTools.Method(typeof(TraitGarokkHammer), nameof(TraitGarokkHammer.OnUse), [typeof(Chara)]),
        ];
    }

    [HarmonyPrefix]
    internal static bool OnHammerUse(Chara c, ref bool __result)
    {
        if (NetSession.Instance.Connection is null || !ElinDelta.IsApplying || c.IsPC) {
            return true;
        }

        __result = true;
        return false;
    }
}

[HarmonyPatch(typeof(TraitShrine), nameof(TraitShrine._OnUse))]
internal static class TraitShrineUseEvent
{
    [HarmonyPrefix]
    internal static bool OnShrineUse(TraitShrine __instance, Chara c)
    {
        if (NetSession.Instance.Connection is null || !ElinDelta.IsApplying || c.IsPC) {
            return true;
        }

        return __instance.Shrine.id is not ("armor" or "material");
    }
}