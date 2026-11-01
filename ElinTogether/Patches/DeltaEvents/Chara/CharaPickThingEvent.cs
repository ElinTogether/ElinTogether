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
    // what Chara.Pick checks before it stores the thing
    private static bool WillStore(Chara chara, Thing t, bool tryStack)
    {
        if (t.parent == chara ||
            (t.trait is TraitCard && t.isNew && EClass.game.config.autoCollectCard && !string.IsNullOrEmpty(t.c_idRefCard))) {
            return true;
        }

        return chara.things.GetDest(t, tryStack).IsValid;
    }

    [HarmonyPrefix]
    internal static bool OnCharaPickThingy(Chara __instance, Thing t, bool tryStack, ref Thing __result)
    {
        // a thing of another team's land stays where it is, whatever picks it (a click, a step on it), see Teams
        if (__instance.IsPC && !ElinDelta.IsApplying && !Teams.CanTake(__instance, t)) {
            Teams.SayBelongs(t);
            __result = t;
            return false;
        }

        // the team bank, for a rank that may not take from it (TeamRanks; the host checks a guest's take again)
        if (!ElinDelta.IsApplying && TeamRanks.RefusesTake(__instance, t)) {
            SE.Beep();
            Msg.Say("emp_rank_denied".lang());
            __result = t;
            return false;
        }

        if (NetSession.Instance.Connection is not { } connection) {
            return true;
        }

        if (connection.IsClient && !CardCache.Contains(t)) {
            // pick self without returning null
            __result = t;
            return false;
        }

        if (CharaProgressCompleteEvent.ShouldPack(true)) {
            CharaProgressCompleteEvent.Pack(new CharaPickThingDelta {
                Owner = CharaProgressCompleteEvent.Chara!,
                Thing = t,
                Pos = null,
                Type = CharaPickThingDelta.PickType.Pick,
                TryStack = tryStack,
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
            // full bag: the game of the player who picks is the one that knows, it keeps the thing on the ground
            // (or drops it at the feet, which Zone.AddCard reports). Sent anyway, the other games stored it by
            // force and gave it back to a bag with no cell left for it
            if (__instance.IsPC && !WillStore(__instance, t, tryStack)) {
                return true;
            }

            connection.Delta.AddRemote(new CharaPickThingDelta {
                Owner = __instance,
                Thing = t,
                Pos = null,
                Type = CharaPickThingDelta.PickType.Pick,
                TryStack = tryStack,
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

        if (!CharaProgressCompleteEvent.ShouldPack(true)) {
            return true;
        }

        CharaProgressCompleteEvent.Pack(new CharaPickThingDelta {
            Owner = CharaProgressCompleteEvent.Chara!,
            Thing = t,
            Pos = p,
            Type = CharaPickThingDelta.PickType.PickOrDrop,
        });

        CardCache.KeepAlive(t);

        return false;
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
            if (LayerDrama.IsActive()) {
                // handed over by a dialog (the casino's free scratch card): on the ground, where the host
                // makes it for real, like a dialog's "drop" (ZoneAddCardEvent, StoryGifts)
                EClass._zone.AddCard(t, p);
            }

            return false;
        }

        if (!CharaProgressCompleteEvent.ShouldPack(true)) {
            return true;
        }

        CharaProgressCompleteEvent.Pack(new CharaPickThingDelta {
            Owner = CharaProgressCompleteEvent.Chara!,
            Thing = t,
            Pos = p,
            Type = CharaPickThingDelta.PickType.TrySmoothPick,
        });

        CardCache.KeepAlive(t);

        return false;
    }
}