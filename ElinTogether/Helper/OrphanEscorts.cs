using System.Linq;
using ElinTogether.Net;
using HarmonyLib;
using UnityEngine;

namespace ElinTogether.Helper;

/// <summary>
///     GitHub issue 49 ("NPCs not in my party but follow me"): an escort quest makes the one to
///     escort a minion of the player (QuestEscort.OnStart). A minion is no party member, yet it follows its master,
///     fights beside it and is carried along to every map (Chara.MoveZone, listCarryoverMap). The game only lets it go
///     when the quest ends through QuestEscort.OnFail or OnEnterZone (ReleaseEscort). Here a player's quests are also
///     put back from the host's copy, dropped with a lost quest zone or a lost connection, failed quietly from the
///     host (PersonalQuests.Restore, QuestFailEvent.FailQuietly), and the escort stayed a minion of the player for
///     good: three of them escorted the reporter everywhere. <br />
///     Every few seconds, in the game that simulates the map: a minion of the local player that is an escort (tagged
///     when its quest started, or one of the people an escort quest picks, c_neutral) and that no escort quest of the
///     quest log names any more is let go as the game lets an escort go
/// </summary>
[HarmonyPatch]
internal static class OrphanEscorts
{
    internal const string EscortKey = "emp_escort";

    private const float Interval = 5f;

    // what was loaded settles first: a world just replaced puts its quests back a moment later (PersonalQuests)
    private const float SettleSeconds = 10f;

    private static float _next;
    private static object? _place;
    private static float _placeSince;

    [HarmonyPostfix]
    [HarmonyPatch(typeof(QuestEscort), nameof(QuestEscort.OnStart))]
    internal static void OnEscortStart(QuestEscort __instance)
    {
        if (EClass._map?.FindChara(__instance.uidChara) is { } escort) {
            escort.SetInt(EscortKey, __instance.uid == 0 ? 1 : __instance.uid);
        }
    }

    internal static void Tick()
    {
        if (Time.unscaledTime < _next) {
            return;
        }

        _next = Time.unscaledTime + Interval;

        var session = NetSession.Instance;
        if (session.Transport is null || !EClass.core.IsGameStarted || EClass.game?.quests is not { } quests ||
            EClass._zone is not { } zone || EClass._map is null || EClass.pc is not { isDead: false } pc) {
            _place = null;
            return;
        }

        // the game that keeps this map: the host of the world, a player holding its map (with visitors or alone)
        var keeps = session.Connection is ElinNetHost || (session.Connection is null && session.IsZoneAuthority);
        if (!keeps || session.Transport is ElinNetClient { IsInTransfer: true }) {
            _place = null;
            return;
        }

        if (!ReferenceEquals(_place, zone)) {
            _place = zone;
            _placeSince = Time.unscaledTime;
            return;
        }

        if (Time.unscaledTime - _placeSince < SettleSeconds) {
            return;
        }

        var orphans = EClass._map.charas
            .Where(c => IsEscortOf(c, pc) && !quests.list.Exists(q => q is QuestEscort e && e.uidChara == c.uid))
            .ToList();
        foreach (var escort in orphans) {
            EmpLog.Information("Escort {Uid} {Id} of player {Pc} has no escort quest any more: let go", escort.uid, escort.id, pc.uid);
            Release(escort);
        }
    }

    /// <summary>
    ///     A minion of the player brought by an escort quest: tagged when it started, or (escorts of worlds older than
    ///     the tag) one of the people the quest picks from, neither a summon nor one of the world's characters
    /// </summary>
    internal static bool IsEscortOf(Chara chara, Chara player)
    {
        if (chara == player || chara.isDead || chara.c_uidMaster != player.uid || chara.c_minionType != MinionType.Default ||
            chara.isSummon || chara.IsGlobal || chara.IsPlayer) {
            return false;
        }

        return chara.GetInt(EscortKey) != 0 || SpawnList.Get("c_neutral").rows.Exists(r => r.id == chara.id);
    }

    /// <summary>
    ///     As QuestEscort.ReleaseEscort: no longer a minion, gone at once on the world map, else in a while
    /// </summary>
    internal static void Release(Chara escort)
    {
        escort.SetInt(EscortKey, 0);
        escort.UnmakeMinion();
        if (EClass._zone.IsRegion) {
            escort.Destroy();
        } else {
            escort.SetSummon(60, dropCorpse: true);
        }
    }
}
