using ElinTogether.Net;
using System;
using ElinTogether.Helper;
using HarmonyLib;

namespace ElinTogether.Patches;

/// <summary>
///     Players away from the host and the companions travelling with them have no zone in its world on purpose,
///     see ElinNetHostTravel and ElinNetHostCompanions <br />
///     Elin sends any home branch member without a zone back home on load (Game.OnLoad): not those
/// </summary>
[HarmonyPatch]
[AlwaysPatched]
internal static class CompanionLimboPatch
{
    private static bool _loading;

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Game), nameof(Game.OnLoad))]
    internal static void OnLoadStart()
    {
        _loading = true;
    }

    [HarmonyFinalizer]
    [HarmonyPatch(typeof(Game), nameof(Game.OnLoad))]
    internal static Exception? OnLoadEnd(Exception? __exception)
    {
        _loading = false;
        return __exception;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Chara), nameof(Chara.MoveZone), typeof(Zone), typeof(ZoneTransition))]
    internal static bool OnMoveHomeOnLoad(Chara __instance)
    {
        if (!_loading || __instance.IsPC || __instance.currentZone is not null) {
            return true;
        }

        // a client's world is the host's: who is nowhere there (the host itself, when the map it left was
        // inherited by a player) stays nowhere, instead of standing at home as a ghost only this client sees
        if (NetSession.Instance.Transport is ElinNetClient) {
            return false;
        }

        return __instance.CompanionOwnerUid == 0 && !__instance.GetBool("remote_chara");
    }
}
