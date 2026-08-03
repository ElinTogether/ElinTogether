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

    protected override void OnApply(ElinNetBase net)
    {
        if (Card.Find() is not { } card) {
            return;
        }

        using var _ = ChatBubbleEvent.TalkSpeakerContext.Push(card);
        card.HostRenderer.Say(Text, duration: Duration);

        if (card is Chara { IsPlayer: true, RemoteState: { } state }) {
            WidgetPopText.Say($"{state.User.Name.TagColor(PeerColorizer.GetColor(state.Index))}: {Text}",
                sprite: UIHelper.FindSprite("emo2_hint"));
        }
    }
}