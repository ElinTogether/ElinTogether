using System.Collections.Generic;
using System.Linq;
using ElinTogether.LangMod;
using ElinTogether.Models;
using ElinTogether.Net;
using ElinTogether.Patches;
using HarmonyLib;

namespace ElinTogether.Helper;

/// <summary>
///     Council 2026-10-25, host option "Teams keep their own story journal" (dev/PLAN_teams_knowledge.md). The main
///     quest stays the world's (one QuestMain.phase moves NPCs and zones for everyone): this only remembers, for each
///     team, the furthest chapter one of its players took part in, and shows it on the team card. Taking part: the
///     player who made the step, and every player on the map where it was made (a player alone on its own map: itself
///     only). No world effect
/// </summary>
[HarmonyPatch]
internal static class TeamStory
{
    // the game that keeps the book: the host of the world, or a game playing alone
    private static bool KeepsBook => NetSession.Instance.Transport is not ElinNetClient;

    /// <summary>
    ///     The box as this game knows it: the host's own setting, a guest's from the host's rules
    /// </summary>
    internal static bool On => KeepsBook
        ? EmpConfig.Server.Teams.Value && EmpConfig.Server.TeamStory.Value
        : NetSession.Instance.Rules is { UseTeams: true, TeamStory: true };

    // the chapters the main quest names (QuestMain), each with its text emp_team_chapter_<phase>
    private static readonly int[] Chapters = [0, 100, 200, 250, 300, 600, 700];

    private static Dictionary<int, int> Book => KeepsBook ? ElinNetHost.TeamStoryBook : Teams.ToldStory;

    /// <summary>
    ///     What every game is told with the teams: none while the box is off
    /// </summary>
    internal static Dictionary<int, int>? Told()
    {
        return On ? new Dictionary<int, int>(ElinNetHost.TeamStoryBook) : null;
    }

    /// <summary>
    ///     The furthest chapter of that owner (a team, or minus a player in no team: its own, user 2026-10-30), as the
    ///     team card says it. A player in no team never noted alone yet: what was noted for all of them before
    /// </summary>
    internal static string ChapterOf(int team)
    {
        if (!Book.TryGetValue(team, out var phase) && (team >= 0 || !Book.TryGetValue(Teams.Wanderers, out phase))) {
            return "emp_team_chapter_none".lang();
        }

        return $"emp_team_chapter_{Chapters.LastOrDefault(c => c <= phase)}".lang();
    }

    /// <summary>
    ///     The main quest moved on in the game that keeps the book (a guest's game only takes note of the host's
    ///     steps): the step of the host's own player, or of the player whose step it runs now, a dialog outcome
    ///     included (<see cref="QuestRewardPatch" />)
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(Quest), nameof(Quest.ChangePhase))]
    private static void OnChangePhase(Quest __instance)
    {
        if (__instance is not QuestMain || !KeepsBook) {
            return;
        }

        if (QuestRewardPatch.Earner is { } alone) {
            Record(alone, __instance.phase, true);
        } else {
            // (never the server's stand-in: it plays no story, see ServerAvatar)
            var receiver = QuestRewardPatch.Receiver ?? EClass.pc;
            if (!ServerAvatar.Is(receiver)) {
                Record(receiver, __instance.phase, false);
            }
        }
    }

    /// <summary>
    ///     That player made the main quest reach that phase: its team and the teams of the players on its map took
    ///     part
    /// </summary>
    internal static void Record(Chara stepper, int phase, bool alone)
    {
        if (!KeepsBook || !On) {
            return;
        }

        var players = new List<Chara> { stepper };
        if (!alone) {
            if (!ServerAvatar.Is(EClass.pc)) {
                players.Add(EClass.pc);
            }
            if (NetSession.Instance.Transport is ElinNetHost { IsZoneSession: false } host) {
                players.AddRange(host.ActiveRemoteCharas.Where(p => !host.IsAwayPeer(p.Key)).Select(p => p.Value));
            }
        }

        var changed = false;
        // (the stepper always counts: a player alone on its own map is no active remote chara of the host's map, so
        // IsPlayer is false for it, and its own step was credited to nobody)
        foreach (var team in players.Where(p => p == stepper || p is { IsPlayer: true } || p == EClass.pc).Select(p => StoryOwner.Of(p.uid)).Distinct()) {
            if (Book.TryGetValue(team, out var was) && was >= phase) {
                continue;
            }

            Book[team] = phase;
            changed = true;
            EmpLog.Information("Team {TeamId} took part in the story up to phase {Phase}", team, phase);
        }

        if (changed) {
            (NetSession.Instance.Transport as ElinNetHost)?.TellTeams();
        }
    }
}
