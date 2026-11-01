using System.Collections.Generic;
using System.Linq;
using ElinTogether.Helper;
using ElinTogether.LangMod;
using ElinTogether.Patches;
using ElinTogether.Net;
using MessagePack;
using UnityEngine;

namespace ElinTogether.Models;

[MessagePackObject]
public class MsgSayDelta : ElinDelta
{
    [Key(1)]
    public required string Text { get; init; }

    [Key(2)]
    public required float R { get; init; }

    [Key(3)]
    public required float G { get; init; }

    [Key(4)]
    public required float B { get; init; }

    [Key(5)]
    public required float A { get; init; }

    /// <summary>
    ///     A line a player typed (not an NPC or game line); only these go global, see GlobalChat
    /// </summary>
    [Key(6)]
    public bool Chat { get; init; }

    /// <summary>
    ///     Identifies a chat line so that a copy reaching a player twice (the map's relay and the global one) is
    ///     shown once
    /// </summary>
    [Key(7)]
    public int ChatId { get; init; }

    /// <summary>
    ///     Character of the player who typed the line, and its name for players that do not know it
    /// </summary>
    [Key(8)]
    public int SpeakerUid { get; init; }

    [Key(9)]
    public string? Name { get; init; }

    /// <summary>
    ///     A line of the server itself (an admin's announcement, a restart warning): shown to every player in the pop
    ///     text and in the message log, whatever the chat rules
    /// </summary>
    [Key(10)]
    public bool Notice { get; init; }

    /// <summary>
    ///     A line for one team only (the Chat tab, 2026-10-30): the host gives it to that team's members, never to the
    ///     others, and only from a member of it. 0: not a team line
    /// </summary>
    [Key(11)]
    public int Team { get; init; }

    private static readonly Queue<int> RecentIds = new();

    /// <summary>The speaker's player name in its colour, else the name the line carries</summary>
    private string SpeakerName()
    {
        var state = NetSession.Instance.CurrentPlayers.FirstOrDefault(p => p.CharaUid == SpeakerUid);
        // (an account without a name, as the bench's: the name the line carries, else the speaker's character)
        var name = state?.User.Name is { Length: > 0 } own ? own : Name is { Length: > 0 } told ? told
            : EClass.game?.cards.globalCharas.Find(SpeakerUid)?.Name ?? "?";
        return state is null ? name : name.TagColor(PeerColorizer.GetColor(state.Index));
    }

    /// <summary>
    ///     False when this chat id was already seen
    /// </summary>
    internal static bool Remember(int id)
    {
        if (RecentIds.Contains(id)) {
            return false;
        }

        RecentIds.Enqueue(id);
        while (RecentIds.Count > 64) {
            RecentIds.Dequeue();
        }

        return true;
    }

    /// <summary>
    ///     Only the host speaks as the server
    /// </summary>
    internal override string? OnHostPermit(ElinNetHost host, Chara? sender)
    {
        return Notice ? "a server notice written by a guest" : null;
    }

    /// <summary>
    ///     The way the game tells something to everyone: the pop text and one line in its message log
    /// </summary>
    internal static void ShowNotice(string text)
    {
        // (the tag in this player's language: the Name sent with a notice is not shown)
        var tag = "emp_ui_server_tag".lang();
        ChatLog.Add(ChatLog.Server, tag, text);
        ChatBubbleEvent.PopChat(tag, text);
        Msg.SetColor(new Color(1f, 0.85f, 0.4f, 1f));
        Msg.SayRaw(tag + " " + text);
    }

    protected override void OnApply(ElinNetBase net)
    {
        if (Chat && ChatId != 0 && !Remember(ChatId)) {
            return;
        }

        // a muted player's line goes no further than its own game; nor a line under another player's name, nor a
        // wall of text (security audit 2026-10-27)
        if (net is ElinNetHost from && FromPeer &&
            (from.FromMuted(OriginPeer) || Text.Length > 500 || (Chat && from.ChatFlooded(OriginPeer)) ||
             // (a player holding a map passes its visitors' lines up: only the host's and other present players' names
             // are someone else's)
             (Chat && SpeakerUid != 0 && (SpeakerUid == pc.uid ||
                                         from.ActiveRemoteCharas.Any(p => p.Key != OriginPeer && p.Value.uid == SpeakerUid))))) {
            return;
        }

        if (Team != 0) {
            // (the host checks the speaker is in that team, and hands the line to that team's members only)
            if (net is ElinNetHost relay && FromPeer) {
                if (relay.PlayerChara(OriginPeer) is not { } speaker || Teams.TeamOf(speaker.uid) != Team) {
                    return;
                }

                ChatLog.RelayTeam(relay, this, OriginPeer);
            }

            if (Teams.TeamOf(pc.uid) == Team && !PlayerSocial.IsIgnoredUid(SpeakerUid)) {
                var who = SpeakerName();
                ChatLog.Add(ChatLog.Team, who, Text);
                ChatBubbleEvent.PopChat("emp_ui_chat_team_tag".Loc(who), Text);
            }

            return;
        }

        if (net is ElinNetHost host) {
            // relay to every connected player, including those travelling alone
            // (they are not in CurrentPlayers while away). Global chat off: player chat only to the players
            // that are on a map (not away)
            if (Chat && !NetSession.Instance.Rules.GlobalChat) {
                host.SendDeltaToMapPeers(OriginPeer, this);
            } else {
                host.SendDeltaToAllExcept(OriginPeer, this);
            }

            // a guest holding its own map is a host for the players there: its chat goes up to the real host,
            // which tells everyone (the players here get it from there too, the id keeps them from reading it twice)
            if (Chat && host.IsZoneSession && NetSession.Instance.Rules.GlobalChat &&
                NetSession.Instance.Transport is ElinNetClient up) {
                up.SendChatWhileAway(this);
            }
        }

        if (Notice) {
            ShowNotice(Text);
            return;
        }

        // an ignored player's line is not shown to me, it still travels on (my personal list, see Social)
        if (Chat && PlayerSocial.IsIgnoredUid(SpeakerUid)) {
            return;
        }

        // a player's line shows like a player speaking on the map: the pop text, never the log (user, 2026-10-24).
        // A speaker on this map already showed it with its bubble, see CardRendererTalkDelta
        if (Chat) {
            // (box unticked: a player elsewhere is not heard, the host's lines reach away players all the same)
            var here = SpeakerUid != 0 && EClass._map?.charas.Exists(c => c.uid == SpeakerUid) == true;
            if (!here && NetSession.Instance.Rules.GlobalChat) {
                ChatBubbleEvent.PopChat(SpeakerName(), Text);
            }

            // (the Chat tab keeps every line this player was shown, its bubble or its pop text)
            if (here || NetSession.Instance.Rules.GlobalChat) {
                ChatLog.Add(ChatLog.All, SpeakerName(), Text);
            }

            return;
        }

        Msg.SetColor(new Color {
            r = R,
            g = G,
            b = B,
            a = A,
        });
        Msg.Say(Text);
    }
}