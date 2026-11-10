using System.Linq;
using ElinTogether.Components;
using ElinTogether.Helper;
using ElinTogether.LangMod;
using ElinTogether.Net;
using ElinTogether.Patches;
using MessagePack;

namespace ElinTogether.Models;

/// <summary>
///     A picture sent in the chat (ChatImages): a small PNG or a drawing of My drawings (its text, credit included, so
///     a receiver can keep it as a drawing), to everyone as the chat rules say, or to one team. The host checks the
///     rule, the sender, the size and the flood limit, and passes it on as it does a chat line
/// </summary>
[MessagePackObject]
public class ChatImageDelta : ElinDelta
{
    [Key(0)]
    public int ChatId { get; init; }

    [Key(1)]
    public int SpeakerUid { get; init; }

    [Key(2)]
    public string? Name { get; init; }

    /// <summary>0: everyone; else the team it is for</summary>
    [Key(3)]
    public int Team { get; init; }

    [Key(4)]
    public byte[]? Png { get; init; }

    /// <summary>A drawing's text (Drawing.Format), in place of the PNG; each game bakes it itself</summary>
    [Key(5)]
    public string? DrawingText { get; init; }

    /// <summary>What the picture weighs for the flood budget</summary>
    [IgnoreMember]
    internal int Bytes => (Png?.Length ?? 0) + (DrawingText?.Length ?? 0);

    internal override string? OnHostPermit(ElinNetHost host, Chara? sender)
    {
        if (!NetSession.Instance.Rules.ChatImages) {
            return "pictures in the chat are off";
        }

        if (sender is null) {
            return "a picture from no player";
        }

        if (sender.uid == SpeakerUid) {
            // (the sender's own name, as a private line's: security review 2026-11-02)
            if (Name is not null && Name != sender.Name && !(host.States.TryGetValue(OriginPeer, out var state) && Name == state.User.Name)) {
                return $"a picture signed {Name}";
            }
        } else if (SpeakerUid == EClass.pc.uid || host.ActiveRemoteCharas.Any(p => p.Key != OriginPeer && p.Value.uid == SpeakerUid) ||
                   Name is { Length: > 32 }) {
            // (a player holding a map passes its visitors' pictures up, as their chat lines: never under the host's or
            // another present player's name)
            return "a picture sent as another player";
        }

        if (Team != 0 && Teams.TeamOf(SpeakerUid) != Team) {
            return "a picture for a team the sender is not in";
        }

        return ChatImages.IsValidPayload(Png, DrawingText) ? null : $"a picture of {Bytes} bytes that is no small PNG or drawing";
    }

    /// <summary>Host: to every player (or the map's, when the chat is not global), or to the team's members</summary>
    internal void Relay(ElinNetHost host, int exceptPeer)
    {
        if (Team != 0) {
            foreach (var id in host.PeerIds.ToList()) {
                if (id != exceptPeer && host.PlayerChara(id) is { } chara && Teams.TeamOf(chara.uid) == Team) {
                    host.SendDeltaTo(id, this);
                }
            }
        } else if (NetSession.Instance.Rules.GlobalChat) {
            host.SendDeltaToAllExcept(exceptPeer, this);
        } else {
            host.SendDeltaToMapPeers(exceptPeer, this);
        }
    }

    protected override void OnApply(ElinNetBase net)
    {
        // (one picture: a PNG, or a drawing read and checked here, kept for the log)
        var drawing = Png == null ? ChatImages.ReadDrawing(DrawingText) : null;
        if (ChatId == 0 || !MsgSayDelta.Remember(ChatId) || !NetSession.Instance.Rules.ChatImages ||
            (Png != null ? DrawingText != null || !ChatImages.IsValid(Png) : drawing == null)) {
            return;
        }

        if (net is ElinNetHost host && FromPeer) {
            // (the chat's flood limit holds only on enforced servers: pictures have their own, always, ChatImages.Admit)
            if (host.FromMuted(OriginPeer) || host.ChatFlooded(OriginPeer) || !ChatImages.Admit(SpeakerUid, Bytes, 3f)) {
                return;
            }

            Relay(host, OriginPeer);

            // a guest holding its own map is a host for the players there: their pictures go up to the real host too,
            // which passes them to the whole world as their chat lines (the id keeps anyone from drawing one twice)
            if (host.IsZoneSession && (Team != 0 || NetSession.Instance.Rules.GlobalChat) &&
                NetSession.Instance.Transport is ElinNetClient up) {
                up.SendWhileAway(this);
            }
        }

        if (SpeakerUid == EClass.pc.uid || PlayerSocial.IsIgnoredUid(SpeakerUid) || (Team != 0 && Teams.TeamOf(EClass.pc.uid) != Team)) {
            return;
        }

        var name = NetSession.Instance.CurrentPlayers.FirstOrDefault(p => p.CharaUid == SpeakerUid)?.User.Name is { Length: > 0 } own ? own : Name ?? "?";
        ChatLog.Add(Team != 0 ? ChatLog.Team : ChatLog.All, name, "", uid: SpeakerUid, image: Png, drawing: drawing);
        Pop(name, Team, Png, drawing);
    }

    /// <summary>
    ///     A picture of the chat in the game's pop text, as the other lines (user 2026-11-03: it said nothing outside the
    ///     Chat tab): the picture itself, a click on it opens it larger, unless this player hides pictures. Also for its
    ///     sender (lead dev 2026-11-10: a drawing sent showed in the menu's chat only, never in the game's)
    /// </summary>
    internal static void Pop(string name, int team, byte[]? png, Drawing? drawing)
    {
        var said = drawing == null ? "emp_ui_chat_pop_picture".lang() :
            drawing.Title.Length > 0 ? "emp_ui_chat_pop_drawing".Loc(drawing.Title) : "emp_ui_chat_pop_drawing_untitled".lang();
        var who = team != 0 ? "emp_ui_chat_team_tag".Loc(name) : name;
        if (EmpConfig.Client.HideChatImages.Value) {
            ChatBubbleEvent.PopChat(who, said + " " + "emp_ui_chat_pop_hidden".lang());
            return;
        }

        ChatBubbleEvent.PopPicture(who, said, ChatPictureView.Make(png, drawing), () => ChatPictureView.Open(png, drawing, name));
    }
}
