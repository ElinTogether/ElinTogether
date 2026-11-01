using System;
using System.Collections.Generic;
using System.Linq;
using ElinTogether.LangMod;
using ElinTogether.Models;
using ElinTogether.Net;
using ElinTogether.Net.Steam;
using HarmonyLib;

namespace ElinTogether.Helper;

/// <summary>
///     Host box "Activity journal" (dev/PLAN_hub_features.md B). The host keeps a ring of the world's last 200 events
///     (teams founded, joined, left, lands claimed or lost, duels won, deaths, boss kills, record depths), saved in
///     the world. Each player is told the entries it may see: at join the whole ring, then one entry as it happens.
///     Entries of a team only (<see cref="Entry.TeamOnly" />) go to that team's players only. Written by Harmony
///     postfixes on the existing host methods, which stay as they are
/// </summary>
[HarmonyPatch]
internal static class ActivityJournal
{
    internal const int Capacity = 200;

    internal const int Found = 1, Join = 2, Leave = 3, LandClaimed = 4, LandsLost = 5, DuelWon = 6, Death = 7, BossKill = 8, Depth = 9;

    internal sealed record Entry(int Day, int Kind, int Actor, int Team, string Arg, bool TeamOnly);

    private static bool KeepsBook => NetSession.Instance.Transport is not ElinNetClient;

    private static ElinNetHost? Host => NetSession.Instance?.Transport as ElinNetHost;

    // a guest's copy, newest last
    private static readonly List<Entry> Told = [];

    /// <summary>A guest's copy of the journal, in lines (the server's memory log)</summary>
    internal static int ToldCount => Told.Count;

    /// <summary>
    ///     Recording is on: the host's box, in the game that keeps the book (never in a zone session)
    /// </summary>
    private static bool Recording => Host is { IsZoneSession: false } && EmpConfig.Server.ActivityJournal.Value;

    internal static bool On => NetSession.Instance is { InGame: true, Rules.ActivityJournal: true };

    // ---- storage

    private static string Pack(Entry e) =>
        string.Join("|", e.Day, e.Kind, e.Actor, e.Team, e.TeamOnly ? 1 : 0, e.Arg.Replace("|", "/"));

    private static Entry? Unpack(string text)
    {
        var p = text.Split('|', 6);
        return p.Length == 6 && int.TryParse(p[0], out var day) && int.TryParse(p[1], out var kind) && int.TryParse(p[2], out var actor) &&
            int.TryParse(p[3], out var team)
                ? new Entry(day, kind, actor, team, p[5], p[4] == "1")
                : null;
    }

    private static List<Entry> Ring() =>
        ElinNetHost.ActivityJournalBook.OrderBy(p => p.Key).Select(p => Unpack(p.Value)).OfType<Entry>().ToList();

    /// <summary>
    ///     The entries this game shows: the host's own ring, a guest's copy
    /// </summary>
    internal static IReadOnlyList<Entry> Entries() => KeepsBook ? Ring() : Told;

    /// <summary>
    ///     That viewer may read the entry
    /// </summary>
    internal static bool Visible(Entry entry, int viewerUid)
    {
        return !entry.TeamOnly || entry.Team == Teams.Wanderers || Teams.TeamOf(viewerUid) == entry.Team;
    }

    private static int Today() => EClass.world.date.GetRawDay() / Date.DayToken;

    /// <summary>
    ///     Writes one entry (host, box on) and tells the players who may see it
    /// </summary>
    internal static void Record(int kind, int actor, int team, string arg = "", bool teamOnly = false)
    {
        if (!Recording || Host is not { } host) {
            return;
        }

        var entry = new Entry(Today(), kind, actor, team, arg, teamOnly);
        var state = ElinNetHost.ActivityJournalState;
        var book = ElinNetHost.ActivityJournalBook;
        var next = state.GetValueOrDefault(0) + 1;
        state[0] = next;
        book[next] = Pack(entry);
        while (book.Count > Capacity) {
            book.Remove(book.Keys.Min());
        }

        EmpLog.Information("Journal {Kind}: actor {Actor}, team {Team} {Arg}", kind, actor, team, arg);
        // (every connected player, also one away on a leased zone: it is no longer in ActiveRemoteCharas)
        foreach (var peer in host.PeerIds.ToArray()) {
            if (host.PlayerChara(peer) is { } chara && Visible(entry, chara.uid)) {
                host.SendDeltaTo(peer, Make([entry], false));
            }
        }
    }

    private static JournalDelta Make(IReadOnlyList<Entry> entries, bool full)
    {
        return new() {
            Day = entries.Select(e => e.Day).ToArray(),
            Kind = entries.Select(e => e.Kind).ToArray(),
            Actor = entries.Select(e => e.Actor).ToArray(),
            Team = entries.Select(e => e.Team).ToArray(),
            // (the team-only flag rides on the text: a leading ~)
            Arg = entries.Select(e => e.TeamOnly ? "~" + e.Arg : e.Arg).ToArray(),
            Full = full,
        };
    }

    /// <summary>
    ///     A guest stores what the host told it
    /// </summary>
    internal static void Receive(JournalDelta delta)
    {
        if (KeepsBook) {
            return;
        }

        if (delta.Full) {
            Told.Clear();
        }

        for (var i = 0; i < delta.Kind.Length; i++) {
            var arg = delta.Arg[i];
            var teamOnly = arg.StartsWith('~');
            Told.Add(new(delta.Day[i], delta.Kind[i], delta.Actor[i], delta.Team[i], teamOnly ? arg[1..] : arg, teamOnly));
        }

        while (Told.Count > Capacity) {
            Told.RemoveAt(0);
        }
    }

    // ---- text

    /// <summary>
    ///     One line for the page: the day, then a sentence for the kind
    /// </summary>
    internal static string Line(Entry e)
    {
        var date = e.Day;
        var day = $"{date / 360}-{date % 360 / 30:00}-{date % 30:00}";
        var actor = e.Actor == 0 ? "" : Teams.PlayerName(e.Actor);
        return $"{day}  " + $"emp_journal_{e.Kind}".Loc(actor, Teams.NameOf(e.Team), e.Arg);
    }

    // ---- events

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Teams), nameof(Teams.Found))]
    private static void OnFound(Chara chara, string name, int __result)
    {
        if (__result != Teams.Wanderers) {
            Record(Found, chara.uid, __result, name.Trim());
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Teams), nameof(Teams.Join))]
    private static void OnJoin(Chara chara, int teamId, bool __result)
    {
        if (__result) {
            Record(Join, chara.uid, teamId, teamOnly: true);
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Teams), nameof(Teams.Leave))]
    private static void BeforeLeave(Chara chara, out int __state) => __state = Teams.TeamOf(chara.uid);

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Teams), nameof(Teams.Leave))]
    private static void OnLeave(Chara chara, bool __result, int __state)
    {
        if (__result && __state != Teams.Wanderers) {
            Record(Leave, chara.uid, __state, teamOnly: true);
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Teams), nameof(Teams.OnClaimed))]
    private static void OnClaimed(Zone zone)
    {
        var claimer = Teams.Claimer ?? EClass.pc;
        // (a team's land; one of a player in no team is noted under minus its uid, read by no team)
        if (ElinNetHost.ZoneTeams.TryGetValue(zone.uid, out var team) && team > 0) {
            Record(LandClaimed, claimer.uid, team, zone.Name, true);
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Teams), nameof(Teams.CheckAbandon))]
    private static void BeforeAbandon(out Dictionary<int, int> __state) => __state = new(ElinNetHost.ZoneTeams);

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Teams), nameof(Teams.CheckAbandon))]
    private static void OnAbandon(Dictionary<int, int> __state)
    {
        foreach (var group in __state.Where(p => !ElinNetHost.ZoneTeams.ContainsKey(p.Key)).GroupBy(p => p.Value)) {
            Record(LandsLost, 0, group.Key, group.Count().ToString(), true);
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(PlayerDuel), "Tell")]
    private static void OnDuelTold(object session, int winnerUid)
    {
        if (winnerUid <= 0 || session is null) {
            return;
        }

        var t = Traverse.Create(session);
        var (a, b) = (t.Field("A").GetValue<Chara>(), t.Field("B").GetValue<Chara>());
        var loser = a?.uid == winnerUid ? b : a;
        if (loser is not null) {
            Record(DuelWon, winnerUid, Teams.TeamOf(winnerUid), loser.NameSimple);
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Chara), nameof(Chara.Die))]
    private static void OnDie(Chara __instance, Card? origin)
    {
        if (!Recording || !__instance.isDead) {
            return;
        }

        if (__instance.IsPC || __instance.IsRemotePlayer) {
            Record(Death, __instance.uid, Teams.TeamOf(__instance.uid), (__instance.currentZone ?? EClass._zone)?.NameWithLevel ?? "");
        } else if (__instance.c_bossType != BossType.none && PlayerAchievements.PlayerOf(origin) is { } killer) {
            Record(BossKill, killer.uid, Teams.TeamOf(killer.uid), __instance.NameSimple);
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Zone), nameof(Zone.Activate))]
    private static void OnZone(Zone __instance)
    {
        if (!Recording || EClass._map?.charas is not { } charas) {
            return;
        }

        var depth = __instance.DangerLv;
        foreach (var chara in charas.Where(c => c.IsPC || c.IsRemotePlayer).ToArray()) {
            // (key 0 is the counter; a uid is never 0)
            var best = ElinNetHost.ActivityJournalState.GetValueOrDefault(chara.uid);
            if (depth > best && chara.uid != 0) {
                ElinNetHost.ActivityJournalState[chara.uid] = depth;
                // the first depth a player is seen at is its start, not a record
                if (best > 0) {
                    Record(Depth, chara.uid, Teams.TeamOf(chara.uid), depth.ToString());
                }
            }
        }
    }

    /// <summary>
    ///     A player joins: it is told the ring, as far as it may see it
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(ElinNetHost), "TellJoined")]
    private static void OnJoined(ElinNetHost __instance, List<(Chara Chara, ISteamNetPeer Peer, Action? After)> joins)
    {
        if (!Recording) {
            return;
        }

        var ring = Ring();
        foreach (var (chara, peer, _) in joins) {
            __instance.SendDeltaTo(peer.Id, Make(ring.Where(e => Visible(e, chara.uid)).ToList(), true));
        }
    }
}
