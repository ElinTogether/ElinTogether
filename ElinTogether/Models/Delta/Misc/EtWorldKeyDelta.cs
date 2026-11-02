using ElinTogether.Net;
using MessagePack;

namespace ElinTogether.Models;

/// <summary>
///     The public server asks a player who joined for its ETWorld public key (host -> guest: a fresh Nonce, no Key),
///     the player answers with the key and its signature of the nonce (guest -> host), and the server vouches for it
///     at the world service under the Steam id Steam authenticated for that link (PublicVouch,
///     PLAN_peer_hosted_server 2.1: keys pre-bound before the world is kept by its players). The signature proves
///     the player holds that key (nobody binds another player's key to their own Steam id). Only the public half
///     and a signature travel; the seed never leaves the player's ElinMP folder
/// </summary>
[MessagePackObject]
public class EtWorldKeyDelta : ElinDelta
{
    [Key(0)]
    public string Key { get; set; } = "";

    [Key(1)]
    public string Nonce { get; set; } = "";

    [Key(2)]
    public string Sig { get; set; } = "";

    internal override bool RequiresGameStarted => false;

    protected override void OnApply(ElinNetBase net)
    {
        switch (net) {
            case ElinNetHost host when FromPeer && Key is { Length: > 0 }:
                Helper.PublicVouch.OnKey(host, OriginPeer, Key, Sig ?? "");
                break;
            case ElinNetClient client when Key is not { Length: > 0 } && Nonce is { Length: > 0 }:
                Helper.PublicVouch.Answer(client, Nonce);
                break;
        }
    }
}
