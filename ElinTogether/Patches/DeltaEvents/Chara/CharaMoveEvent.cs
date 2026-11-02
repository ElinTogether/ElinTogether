using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using ElinTogether.Elements;
using ElinTogether.Helper;
using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

[HarmonyPatch(typeof(Chara), nameof(Chara._Move))]
internal static class CharaMoveEvent
{
    [HarmonyPrefix]
    internal static bool OnCharaMove(Chara __instance)
    {
        return __instance.ai is not GoalRemote && !__instance.IsActiveRemoteChara;
    }

    [HarmonyPostfix]
    internal static void OnCharaMoveEnd(Chara __instance, Card.MoveResult __result, Point newPoint, ref Card.MoveType type)
    {
        if (__result == Card.MoveResult.Fail) {
            return;
        }

        var session = NetSession.Instance;
        if (session.Connection is not { } connection) {
            return;
        }

        // we are host, everyone should generate a delta
        // also relay the client move to other clients

        // we are client, only ourselves should generate a delta
        // ignore all other relayed moves

        if (__instance.ai is GoalRemote || __instance.IsActiveRemoteChara) {
            return;
        }

        // a client tells its own steps only: the game also moves others in it (the leash tugs its leashed
        // companions at each step of "the player"), and those moves, sent, put the host's companion back where this
        // game had it (the guest's leashed cat that did not follow, 2026-10-22)
        if (connection.IsClient && !__instance.IsPC) {
            return;
        }

        // movement could be random in certain circumstances
        // not really a big problem if everyone is moving
        // simply ignore and wait for reconciliation or next delta

        connection.Delta.AddRemote(new CharaMoveDelta {
            Owner = __instance,
            Pos = newPoint,
            MoveType = type,
            ZoneUid = session.CurrentZone?.uid ?? -1,
        });
    }

    extension(Chara chara)
    {
        /// <summary>
        ///     A step another game made, played here (and a placement, <see cref="Place" />). The snapshot lacks the
        ///     transpilers of classes patched after this one (see SnapshotStub): a guest's step on the host still
        ///     swapped places with the companion or summon it walked into (RemoteNoPushPatch: nobody else does, the
        ///     host walks onto their tile), and an own placement took the game's actTime. Both are applied to this
        ///     copy. RemoteTravelRegionPatch is left out on purpose: with the local player the copy only places it
        ///     (an arrival, a resync), and its StepDate would have the hours of a world-map step passed
        /// </summary>
        [HarmonyReversePatch(HarmonyReversePatchType.Snapshot)]
        internal Card.MoveResult Stub_Move(Point point, Card.MoveType type)
        {
            IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
            {
                SnapshotStub.Without(original, RemoteTravelRegionPatch.OnRegionTravelIl);
                return NoWorldMapStep(SnapshotStub.With(instructions, original, RemoteNoPushPatch.OnCharaMoveByForceIl,
                    CharaSynchronizationContext.OnCharaMove));
            }

            _ = Transpiler([], null!);
            throw new NotImplementedException("Chara.Move");
        }

        /// <summary>
        ///     Put on a tile as the game puts a character that arrives: a step would pick up what lies there (Chara._Move,
        ///     the player's footstep), which an arrival, a soft rejoin or a map reload never does
        /// </summary>
        internal Card.MoveResult Place(Point point)
        {
            var lying = point.IsValid ? point.Things.FindAll(t => !t.ignoreAutoPick) : [];
            lying.ForEach(t => t.ignoreAutoPick = true);
            try {
                return chara.Stub_Move(point, Card.MoveType.Force);
            } finally {
                lying.ForEach(t => t.ignoreAutoPick = false);
            }
        }
    }

    /// <summary>
    ///     The copy reaches the game's "the player steps on the world map" block (the hours, a turn of conditions per
    ///     minute for the party, the abort when hurt) only with the local player, from Place: an arrival on the world
    ///     map (joining the host there, a soft rejoin, a resync) paid a whole world-map step, some 120 turns of
    ///     hunger, buffs and poison for its character, each one sent to the host. The block is skipped in the copy
    /// </summary>
    private static IEnumerable<CodeInstruction> NoWorldMapStep(IEnumerable<CodeInstruction> instructions)
    {
        var currentZone = AccessTools.PropertyGetter(typeof(Chara), nameof(Chara.currentZone));
        var isRegion = AccessTools.PropertyGetter(typeof(Spatial), nameof(Spatial.IsRegion));
        var list = instructions.ToList();
        var at = list.FindIndex(1, c => c.Calls(isRegion));
        if (at < 0 || !list[at - 1].Calls(currentZone)) {
            EmpLog.Warning("Chara._Move: no currentZone.IsRegion, a placement on the world map still pays a step");
            return list;
        }

        // (the labels stay where they are: a branch may land on the first)
        list.InsertRange(at + 1, new[] { new CodeInstruction(OpCodes.Pop), new CodeInstruction(OpCodes.Ldc_I4_0) });
        return list;
    }
}