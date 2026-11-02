using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ElinTogether.Helper;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

/// <summary>
///     The zone of a quest is run by the game simulating it: the host, or the player holding that zone (alone
///     there it reads as host, see NetSession.Connection). A player who came along with the one who took the
///     quest does not have it in its log (ZoneEventQuest.quest is null there): nothing of it is its own to run,
///     to show or to settle
/// </summary>
[HarmonyPatch]
internal static class QuestZoneVisitorPatch
{
    /// <summary>
    ///     The local player is in the zone of a quest someone else took
    /// </summary>
    internal static bool IsVisitor(int uidQuest)
    {
        return uidQuest != 0 && PersonalQuests.Enabled && NetSession.Instance.Transport is ElinNetClient &&
               EClass.game.quests.Get(uidQuest) is null;
    }

    /// <summary>
    ///     The local player is in the zone of a quest that is not settled by this game: someone else's, or its
    ///     own while the host who came along runs the zone (the host says how it went, see
    ///     ElinNetHost.LeaveAccompaniedZone). The game moving the player out must not weigh, search or settle
    /// </summary>
    internal static bool IsSettledElsewhere(int uidQuest)
    {
        return IsVisitor(uidQuest) ||
               (uidQuest != 0 && PersonalQuests.InstancesEnabled && NetSession.Instance.Connection is ElinNetClient);
    }

    /// <summary>
    ///     On the host, in the zone of a quest it came along to: the quest is its taker's, kept in no quest log
    ///     here. What the zone reads of it (how hard, how much is delivered) is what that player last told
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(QuestManager), nameof(QuestManager.Get), typeof(int))]
    internal static void OnGetQuest(int uid, ref Quest? __result)
    {
        // nothing of this outside of a session hosted here
        if (NetSession.Instance.Transport is not ElinNetHost host) {
            return;
        }

        __result ??= host.AccompaniedQuest(uid);
    }

    /// <summary>
    ///     The overrides of a ZoneEvent method written by the events of quests (subdue, harvest, defense...)
    /// </summary>
    internal static IEnumerable<MethodBase> QuestEventOverrides(string methodName)
    {
        return OverrideMethodComparer.FindAllOverrides(typeof(ZoneEvent), methodName)
            .Where(m => typeof(ZoneEventQuest).IsAssignableFrom(m.DeclaringType));
    }

    /// <summary>
    ///     Waves, aggro, the time limit: only where the zone is simulated
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(ZoneEventQuest), nameof(ZoneEventQuest.OnTickRound))]
    internal static bool OnTickRound()
    {
        return NetSession.Instance.IsHost;
    }

    /// <summary>
    ///     Leaving, with the taker or alone before the end: the quest is settled by its taker's game only. <br />
    ///     Not a check on who simulates: a visitor going back to town alone already holds that town when it moves
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(ZoneInstanceRandomQuest), nameof(ZoneInstanceRandomQuest.OnLeaveZone))]
    internal static bool OnLeaveInstance(ZoneInstanceRandomQuest __instance)
    {
        return !IsSettledElsewhere(__instance.uidQuest);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(ZoneEventHarvest), nameof(ZoneEventHarvest.TextWidgetDate), MethodType.Getter)]
    internal static bool OnHarvestText(ZoneEventHarvest __instance, ref string __result)
    {
        return HasQuest(__instance, ref __result);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(ZoneEventMusic), nameof(ZoneEventMusic.TextWidgetDate), MethodType.Getter)]
    internal static bool OnMusicText(ZoneEventMusic __instance, ref string __result)
    {
        return HasQuest(__instance, ref __result);
    }

    private static bool HasQuest(ZoneEventQuest zoneEvent, ref string result)
    {
        if (zoneEvent.quest is not null) {
            return true;
        }

        result = "";
        return false;
    }
}

/// <summary>
///     What a quest puts in its zone on arrival (monsters, crops, guests) is put there once, where the zone is
///     simulated; the others receive it with the map
/// </summary>
[HarmonyPatch]
internal static class QuestZoneVisitPatch
{
    internal static IEnumerable<MethodBase> TargetMethods()
    {
        return QuestZoneVisitorPatch.QuestEventOverrides(nameof(ZoneEvent.OnVisit));
    }

    [HarmonyPrefix]
    internal static bool OnVisit()
    {
        return NetSession.Instance.IsHost;
    }
}

/// <summary>
///     What a quest decides on the way out (a harvest weighed and the stolen crops taken back, wedding guests
///     sent home) is decided by its taker's game: there the bags of the whole party are searched, the visitor's
///     included
/// </summary>
[HarmonyPatch]
internal static class QuestZoneLeavePatch
{
    internal static IEnumerable<MethodBase> TargetMethods()
    {
        return QuestZoneVisitorPatch.QuestEventOverrides(nameof(ZoneEvent.OnLeaveZone));
    }

    [HarmonyPrefix]
    internal static bool OnLeaveZone(ZoneEvent __instance)
    {
        return __instance is not ZoneEventQuest quest || !QuestZoneVisitorPatch.IsSettledElsewhere(quest.uidQuest);
    }
}

/// <summary>
///     The game fails the quest of a zone when the local player dies in it. Where the host came along to a
///     player's quest, that is the death of that player, not the host's
/// </summary>
[HarmonyPatch(typeof(Chara), nameof(Chara.Die))]
internal static class QuestZoneDeathPatch
{
    // First: CharaDieEvent's prefix (patched before this class) plays a death caused by a guest's delta right there
    // (Stub_Die); after it, the status read here was already the failure the death set
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    internal static void OnDie(out ZoneInstance.Status? __state)
    {
        __state = null;

        // nothing of this outside of a session hosted here
        if (NetSession.Instance.Transport is not ElinNetHost) {
            return;
        }

        __state = (EClass.game?.activeZone?.instance as ZoneInstanceRandomQuest)?.status;
    }

    [HarmonyPostfix]
    internal static void OnDied(Chara __instance, ZoneInstance.Status? __state)
    {
        if (NetSession.Instance.Transport is not ElinNetHost host) {
            return;
        }

        if (__state is { } before) {
            host.OnDeathInQuestZone(__instance, before);
        }
    }
}

/// <summary>
///     The save of the host never holds the quest of another player: while it is written, the zone the host
///     came along to is a plain one (no quest event, nothing to settle on the way out), which a game loading it
///     later, with or without this mod, walks out of without looking for a quest it does not have
/// </summary>
[HarmonyPatch(typeof(Game), nameof(Game.Save))]
internal static class QuestZoneSavePatch
{
    [HarmonyPrefix]
    internal static void OnSave(out System.Action? __state)
    {
        __state = null;

        // nothing of this outside of a session hosted here
        if (NetSession.Instance.Transport is not ElinNetHost host) {
            return;
        }

        __state = host.HideAccompaniedQuest();
    }

    // a finalizer: a save that throws still gives the zone its quest back
    [HarmonyFinalizer]
    internal static void OnSaved(System.Action? __state)
    {
        __state?.Invoke();
    }
}
