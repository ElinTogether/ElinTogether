using ElinTogether.Helper;
using ElinTogether.Net;
using ElinTogether.Patches;
using MessagePack;

namespace ElinTogether.Models;

[MessagePackObject]
public class CardDamageHpDelta : ElinDelta
{
    [Key(0)]
    public required RemoteCard Owner { get; init; }

    [Key(1)]
    public required long Dmg { get; init; }

    [Key(2)]
    public required int Ele { get; init; }

    [Key(3)]
    public int EleP { get; init; }

    [Key(4)]
    public AttackSource AttackSource { get; init; }

    [Key(5)]
    public RemoteCard? Origin { get; init; }

    [Key(6)]
    public bool ShowEffect { get; init; }

    [Key(7)]
    public RemoteCard? Weapon { get; init; }

    [Key(8)]
    public RemoteCard? OriginalTarget { get; init; }

    [Key(9)]
    public int ResistPenetrationLevel { get; init; }

    [Key(10)]
    public int? HpAfter { get; set; }

    internal override string? OnHostPermit(ElinNetHost host, Chara? sender)
    {
        // a guest's game reports the blows its own character took (CardDamageHpEvent); who struck is anyone
        return NoCharacter(sender) ?? NotOwned(sender, Owner, "damaged");
    }

    protected override void OnApply(ElinNetBase net)
    {
        if (Owner.Find() is not { } card) {
            return;
        }

        // a player is shielded from another player's blow wherever the blow lands: in the replay of what the
        // simulating game decided, and here when a client reports the blow its own game dealt to its player
        // (the stub is the game's own code, without the prefix that shields)
        var origin = Origin?.Find();
        if (PlayerDuel.JustEnded(card, origin)) {
            return;
        }

        var shield = RemotePlayerKillPatch.Shield(card, origin);
        // the stub skips CardDamageHpEvent's prefix, which marks "inside the damage code": on the host, a blow a
        // guest's game reported on its own character (poison, a trap, hunger) left that player's copy feared under
        // a fifth of its life (the game spares "the player" only), and the fear reached the guest, who could no
        // longer strike. The host's own player is never feared there
        CardDamageHpEvent.Depth++;
        try {
            using (Simulate(net.IsHost)) {
                card.Stub_DamageHP(Dmg, Ele, EleP, AttackSource, Origin, ShowEffect, Weapon, OriginalTarget, ResistPenetrationLevel);
            }
        } finally {
            CardDamageHpEvent.Depth--;
            RemotePlayerKillPatch.Unshield(card, shield, origin);
        }

        if (!net.IsHost && HpAfter is { } hpAfter && card is not Chara { isDead: true }) {
            card.hp = hpAfter;
        }
    }
}