using System.Collections.Generic;
using System.Reflection;
using ElinTogether.Helper;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

[HarmonyPatch]
internal class TraitOnUsePatch
{
    internal static IEnumerable<MethodBase> TargetMethods()
    {
        return [
            AccessTools.Method(typeof(TraitRecycle), nameof(TraitRecycle.OnUse), [typeof(Chara)]),
        ];
    }

    [HarmonyPrefix]
    internal static bool OnRemotePlayerUse(Chara c)
    {
        return !NetSession.Instance.HasActiveConnection || c is not { IsPC: false, IsRemotePlayer: true };
    }
}