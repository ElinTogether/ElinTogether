using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

/// <summary>
///     Host setting "No cheats" (Workshop comment 2026-11-20: "Players can enable console and spawn items. I did it in
///     the public server"). The game's console takes the command Cheat from anyone (CoreDebug.Cheat sets
///     game.config.cheat, saved with the world), then Spawn, SetElement and the other cheats run behind
///     CoreDebug.CheatEnabled. With the rule on, in every game of the session (the host's too), CheatEnabled says no
///     and Cheat does nothing. Not covered: a modified game that makes its things another way
/// </summary>
[HarmonyPatch]
internal static class NoCheatsPatch
{
    // (a player away holding its own map is the host of that zone session: the world host's rules still count)
    internal static bool On => NetSession.Instance.Transport is ElinNetClient
        ? NetSession.Instance.Rules.NoCheats
        : NetSession.Instance.Connection is ElinNetHost && EmpConfig.Server.NoCheats.Value;

    [HarmonyPrefix]
    [HarmonyPatch(typeof(CoreDebug), nameof(CoreDebug.CheatEnabled))]
    internal static bool OnCheatEnabled(ref bool __result)
    {
        // the owner's admin character of the public world keeps the game's cheats (Helper.OwnerAdmin)
        if (!On || Helper.OwnerAdmin.IsMe) {
            return true;
        }

        __result = false;
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(CoreDebug), nameof(CoreDebug.Cheat))]
    internal static bool OnCheat(ref string __result)
    {
        if (!On || Helper.OwnerAdmin.IsMe) {
            return true;
        }

        __result = "emp_no_cheats".lang();
        return false;
    }
}
