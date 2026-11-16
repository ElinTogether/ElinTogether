using ElinTogether.Helper;
using ElinTogether.Net;
using MessagePack;

namespace ElinTogether.Models;

/// <summary>
///     How much a resident likes the players: one value for the group, kept by the host. The game rolls dice
///     for every point, so each game would otherwise end with its own number. Host option "Affinity per player": a
///     guest's change goes to its own column, the host answers that guest alone with the new value
/// </summary>
[MessagePackObject]
public class CharaAffinityDelta : ElinDelta
{
    [Key(0)]
    public required RemoteCard Owner { get; init; }

    [Key(1)]
    public required int Value { get; init; }

    /// <summary>
    ///     From a client: what its action changed. From the host: the value everyone takes
    /// </summary>
    [Key(2)]
    public bool Relative { get; set; }

    /// <summary>
    ///     Affinity per player, from the host: its own value (the game's Chara._affinity, the world owner's), which
    ///     the others never read but keep in their copy of the map: a map a guest kept while the host was away comes
    ///     back with it (#41)
    /// </summary>
    [Key(3)]
    public bool Owners { get; set; }

    // shared value: only the default permit (audit 2026-10-23): whoever a guest gives to or talks with is legit,
    // another player too (a gift to it runs ModAffinity in the giver's game). How much is not checked: a gift moves
    // it a lot
    internal override string? OnHostPermit(ElinNetHost host, Chara? sender)
    {
        if (!AffinityPerPlayer.On) {
            return null;
        }

        // per player it only ever moves the sender's own column: a loss hurts the sender alone, a gain is what one
        // act can bring, next to the resident
        if (NoCharacter(sender) is { } none) {
            return none;
        }

        if (!Relative) {
            return "a guest sends changes, not values";
        }

        if (Value > AffinityPerPlayer.MaxGain) {
            return $"a gain of {Value} is more than one act brings";
        }

        return Value > 0 && Owner.Find() is Chara npc && npc.currentZone is { } there && sender!.currentZone is { } here &&
               there != here
            ? $"resident {npc.uid} is not on the sender's map"
            : null;
    }

    protected override void OnApply(ElinNetBase net)
    {
        if (AffinityPerPlayer.On) {
            switch (net) {
                case ElinNetHost { IsZoneSession: false } keeper when Relative && FromPeer && keeper.PlayerChara(OriginPeer) is { } sender:
                    keeper.SendDeltaTo(OriginPeer, new CharaAffinityDelta {
                        Owner = Owner,
                        Value = AffinityPerPlayer.Add(Owner, sender.uid, Value),
                    });
                    break;
                case ElinNetClient when !Relative && Owners:
                    if (Owner.Find() is Chara known) {
                        AffinityPerPlayer.KeepOwners(known, Value);
                    }

                    break;
                case ElinNetClient when !Relative:
                    AffinityPerPlayer.Learn(Owner.Uid, Value);
                    break;
            }

            return;
        }

        if (Owner.Find() is not Chara chara) {
            return;
        }

        if (net is ElinNetHost host) {
            if (!Relative) {
                return;
            }

            chara._affinity += Value;
            host.Delta.AddRemote(new CharaAffinityDelta {
                Owner = Owner,
                Value = chara._affinity,
            });
            return;
        }

        if (!Relative) {
            chara._affinity = Value;
        }
    }
}
