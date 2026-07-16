using ElinTogether.Net;
using MessagePack;

namespace ElinTogether.Models;

[MessagePackObject]
public class CardSetDirDelta : ElinDelta
{
    [Key(0)]
    public required RemoteCard Card { get; init; }

    [Key(1)]
    public required int Dir { get; init; }

    protected override void OnApply(ElinNetBase net)
    {
        if (Card.Find() is not { isDestroyed: false } card) {
            return;
        }

        if (net.IsHost) {
            net.Delta.AddRemote(this);
        }

        if (card.IsPC) {
            return;
        }

        card.SetDir(Dir);
    }
}