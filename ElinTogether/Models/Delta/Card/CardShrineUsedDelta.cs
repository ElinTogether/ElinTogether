using ElinTogether.Net;
using MessagePack;

namespace ElinTogether.Models;

[MessagePackObject]
public class CardShrineUsedDelta : ElinDelta
{
    [Key(0)]
    public required RemoteCard Card { get; init; }

    protected override void OnApply(ElinNetBase net)
    {
        // host only, no Toggle
        if (net.IsHost) {
            return;
        }

        if (Card.Find() is not Thing { isDestroyed: false, trait: TraitPowerStatue } shrine) {
            return;
        }

        if (!shrine.isOn) {
            return;
        }

        shrine.isOn = false;
        if (shrine.trait is TraitGodStatue) {
            shrine.ChangeMaterial("onyx"); // used
        }

        shrine.rarity = Rarity.Normal;
        shrine.renderer?.RefreshExtra();
    }
}