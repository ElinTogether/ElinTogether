using ElinTogether.Net;
using ElinTogether.Patches;
using MessagePack;

namespace ElinTogether.Models;

/// <summary>
///     Host to one player: how far its god's gifts went in this world (its own count, see RemoteGodGiftPatch). The
///     host gives the gift of a prayer made on its map and keeps the count on its copy of that player; a player
///     praying away from the host counts on its own character. Without this the two counts never met, and each gave
///     the pet and the artifact once (GitHub issue 51: two apostles of Mani, two guns and rings)
/// </summary>
[MessagePackObject]
public class GodGiftDelta : ElinDelta
{
    [Key(0)]
    public required string Religion { get; init; }

    [Key(1)]
    public required int Rank { get; init; }

    protected override void OnApply(ElinNetBase net)
    {
        // only the host counts gifts for others: a player's own count reaches it with its character
        if (net is ElinNetHost || pc is null) {
            return;
        }

        var key = RemoteGodGiftPatch.Key(Religion);
        if (pc.GetInt(key) < Rank) {
            pc.SetInt(key, Rank);
        }
    }
}
