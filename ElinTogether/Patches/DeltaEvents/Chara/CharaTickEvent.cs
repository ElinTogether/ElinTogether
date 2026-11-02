using System;
using System.Collections.Generic;
using System.Reflection;
using ElinTogether.Elements;
using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

[HarmonyPatch(typeof(Chara), nameof(Chara.Tick))]
internal static class CharaTickEvent
{
    internal static int LastTick { get; private set; }

    [HarmonyPrefix]
    internal static bool OnCharaTick(Chara __instance)
    {
        if (NetSession.Instance.Connection is not { } connection) {
            return true;
        }

        // when any player tick, it should tick the host world
        // which should relay to all players again
        if (__instance.ai is GoalRemote) {
            return false;
        }

        // clients joined before assigning GoalRemote
        if (connection.IsClient && !__instance.IsPC) {
            return false;
        }

        connection.Delta.AddRemote(new CharaTickDelta {
            Owner = __instance,
        });

        LastTick = NetSession.Instance.Tick;

        return true;
    }

    extension(Chara chara)
    {
        /// <summary>
        ///     Another player's turn played on its copy here. The snapshot lacks CharaSynchronizationContext's pace
        ///     (patched later, see SnapshotStub): the copy took the game's actTime, measured on this game's player's
        ///     speed, and its steps were drawn slower or faster than they are walked. Applied to this copy too
        /// </summary>
        [HarmonyReversePatch(HarmonyReversePatchType.Snapshot)]
        internal void Stub_Tick()
        {
            IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
            {
                return SnapshotStub.With(instructions, original, CharaSynchronizationContext.OnCharaTick);
            }

            _ = Transpiler([], null!);
            throw new NotImplementedException("Chara.Tick");
        }
    }
}