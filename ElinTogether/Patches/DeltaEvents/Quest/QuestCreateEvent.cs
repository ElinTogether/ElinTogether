using System.Collections.Generic;
using System.Linq;
using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

[HarmonyPatch]
internal static class QuestCreateEvent
{
    [HarmonyPrefix]
    [HarmonyPatch(typeof(Zone), nameof(Zone.UpdateQuests))]
    internal static bool OnUpdateQuests(Zone __instance, bool force, out HashSet<int>? __state)
    {
        // before: which offers the residents hold, to see afterwards whether the draw took some away
        // (at the activation of a map nothing is said: the map is sent whole then)
        var session = NetSession.Instance;
        __state = session is { IsHost: true, Connection: not null } && !ElinDelta.IsApplying &&
                  !ZoneActivateEvent.IsHappening && __instance == EClass._zone && __instance.map is not null
            ? Offers(__instance).ToHashSet()
            : null;
        // the "Reroll Quests" button of the board in a client's game: the game that keeps the map draws them
        // (the button spent the influence here and drew nothing)
        // and its free draw (the board opened, LayerQuestBoard): the day's new offers are drawn there too, else a
        // guest on the host's map saw only the offers of the host's last own look at the board. On arrival the
        // request of the visit asks for it (ZoneVisitEvent)
        if (!ElinDelta.IsApplying && session.Connection is ElinNetClient client && __instance == EClass._zone &&
            (force || !ZoneVisitEvent.IsVisiting)) {
            client.Delta.AddRemote(new QuestRerollDelta {
                ZoneUid = __instance.uid,
                Kind = force ? QuestRerollDelta.Reroll : QuestRerollDelta.Board,
            });
        }

        return session.IsHost;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Zone), nameof(Zone.UpdateQuests))]
    internal static void OnUpdateQuestsEnd(Zone __instance, HashSet<int>? __state)
    {
        if (__state is null || NetSession.Instance.Connection is not { } connection) {
            return;
        }

        // nothing taken away: the creations alone tell it
        var kept = Offers(__instance).ToArray();
        if (__state.All(kept.Contains)) {
            return;
        }

        connection.Delta.AddRemote(new QuestOffersDelta {
            ZoneUid = __instance.uid,
            Kept = kept,
        });
    }

    private static IEnumerable<int> Offers(Zone zone)
    {
        return zone.map.charas.Concat(zone.map.deadCharas)
            .Where(c => c.quest is not null && !EClass.game.quests.list.Contains(c.quest))
            .Select(c => c.quest.uid);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Quest), nameof(Quest.Create))]
    internal static void OnCreate(Quest __result)
    {
        if (NetSession.Instance.Connection is not { } connection) {
            return;
        }

        if (connection is ElinNetClient) {
            __result.uid = -__result.uid;
            return;
        }

        if (ZoneActivateEvent.IsHappening) {
            return;
        }

        connection.Delta.AddRemote(QuestCreateDelta.Create(__result));
    }
}