using System.Linq;
using ElinTogether.Helper;
using ElinTogether.Net;
using MessagePack;

namespace ElinTogether.Models;

/// <summary>
///     A picture sent in the chat (ChatImages): a small PNG, to everyone as the chat rules say, or to one team. The
///     host checks the rule, the sender, the size and the flood limit, and passes it on as it does a chat line
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

    internal override string? OnHostPermit(ElinNetHost host, Chara? sender)
    {
        if (!NetSession.Instance.Rules.ChatImages) {
            return "pictures in the chat are off";
        }

        if (sender?.uid != SpeakerUid) {
            return "a picture sent as another player";
        }

        if (Team != 0 && Teams.TeamOf(SpeakerUid) != Team) {
            return "a picture for a team the sender is not in";
        }

        return ChatImages.IsValid(Png) ? null : $"a picture of {Png?.Length ?? 0} bytes that is no small PNG";
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
        if (ChatId == 0 || !MsgSayDelta.Remember(ChatId) || !NetSession.Instance.Rules.ChatImages || !ChatImages.IsValid(Png)) {
            return;
        }

        if (net is ElinNetHost host && FromPeer) {
            if (host.FromMuted(OriginPeer) || host.ChatFlooded(OriginPeer)) {
                return;
            }

            Relay(host, OriginPeer);
        }

        if (SpeakerUid == EClass.pc.uid || PlayerSocial.IsIgnoredUid(SpeakerUid) || (Team != 0 && Teams.TeamOf(EClass.pc.uid) != Team)) {
            return;
        }

        var name = NetSession.Instance.CurrentPlayers.FirstOrDefault(p => p.CharaUid == SpeakerUid)?.User.Name is { Length: > 0 } own ? own : Name ?? "?";
        ChatLog.Add(Team != 0 ? ChatLog.Team : ChatLog.All, name, "", uid: SpeakerUid, image: Png);
    }
}
