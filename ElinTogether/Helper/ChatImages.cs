using ElinTogether.LangMod;
using ElinTogether.Models;
using ElinTogether.Net;
using ElinTogether.Patches;

namespace ElinTogether.Helper;

/// <summary>
///     Pictures in the chat (a drawing, from the drawing editor to come): one small PNG per line, to everyone (as the
///     chat rules say) or to the sender's team. A host rule (ChatImages, off by default) lets them through; each player
///     can hide them (My settings). This is the whole way in: <see cref="Send" />
/// </summary>
internal static class ChatImages
{
    /// <summary>The largest picture sent, in bytes and in pixels a side</summary>
    internal const int MaxBytes = 48 * 1024;

    internal const int MaxSide = 256;

    internal static bool Allowed => NetSession.Instance.Rules.ChatImages;

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
    ///     Sends a picture in the chat, to everyone or to this player's team; it shows in this player's log at once.
    ///     Null when sent, else why not (already in the player's language)
    /// </summary>
    internal static string? Send(byte[] png, bool team = false)
    {
        if (!Allowed) {
            return "emp_ui_chat_images_off".lang();
        }

        if (!IsValid(png)) {
            return "emp_ui_chat_image_too_big".Loc(MaxSide, MaxBytes / 1024);
        }

        if (ModerationDelta.IsMuted(NetSession.Instance.Self?.User ?? HeathenEngineering.SteamworksIntegration.UserData.Me)) {
            return "emp_ui_muted".lang();
        }

        var teamId = team ? Teams.TeamOf(EClass.pc.uid) : 0;
        if (team && (teamId == Teams.Wanderers || !NetSession.Instance.Rules.UseTeams)) {
            return "emp_rank_denied".lang();
        }

        var delta = new ChatImageDelta {
            ChatId = UnityEngine.Random.Range(1, int.MaxValue),
            SpeakerUid = EClass.pc.uid,
            Name = EClass.pc.RemoteState?.User.Name is { Length: > 0 } me ? me : EClass.pc.Name,
            Team = teamId,
            Png = png,
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

        ChatLog.Add(team ? ChatLog.Team : ChatLog.All, delta.Name ?? "?", "", true, EClass.pc.uid, png);
        return null;
    }
}
