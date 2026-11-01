using ElinTogether.Helper;
using ElinTogether.Net;
using ElinTogether.Patches;
using MessagePack;

namespace ElinTogether.Models;

[MessagePackObject]
public class CardPlacedDelta : ElinDelta
{
    [Key(0)]
    public required RemoteCard Owner { get; init; }

    [Key(1)]
    public required PlaceState PlaceState { get; init; }

    [Key(2)]
    public required int Dir { get; init; }

    [Key(3)]
    public required bool ByPlayer { get; init; }

    protected override void OnApply(ElinNetBase net)
    {
        if (Owner.Find() is not { } card) {
            return;
        }

        // nothing installed nor taken up on the way to Vernis's mine, nor the furniture of Vernis not claimed (MineWay): the sender is told the thing as it is
        if (net is ElinNetHost wayHost && FromPeer && card is Thing && card.parent == _zone &&
            (PlaceState == PlaceState.installed || card.IsInstalled) && PlaceState != card.placeState &&
            (MineWay.Protects(card.pos, System.Math.Max(card.W, card.H) - 1) || MineWay.IsTownFurniture(card))) {
            wayHost.Delta.AddRemote(new CardPlacedDelta {
                Owner = Owner,
                PlaceState = card.placeState,
                Dir = card.dir,
                ByPlayer = false,
            });
            return;
        }

        if (net.IsHost) {
            net.Delta.AddRemote(this);
        }

        card.dir = Dir;
        card.Stub_SetPlacedState(PlaceState, ByPlayer);
        // the player who installs a mail box owns it
        if (net is ElinNetHost host && FromPeer && PlaceState == PlaceState.installed && ByPlayer &&
            host.ActiveRemoteCharas.TryGetValue(OriginPeer, out var sender)) {
            TeamMail.Claim(card, sender.uid);
        }
    }
}