using ElinTogether.Helper;
using HarmonyLib;

namespace ElinTogether.Patches;

/// <summary>
///     The owner's admin character of the public world (Helper.OwnerAdmin) takes no damage and cannot die, in the
///     game that keeps its map and in its own (as the server's stand-in, ServerAvatarPatch: first, so no blow or
///     death is told to the others), and its own game keeps it at full strength
/// </summary>
[HarmonyPatch]
internal static class OwnerAdminPatch
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    [HarmonyPatch(typeof(Card), nameof(Card.DamageHP), typeof(long), typeof(int), typeof(int), typeof(AttackSource), typeof(Card),
        typeof(bool), typeof(Thing), typeof(Chara), typeof(int))]
    internal static bool OnDamageHP(Card __instance)
    {
        return __instance is not Chara chara || !OwnerAdmin.Is(chara);
    }

    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    [HarmonyPatch(typeof(Chara), nameof(Chara.Die))]
    internal static bool OnDie(Chara __instance)
    {
        return !OwnerAdmin.Is(__instance);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Chara), nameof(Chara.Tick))]
    internal static void OnTick(Chara __instance)
    {
        // (every character ticks: the name first, it is the cheap part)
        if (__instance.IsPC && __instance.c_altName == OwnerAdmin.CharaName && OwnerAdmin.Is(__instance)) {
            OwnerAdmin.Keep(__instance);
        }
    }
}
