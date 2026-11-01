using System.Collections.Generic;
using System.Reflection;
using ElinTogether.Helper;
using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

[HarmonyPatch]
internal static class CharaLevelEvent
{
    internal static IEnumerable<MethodBase> TargetMethods()
    {
        return [
            AccessTools.PropertySetter(typeof(Card), nameof(Card.LV)),
            AccessTools.PropertySetter(typeof(Card), nameof(Card.exp)),
        ];
    }

    [HarmonyPrefix]
    internal static void OnSetLevelState(Card __instance, out (int Lv, int Exp) __state)
    {
        __state = (__instance.LV, __instance.exp);
    }

    // a client tells its own character; the host tells the companions it simulates for a player
    [HarmonyPostfix]
    internal static void OnSetLevelEnd(Card __instance, (int Lv, int Exp) __state)
    {
        if (__instance is not Chara chara || (chara.LV == __state.Lv && chara.exp == __state.Exp)) {
            return;
        }

        switch (NetSession.Instance.Connection) {
            case ElinNetClient when chara.IsPC:
            case ElinNetHost when OwnedCompanion(chara):
                NetSession.Instance.Connection.Delta.AddRemote(new CharaLevelDelta {
                    Owner = chara,
                    Level = chara.LV,
                    Exp = chara.exp,
                });
                break;
        }
    }

    /// <summary>
    ///     A player's companion simulated by the host (on the host map): its owner's copy only learnt the levels and
    ///     exp gained here from the next world copy, and when the owner went on a trip alone first, the stale copy
    ///     it brought back replaced the host's (levels and feat points lost)
    /// </summary>
    internal static bool OwnedCompanion(Chara chara)
    {
        // (another player's: the host's own companions are only the host's game's)
        if (!NetCompany.HasCompany || !EClass.core.IsGameStarted || chara.IsPC || chara.IsRemotePlayer) {
            return false;
        }

        var owner = chara.CompanionOwnerUid is var uid and not 0 ? uid : CompanionHelper.OwnerOf(chara)?.uid ?? 0;
        return owner != 0 && owner != EClass.pc.uid;
    }
}

[HarmonyPatch(typeof(Card), nameof(Card.AddExp))]
internal static class CardAddExpMirrorGuard
{
    [HarmonyPrefix]
    internal static bool OnAddExp(Card __instance)
    {
        if (NetSession.Instance.Connection is null) {
            return true;
        }

        return __instance is not Chara { IsRemotePlayer: true };
    }
}

[HarmonyPatch(typeof(Card), nameof(Card.LevelUp))]
internal static class CardLevelUpMirrorGuard
{
    [HarmonyPrefix]
    internal static bool OnLevelUp(Card __instance)
    {
        if (NetSession.Instance.Connection is null) {
            return true;
        }

        return __instance is not Chara { IsRemotePlayer: true };
    }
}