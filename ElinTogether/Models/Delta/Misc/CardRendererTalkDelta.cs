using ElinTogether.Helper;
using ElinTogether.Net;
using ElinTogether.Patches;
using MessagePack;

namespace ElinTogether.Models;

[MessagePackObject]
public class CardRendererTalkDelta : ElinDelta
{
    [Key(0)]
    public required RemoteCard Card { get; init; }

    [Key(1)]
    public required string Text { get; init; }

    [Key(2)]
    public required float Duration { get; init; }

    /// <summary>
    ///     A guest's game shows the bubbles of its own character only (ChatBubbleEvent): never over another character,
    ///     a wall of text or one that stays (security audit 2026-10-27, item 9: any character said anything, under a
    ///     player's name in the pop text)
    /// </summary>
    internal override string? OnHostPermit(ElinNetHost host, Chara? sender)
    {
        if (Text.Length > 500 || Duration is < 0f or > 30f) {
            return $"a bubble of {Text.Length} characters for {Duration} s";
        }

        return Card.Find() is Chara target && target.uid != sender?.uid ? $"a bubble over {target.uid}, not the sender" : null;
    }

    protected override void OnApply(ElinNetBase net)
    {
        if (Card.Find() is not { } card) {
            return;
        }

        // the bubble of a muted player's line (not what the characters of its map say)
        if (card is Chara { IsPlayer: true } && net is ElinNetHost from && from.FromMuted(OriginPeer)) {
            return;
        }

        // the host's rule against players' chat bubbles holds on the host too (a game that does not follow it)
        if (card is Chara { IsPlayer: true } && net is ElinNetHost && FromPeer && !NetSession.Instance.Rules.ChatBubbles) {
            return;
        }

        // relay
        if (net.IsHost) {
            net.Delta.AddRemote(this);
        }

        if (card is Chara { IsPC: true } || (card is Chara { IsPlayer: true } talker && PlayerSocial.IsIgnoredUid(talker.uid))) {
            return;
        }

        using var _ = ChatBubbleEvent.TalkSpeakerContext.Push(card);
        card.HostRenderer.Say(Text, duration: Duration);

        if (card is Chara { IsPlayer: true, RemoteState: { } state }) {
            ChatBubbleEvent.PopChat(state.User.Name.TagColor(PeerColorizer.GetColor(state.Index)), Text);
        }
    }
}