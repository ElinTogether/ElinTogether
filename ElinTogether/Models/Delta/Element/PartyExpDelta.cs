using System.Linq;
using ElinTogether.Net;
using MessagePack;

namespace ElinTogether.Models;

/// <summary>
///     Experience the game gives to "the whole party" (Party.ModExp: a harvest teaches gardening to everyone).
///     Each game gave it to its own copies of the other players, which nothing reads: only the player whose
///     game ran the act learnt. The game where it happens tells the others, each gives it to its own player
/// </summary>
[MessagePackObject]
public class PartyExpDelta : ElinDelta
{
    [Key(0)]
    public required int Element { get; init; }

    [Key(1)]
    public required int Amount { get; init; }

    /// <summary>
    ///     The whole party's (Party.ModExp), not one player's own gain told by the host (stealth, taming, anatomy)
    /// </summary>
    [Key(2)]
    public bool Party { get; init; }

    protected override void OnApply(ElinNetBase net)
    {
        if (net is ElinNetHost host) {
            host.SendDeltaToAllExcept(OriginPeer, this);
        }

        if (Amount is <= 0 or > 100000 || !sources.elements.map.ContainsKey(Element)) {
            return;
        }

        // as this player's own gain: what it becomes is told as any other growth of its character
        using var own = Simulate();
        pc.ModExp(Element, Amount);

        // the companions on the host map are the host's to simulate: a guest's harvest taught them nothing, where the
        // host's own taught every member (Party.ModExp). What they learn is told as any change of theirs
        if (Party && FromPeer && net is ElinNetHost && pc.party is { } party) {
            foreach (var member in party.members.ToArray()) {
                if (member is { IsPC: false, isDead: false } && !member.GetBool("remote_chara")) {
                    member.ModExp(Element, Amount);
                }
            }
        }
    }
}
