using ElinTogether.Net;
using MessagePack;

namespace ElinTogether.Models;

/// <summary>
///     The public server asks a player who joined for its ETWorld public key (Key empty, host -> guest), the player
///     answers with it (guest -> host), and the server vouches for it at the world service under the Steam id Steam
///     authenticated for that link (PublicVouch, PLAN_peer_hosted_server 2.1: keys pre-bound before the world is
///     kept by its players). Only the public half travels; the seed never leaves the player's ElinMP folder
/// </summary>
[MessagePackObject]
public class EtWorldKeyDelta : ElinDelta
{
    [Key(0)]
    public string Key { get; set; } = "";

    internal override bool RequiresGameStarted => false;

    protected override void OnApply(ElinNetBase net)
    {
        switch (net) {
            case ElinNetHost host when FromPeer && Key is { Length: > 0 }:
                Helper.PublicVouch.OnKey(host, OriginPeer, Key);
                break;
            case ElinNetClient client when Key is not { Length: > 0 }:
                Helper.PublicVouch.Answer(client);
                break;
        }
    }
}
