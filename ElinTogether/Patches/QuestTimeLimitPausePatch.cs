using System;
using ElinTogether.Net;
using HarmonyLib;
using UnityEngine;

namespace ElinTogether.Patches;

/// <summary>
///     GitHub issue 53: the time of a quest zone runs out (a harvest's 180 minutes): ZoneEventQuest.OnTickRound
///     pauses the game with "leave the zone" (<c>pc.MoveZone(_zone.parent)</c>) as what runs when the pause closes.
///     Alone that move is immediate. Here it may wait (the host recalls the town from the player keeping it, a
///     player waits for its lease): the rounds go on, each one pauses again with the same continuation, and the
///     one that closes after the arrival (a key, or the layers cleared by the new scene) reads the zone of the town
///     and sends the player to its parent, the world map: out of the city, the reward left at the quest giver.
///     A continuation made on one map runs on that map only, one pause with it at a time, and none while the move
///     it asked for is on its way
/// </summary>
[HarmonyPatch(typeof(Game), nameof(Game.Pause))]
internal static class QuestTimeLimitPausePatch
{
    // a move refused for good (a land closed to the player) lets the game ask again after that
    private const float LeavingSeconds = 30f;

    private static Zone? _leaving;
    private static float _leavingUntil;

    [HarmonyPrefix]
    internal static bool OnPause(ref Action? onUnpause)
    {
        if (onUnpause is null || NetSession.Instance.Transport is null) {
            return true;
        }

        var zone = EClass._zone;

        // the same pause is still open, or its continuation already runs and the move waits for an answer
        if (EClass.ui.GetLayer<LayerPause>() is not null ||
            (zone == _leaving && EClass.pc?.currentZone == zone && Time.realtimeSinceStartup < _leavingUntil)) {
            return false;
        }

        var then = onUnpause;
        onUnpause = () => {
            if (EClass._zone != zone || EClass.pc?.currentZone != zone) {
                return;
            }

            _leaving = zone;
            _leavingUntil = Time.realtimeSinceStartup + LeavingSeconds;
            then();
        };
        return true;
    }
}
