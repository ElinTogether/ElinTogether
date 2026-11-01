using System.Linq;
using ElinTogether.Elements;
using ElinTogether.Helper;
using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

[HarmonyPatch]
internal class PauseGame
{
    [HarmonyPostfix]
    [HarmonyPatch(typeof(AM_Adv), nameof(AM_Adv.ShouldPauseGame), MethodType.Getter)]
    internal static void OnGetShouldPauseGame(ref bool __result)
    {
        // nobody plays: the world waits for someone (council 2026-10-26, stage 3)
        if (ActionModeCombat.Paused || EmptyWorld.Frozen) {
            __result = true;
            return;
        }

        // our map and our character are with the host, its answer is on its way: nothing moves here meanwhile,
        // see ElinNetClient.IsAwaitingSoftRejoin
        // (or the player keeping the map we stay on is joined, see ElinNetClient.IsAwaitingSoftStay)
        if (NetSession.Instance.Transport is ElinNetClient { IsAwaitingSoftRejoin: true } or ElinNetClient { IsAwaitingSoftStay: true }) {
            __result = true;
            return;
        }

        if (!__result) {
            return;
        }

        // turns just granted to what fights a player have to play out
        if (PlayerCombatTime.HoldsWorld) {
            __result = false;
            return;
        }

        // pause only if all players have no goal and none of them just walked:
        // the world goes on while anyone moves, not only the host (followers, monsters...)
        __result &= (EClass.pc.party?.members ?? [])
            .Where(c => c?.IsRemotePlayer is true)
            .All(c => c.ai is GoalRemote { child: null } && !CharaMoveDelta.HasRecentMove(c));
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(UI), nameof(UI.IsPauseGame), MethodType.Getter)]
    public static void GetIsPauseGame(UI __instance, ref bool __result)
    {
        // menus pause the world only while nobody else plays
        if (NetCompany.HasCompany) {
            __result = false;
        }
    }
}