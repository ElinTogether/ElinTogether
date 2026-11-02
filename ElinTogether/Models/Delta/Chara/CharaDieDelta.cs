using ElinTogether.Net;
using ElinTogether.Patches;
using MessagePack;

namespace ElinTogether.Models;

[MessagePackObject]
public class CharaDieDelta : ElinDelta
{
    [Key(0)]
    public required RemoteCard Owner { get; init; }

    [Key(1)]
    public int? ElementId { get; init; }

    [Key(2)]
    public RemoteCard? Origin { get; init; }

    [Key(3)]
    public AttackSource AttackSource { get; init; }

    [Key(4)]
    public RemoteCard? OriginalTarget { get; init; }

    protected override void OnApply(ElinNetBase net)
    {
        if (net.IsHost) {
            // reject every single chara die delta from clients
            return;
        }

        if (Owner.Find() is not Chara chara) {
            return;
        }

        CharaMoveDelta.ClearRecentMove(chara.uid);

        var wasDead = chara.isDead;

        // already taken off its zone here (the removal came first): the game's death code reads the zone and
        // throws, the death itself is just marked
        if (!chara.IsPC && !chara.IsPCFaction && chara.currentZone is null) {
            chara.hp = -1;
            chara.isDead = true;
            return;
        }

        var element = ElementId is null ? null : Element.Create(ElementId.Value);
        // the stub runs none of Chara.Die's prefixes. PlayerAchievements': what the death earns is the killer's
        // player's; without it the replay of another player's kill of a putit earned PUTIT on this account (the host
        // sends a guest's own, AchievementDelta)
        Helper.PlayerAchievements.OnDie(Origin, out var earnedFor);
        // OtherPlayerPatch's: another player's death is no pet's (the statistic and the "a pet died" tutorial)
        OtherPlayerPatch.OnDie(chara, out var allyDeath);
        try {
            chara.Stub_Die(element, Origin, AttackSource, OriginalTarget);
        } finally {
            OtherPlayerPatch.OnDieEnd(allyDeath);
            Helper.PlayerAchievements.OnDieDone(earnedFor);
        }

        // stale
        if (!wasDead && chara.IsPC) {
            player.deathDialog = false;
        }
    }
}