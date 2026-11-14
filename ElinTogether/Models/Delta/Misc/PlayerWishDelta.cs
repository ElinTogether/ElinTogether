using System.Collections.Generic;
using ElinTogether.Helper;
using ElinTogether.Net;
using MessagePack;

namespace ElinTogether.Models;

/// <summary>
///     A player's wishes line (PlayerWishes, GitHub issue 21, council 2026-11-14). Guest to host: Text, "set my line"
///     (empty removes it). Host to players: All, the whole table by character uid, or Refusal, why the line was not kept
/// </summary>
[MessagePackObject]
public class PlayerWishDelta : ElinDelta
{
    [Key(0)]
    public string? Text { get; init; }

    [Key(1)]
    public Dictionary<int, string>? All { get; init; }

    /// <summary>PlayerWishes.TooFast, TooLong or Muted; 0 for no refusal</summary>
    [Key(2)]
    public int Refusal { get; init; }

    internal override string? OnHostPermit(ElinNetHost host, Chara? sender)
    {
        if (All != null || Refusal != 0) {
            return "not a request";
        }

        if (!PlayerWishes.On) {
            return "player wishes are off";
        }

        return sender is null ? "a wish from no player" : Text is { Length: > 1000 } ? "a wish of a wall of text" : null;
    }

    protected override void OnApply(ElinNetBase net)
    {
        if (net is ElinNetHost host) {
            if (FromPeer && All == null && Refusal == 0 && host.PlayerChara(OriginPeer) is { } sender &&
                PlayerWishes.Set(host, sender.uid, host.FromMuted(OriginPeer), Text ?? "") is var code and not 0) {
                host.SendDeltaTo(OriginPeer, new PlayerWishDelta { Refusal = code });
            }

            return;
        }

        if (!PlayerWishes.On) {
            return;
        }

        if (All != null) {
            PlayerWishes.Received = All;
        } else if (Refusal != 0) {
            PlayerWishes.Say(Refusal);
        }
    }
}
