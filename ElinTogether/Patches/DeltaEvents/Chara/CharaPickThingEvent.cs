using System.Collections.Generic;
using System.Reflection.Emit;
using ElinTogether.Helper;
using ElinTogether.Models;
using ElinTogether.Net;
using EModding.Helper;
using HarmonyLib;

namespace ElinTogether.Patches;

[HarmonyPatch(typeof(Chara), nameof(Chara.Pick))]
internal static class CharaPickThingEvent
{
    [HarmonyPrefix]
    internal static bool OnCharaPickThingy(Chara __instance, Thing t, ref Thing __result)
    {
        if (NetSession.Instance.Connection is not { } connection) {
            return true;
        }

        if (connection.IsClient && !CardCache.Contains(t)) {
            // pick self without returning null
            __result = t;
            return false;
        }

        if (connection.IsHost && CharaProgressCompleteEvent.IsHappening && CharaProgressCompleteEvent.Chara.IsRemotePlayer) {
            CharaProgressCompleteEvent.DeltaList.Add(new CharaPickThingDelta {
                Owner = CharaProgressCompleteEvent.Chara!,
                Thing = t,
                Pos = null,
                Type = CharaPickThingDelta.PickType.Pick,
            });

            CardCache.KeepAlive(t);

            __result = t;
            return false;
        }

        if (connection.IsClient && PendingUid.IsPending(t.uid)) {
            return true;
        }

        // we are host, propagate to everyone
        // we are client, only propagate ourselves
        if (connection.IsHost || __instance.IsPC) {
            connection.Delta.AddRemote(new CharaPickThingDelta {
                Owner = __instance,
                Thing = t,
                Pos = null,
                Type = CharaPickThingDelta.PickType.Pick,
            });
        }

        return true;
    }
}

[HarmonyPatch(typeof(Chara), nameof(Chara.TryPickGroundItem))]
internal static class CharaTryPickGroundItemEvent
{
    [HarmonyTranspiler]
    internal static IEnumerable<CodeInstruction> OnTryPickGroundItem(IEnumerable<CodeInstruction> instructions)
    {
        return new CodeMatcher(instructions)
            .End()
            .MatchStartBackwards(
                new OperandContains(OpCodes.Callvirt, nameof(Card.IsPC)))
            .EnsureValid("Chara.TryPickGroundItem npc property")
            .SetInstructionAndAdvance(
                Transpilers.EmitDelegate((Chara chara) => chara.IsPlayer))
            .InstructionEnumeration();
    }
}

[HarmonyPatch(typeof(Chara), nameof(Chara.PickOrDrop), typeof(Point), typeof(Thing), typeof(bool))]
internal static class CharaPickOrDropEvent
{
    [HarmonyPrefix]
    internal static bool OnCharaPickOrDrop(Chara __instance, Point p, Thing t)
    {
        if (NetSession.Instance.Connection is not { } connection) {
            return true;
        }

        if (connection.IsClient && !CardCache.Contains(t)) {
            return false;
        }

        if (connection.IsClient || !CharaProgressCompleteEvent.IsHappening || !CharaProgressCompleteEvent.Chara.IsRemotePlayer) {
            return true;
        }

        CharaProgressCompleteEvent.DeltaList.Add(new CharaPickThingDelta {
            Owner = CharaProgressCompleteEvent.Chara!,
            Thing = t,
            Pos = p,
            Type = CharaPickThingDelta.PickType.PickOrDrop,
        });

        CardCache.KeepAlive(t);

        return !CharaProgressCompleteEvent.Chara.IsRemotePlayer;
    }
}

[HarmonyPatch(typeof(Map), nameof(Map.TrySmoothPick), typeof(Point), typeof(Thing), typeof(Chara))]
internal static class CharaTrySmoothPickEvent
{
    [HarmonyPrefix]
    internal static bool OnTrySmoothPick(Point p, Thing t, Chara c)
    {
        if (NetSession.Instance.Connection is not { } connection) {
            return true;
        }

        if (connection.IsClient && !CardCache.Contains(t)) {
            return false;
        }

        if (connection.IsClient || !CharaProgressCompleteEvent.IsHappening || !CharaProgressCompleteEvent.Chara.IsRemotePlayer) {
            return true;
        }

        CharaProgressCompleteEvent.DeltaList.Add(new CharaPickThingDelta {
            Owner = CharaProgressCompleteEvent.Chara!,
            Thing = t,
            Pos = p,
            Type = CharaPickThingDelta.PickType.TrySmoothPick,
        });

        CardCache.KeepAlive(t);

        return !CharaProgressCompleteEvent.Chara.IsRemotePlayer;
    }
}