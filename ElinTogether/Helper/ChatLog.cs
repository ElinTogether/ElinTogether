using System;
using System.Collections.Generic;
using System.Linq;
using ElinTogether.LangMod;
using ElinTogether.Models;
using ElinTogether.Net;
using ElinTogether.Patches;
using UnityEngine;

namespace ElinTogether.Helper;

/// <summary>
///     The lines this game was shown or sent while it runs (the Chat tab, user 2026-10-30): everyone's, the team's, the
///     private ones and the server's, the newest last, 200 at most. Kept in memory only, never in a save
/// </summary>
internal static class ChatLog
{
    internal const int All = 0;
    internal const int Team = 1;
    internal const int Private = 2;
    internal const int Server = 3;

    private const int Capacity = 200;

    internal sealed record Line(string Time, int Channel, string Who, string Text)
    {
        /// <summary>The speaker's character (0: the server or unknown): its name's menu, its team colour, muting</summary>
        public int Uid { get; init; }

        /// <summary>The line names this player (@name): drawn highlighted, counted on the tab</summary>
        public bool Mention { get; init; }
    }

    private static readonly List<Line> Lines = [];

    /// <summary>Goes up with each line, so that an open Chat tab adds the new ones without being built again</summary>
    internal static int Version { get; private set; }

    internal static IReadOnlyList<Line> Entries => Lines;

    // the Version the Chat tab last showed: the lines after it are unread (the tab's badge)
    private static int _seen;

    internal static int Unread => Mathf.Min(Version - _seen, Lines.Count);

    /// <summary>The lines naming this player since the Chat tab was last shown (the tab's "@" count)</summary>
    internal static int UnreadMentions { get; private set; }

    internal static void MarkSeen()
    {
        _seen = Version;
        UnreadMentions = 0;
    }

    internal static void Add(int channel, string who, string text, bool mine = false, int uid = 0)
    {
        // (a line naming this player: highlighted, a short sound of the game's chat, counted until the tab is seen)
        var mention = !mine && channel != Server && ChatMarkup.Mentions(text, MyNames());
        if (mention) {
            UnreadMentions++;
            SE.Play("click_chat");
        }

        Lines.Add(new(DateTime.Now.ToString("HH:mm"), channel, who, text) { Uid = uid, Mention = mention });
        if (Lines.Count > Capacity) {
            Lines.RemoveAt(0);
        }

        Version++;
        // (a line of this player's own is never unread)
        if (mine) {
            _seen = Version;
        }
    }

    /// <summary>The names other players call this one by: its account's, its character's</summary>
    internal static IEnumerable<string> MyNames()
    {
        if (NetSession.Instance.Self?.User.Name is { Length: > 0 } account) {
            yield return account;
        }

        if (EClass.core.IsGameStarted && EClass.pc?.Name is { Length: > 0 } chara) {
            yield return chara;
        }
    }

    /// <summary>Back to the title: the next game starts with no line of the last one</summary>
    [ElinPreSceneInit]
    private static void OnTitle(Scene.Mode mode)
    {
        if (mode == Scene.Mode.Title) {
            Clear();
            _seen = Version;
        }
    }

    /// <summary>A new session starts with no line of the previous one</summary>
    internal static void Clear()
    {
        Lines.Clear();
        UnreadMentions = 0;
        Version++;
    }

    /// <summary>
    ///     A team line: the host gives it to the members of that team only (and shows it when it is one of them), a
    ///     guest sends it up to the host. False when this player is in no team or teams are off
    /// </summary>
    internal static bool SendTeam(string text)
    {
        var team = Teams.TeamOf(EClass.pc.uid);
        if (team == Teams.Wanderers || !NetSession.Instance.Rules.UseTeams || text.Length == 0) {
            return false;
        }

        // (a rank without the team chat: the host refuses the line too, TeamRanks)
        if (!TeamRanks.Can(EClass.pc.uid, TeamPerm.Chat, team)) {
            Msg.Say("emp_rank_denied".lang());
            return false;
        }

        if (ModerationDelta.IsMuted(NetSession.Instance.Self?.User ?? HeathenEngineering.SteamworksIntegration.UserData.Me)) {
            Msg.Say("emp_ui_muted".lang());
            return false;
        }

        var delta = new MsgSayDelta {
            Text = text,
            R = 1,
            G = 1,
            B = 1,
            A = 1,
            Chat = true,
            ChatId = UnityEngine.Random.Range(1, int.MaxValue),
            SpeakerUid = EClass.pc.uid,
            Name = EClass.pc.RemoteState?.User.Name is { Length: > 0 } me ? me : EClass.pc.Name,
            Team = team,
        };
        MsgSayDelta.Remember(delta.ChatId);
        // (the same ways as an everyone line, see ChatBubbleEvent.OnEnterChat: an away player straight to the host)
        var away = NetSession.Instance.IsAway ? NetSession.Instance.Transport as ElinNetClient : null;
        if (away is not null) {
            away.SendChatWhileAway(delta);
        } else if (NetSession.Instance.Transport is ElinNetHost main) {
            RelayTeam(main, delta, int.MinValue);
        } else if (NetSession.Instance.Connection is { } connection) {
            connection.Delta.AddRemote(delta);
        } else {
            return false;
        }

        Add(Team, delta.Name ?? "?", text, true, EClass.pc.uid);
        ChatBubbleEvent.PopChat("emp_ui_chat_team_tag".Loc(delta.Name ?? "?"), text);
        return true;
    }

    /// <summary>
    ///     Host: a team line goes to the connected members of that team only, wherever they are
    /// </summary>
    internal static void RelayTeam(ElinNetHost host, MsgSayDelta delta, int exceptPeer)
    {
        foreach (var id in host.PeerIds.ToList()) {
            if (id != exceptPeer && host.PlayerChara(id) is { } chara && Teams.TeamOf(chara.uid) == delta.Team) {
                host.SendDeltaTo(id, delta);
            }
        }
    }

    /// <summary>The colour of a channel's tag in the Chat tab</summary>
    internal static Color ColorOf(int channel) => channel switch {
        Team => new(0.2f, 0.5f, 0.2f),
        Private => new(0.5f, 0.25f, 0.6f),
        Server => new(0.7f, 0.45f, 0.05f),
        _ => new(0.15f, 0.1f, 0.05f),
    };
}
