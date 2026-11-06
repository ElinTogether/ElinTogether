using ElinTogether.Net;
using HarmonyLib;
using UnityEngine;

namespace ElinTogether.Patches;

/// <summary>
///     Arrived on a map the host handed over, a player lands in front of the stairs it came by: a walk still running
///     from the other map stepped onto them and took it back at once (GitHub issue 10, leaving the Vernis mine). The
///     stairs do not take a player in by a step in the first second after such an arrival; a click still does.
///     Nor by a step that was no step: arriving puts the player on its tile with a move, and its next action (a key
///     step off a teleporter, minutes later) runs the step check on that tile first: the teleporter sent it back,
///     again and again (GitHub issues 76 and 82). A real step comes from the next tile
/// </summary>
[HarmonyPatch(typeof(TraitNewZone), nameof(TraitNewZone.OnStepped))]
internal static class ArrivalStairsPatch
{
    private const float Settle = 1.5f;

    [HarmonyPrefix]
    internal static bool OnStepped(Chara c)
    {
        if (!c.IsPC) {
            return true;
        }

        if (NetSession.Instance.Connection is not null && c.lastPos is { } last &&
            Mathf.Max(Mathf.Abs(last.x - c.pos.x), Mathf.Abs(last.z - c.pos.z)) != 1) {
            EmpLog.Debug("Stairs not taken: the player was put on them, it did not step on them");
            return false;
        }

        if (Time.realtimeSinceStartup - ElinNetClient.ArrivedAt > Settle) {
            return true;
        }

        EmpLog.Debug("Stairs not taken by a step right after arriving on a leased map");
        c.ai?.Cancel();
        return false;
    }
}
