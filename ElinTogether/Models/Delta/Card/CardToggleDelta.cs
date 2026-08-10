using ElinTogether.Net;
using MessagePack;

namespace ElinTogether.Models;

[MessagePackObject]
public class CardToggleDelta : ElinDelta
{
    [Key(0)]
    public required RemoteCard Card { get; init; }

    [Key(1)]
    public required bool IsOn { get; init; }

    [Key(2)]
    public required bool Silent { get; init; }

    protected override void OnApply(ElinNetBase net)
    {
        if (Card.Find() is not { isDestroyed: false } card) {
            return;
        }

        if (card.isOn == IsOn) {
            return;
        }

        if (net.IsHost) {
            using var _ = Simulate();
            card.trait.Toggle(IsOn, Silent);
            if (card.isOn != IsOn) {
                net.Delta.AddRemote(new CardToggleDelta {
                    Card = Card,
                    IsOn = card.isOn,
                    Silent = true,
                });
            }

            return;
        }

        card.trait.Toggle(IsOn, Silent);
        if (card.isOn != IsOn) {
            card.isOn = IsOn;
            card.trait.PlayToggleEffect(true);
            card.trait.OnToggle();
        }
    }
}