using System.Collections.Generic;
using System.Linq;
using ElinTogether.LangMod;
using ElinTogether.Models;
using ElinTogether.Net;
using ElinTogether.Patches;
using UnityEngine;

namespace ElinTogether.Helper;

/// <summary>
///     Pictures in the chat: one small PNG, or one drawing of My drawings (its text with its credit: every game bakes
///     it, and a player can keep it in their own drawings, credited to its creator), per line, to everyone (as the chat
///     rules say) or to the sender's team. A host rule (ChatImages, off by default) lets them through; each player can
///     hide them (My settings). The ways in: <see cref="Send" />, <see cref="SendDrawing" />
/// </summary>
internal static class ChatImages
{
    /// <summary>The largest picture sent, in bytes and in pixels a side</summary>
    internal const int MaxBytes = 48 * 1024;

    internal const int MaxSide = 256;

    internal static bool Allowed => NetSession.Instance.Rules.ChatImages;

    /// <summary>Why no picture can go to the chat now (no game together, the host's rule off), null when one can</summary>
    internal static string? CantSend() =>
        !NetSession.Instance.InGame ? "emp_ui_chat_off".lang() : !Allowed ? "emp_ui_chat_images_off".lang() : null;

    /// <summary>
    ///     A PNG no larger than MaxBytes whose header says at most MaxSide pixels a side, read before Unity decodes it
    ///     (a small file can claim a huge picture)
    /// </summary>
    internal static bool IsValid(byte[]? png)
    {
        if (png is not { Length: > 24 } || png.Length > MaxBytes ||
            png[0] != 0x89 || png[1] != (byte)'P' || png[2] != (byte)'N' || png[3] != (byte)'G' ||
            png[12] != (byte)'I' || png[13] != (byte)'H' || png[14] != (byte)'D' || png[15] != (byte)'R') {
            return false;
        }

        var width = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
        var height = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
        return width is > 0 and <= MaxSide && height is > 0 and <= MaxSide;
    }

    /// <summary>
    ///     A drawing's text read and checked (no longer than a PNG may weigh, sides of at most MaxSide: Drawing.Parse),
    ///     null when it is not one; a text that throws is no drawing
    /// </summary>
    internal static Drawing? ReadDrawing(string? text)
    {
        if (text is not { Length: > 0 } || text.Length > MaxBytes) {
            return null;
        }

        try {
            return Drawing.Parse(text) is { Art.W: <= MaxSide, Art.H: <= MaxSide } d ? d : null;
        } catch (System.Exception e) {
            EmpLog.Information(e, "Chat: a drawing of {Length} characters could not be read", text.Length);
            return null;
        }
    }

    /// <summary>One picture: a small PNG or a drawing, never both</summary>
    internal static bool IsValidPayload(byte[]? png, string? drawing) =>
        png != null ? drawing == null && IsValid(png) : ReadDrawing(drawing) != null;

    // the picture limit, always on (the chat's flood limit holds only on enforced servers): one picture per player every
    // SecondsBetween, and at most BudgetBytes of pictures passed on per minute by this game, all players together
    internal const float SecondsBetween = 20f;

    internal const int BudgetBytes = 512 * 1024;

    private static readonly Dictionary<int, float> LastBySpeaker = [];
    private static readonly Queue<(float At, int Bytes)> Recent = new();

    /// <summary>Seconds before that player may send another picture (0: now)</summary>
    internal static float WaitFor(int speakerUid) =>
        LastBySpeaker.TryGetValue(speakerUid, out var last) ? Mathf.Max(0f, SecondsBetween - (Time.realtimeSinceStartup - last)) : 0f;

    /// <summary>
    ///     A picture of that player may go on now: counted when it may. The host asks for each one it gets, a game for
    ///     its own before sending (the same limit for every player, the host's included)
    /// </summary>
    internal static bool Admit(int speakerUid, int bytes, float slack = 0f)
    {
        var now = Time.realtimeSinceStartup;
        // (the host's own count starts when a picture reaches it, a little after the sender's: a few seconds of slack)
        var between = SecondsBetween - slack;
        // (players gone or quiet for a while are forgotten: the table never grows)
        foreach (var gone in LastBySpeaker.Where(p => now - p.Value >= between).Select(p => p.Key).ToList()) {
            LastBySpeaker.Remove(gone);
        }

        while (Recent.Count > 0 && now - Recent.Peek().At > 60f) {
            Recent.Dequeue();
        }

        if (LastBySpeaker.ContainsKey(speakerUid) || Recent.Sum(r => r.Bytes) + bytes > BudgetBytes) {
            return false;
        }

        LastBySpeaker[speakerUid] = now;
        Recent.Enqueue((now, bytes));
        return true;
    }

    /// <summary>
    ///     Sends a picture in the chat, to everyone or to this player's team; it shows in this player's log at once.
    ///     Null when sent, else why not (already in the player's language)
    /// </summary>
    internal static string? Send(byte[] png, bool team = false) =>
        !Allowed ? "emp_ui_chat_images_off".lang() :
        !IsValid(png) ? "emp_ui_chat_image_too_big".Loc(MaxSide, MaxBytes / 1024) :
        SendPicture(png, null, null, team);

    /// <summary>
    ///     Sends a drawing of My drawings in the chat, its credit with it (as <see cref="Send" />). Null when sent, else
    ///     why not
    /// </summary>
    internal static string? SendDrawing(Drawing drawing, bool team = false)
    {
        if (!Allowed) {
            return "emp_ui_chat_images_off".lang();
        }

        var text = drawing.Format();
        return ReadDrawing(text) is { } read ? SendPicture(null, text, read, team) : "emp_ui_chat_drawing_too_big".lang();
    }

    private static string? SendPicture(byte[]? png, string? text, Drawing? drawing, bool team)
    {

        if (ModerationDelta.IsMuted(NetSession.Instance.Self?.User ?? HeathenEngineering.SteamworksIntegration.UserData.Me)) {
            return "emp_ui_muted".lang();
        }

        var teamId = team ? Teams.TeamOf(EClass.pc.uid) : 0;
        if (team && (teamId == Teams.Wanderers || !NetSession.Instance.Rules.UseTeams)) {
            return "emp_rank_denied".lang();
        }

        // (the host would drop it: said here, with how long to wait)
        if (!Admit(EClass.pc.uid, (png?.Length ?? 0) + (text?.Length ?? 0))) {
            return "emp_ui_chat_image_wait".Loc(Mathf.CeilToInt(Mathf.Max(1f, WaitFor(EClass.pc.uid))));
        }

        var delta = new ChatImageDelta {
            ChatId = UnityEngine.Random.Range(1, int.MaxValue),
            SpeakerUid = EClass.pc.uid,
            Name = EClass.pc.RemoteState?.User.Name is { Length: > 0 } me ? me : EClass.pc.Name,
            Team = teamId,
            Png = png,
            DrawingText = text,
        };
        MsgSayDelta.Remember(delta.ChatId);
        var away = NetSession.Instance.IsAway ? NetSession.Instance.Transport as ElinNetClient : null;
        if (away is not null) {
            away.SendWhileAway(delta);
        } else if (NetSession.Instance.Transport is ElinNetHost host) {
            delta.Relay(host, int.MinValue);
        } else if (NetSession.Instance.Connection is { } connection) {
            connection.Delta.AddRemote(delta);
        } else {
            return "emp_ui_chat_off".lang();
        }

        ChatLog.Add(team ? ChatLog.Team : ChatLog.All, delta.Name ?? "?", "", true, EClass.pc.uid, png, drawing);
        ChatImageDelta.Pop(delta.Name ?? "?", teamId, png, drawing);
        return null;
    }

    // ---------------------------------------------------------------- keeping a drawing seen in the chat

    /// <summary>That drawing is in this player's My drawings already (the same work by the same creator)</summary>
    internal static bool IsSaved(Drawing drawing) => DrawingStore.All.Any(e => e.Drawing.SameWork(drawing));

    /// <summary>
    ///     A drawing of the chat kept in My drawings as it came, its creator's credit kept (never twice). Null when
    ///     kept, else why not
    /// </summary>
    internal static string? SaveFromChat(Drawing drawing)
    {
        if (IsSaved(drawing)) {
            return "emp_ui_chat_drawing_saved".lang();
        }

        return DrawingStore.Save(drawing) != null ? null : "emp_ui_drawing_full".lang();
    }
}
