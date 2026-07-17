using System.Collections.Generic;
using System.Reflection;
using ElinTogether.Helper;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

[HarmonyPatch]
internal class TraitOnReadPatch
{
    internal static IEnumerable<MethodBase> TargetMethods()
    {
        return [
            AccessTools.Method(typeof(TraitNewspaper), nameof(Trait.OnRead)),
            AccessTools.Method(typeof(TraitBook), nameof(Trait.OnRead)),
            AccessTools.Method(typeof(TraitStoryBook), nameof(Trait.OnRead)),
            AccessTools.Method(typeof(TraitStoryBookHome), nameof(Trait.OnRead)),
            AccessTools.Method(typeof(TraitBlueprint), nameof(Trait.OnRead)),
            AccessTools.Method(typeof(TraitDeed), nameof(Trait.OnRead)),
            AccessTools.Method(typeof(TraitDeedRelocate), nameof(Trait.OnRead)),
        ];
    }

    [HarmonyPrefix]
    internal static bool OnRemotePlayerRead(Chara c)
    {
        return !NetSession.Instance.HasActiveConnection || c is not { IsPC: false, IsRemotePlayer: true };
    }
}