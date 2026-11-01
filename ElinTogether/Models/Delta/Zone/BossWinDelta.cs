using ElinTogether.Net;
using MessagePack;

namespace ElinTogether.Models;

/// <summary>
///     The boss of a zone fell. The game that simulated its death says so: it alone marked the dungeon
///     conquered, played the fanfare and gave fame to "the player". A guest who beat a boss alone in a dungeon
///     told nobody (the host's world still had the boss and an unconquered dungeon), and a player beside the one
///     whose game simulates saw the boss die like any monster. The world's part goes to every game, the
///     victory to every player who stands on that map
/// </summary>
[MessagePackObject]
public class BossWinDelta : ElinDelta
{
    [Key(0)]
    public required int ZoneUid { get; init; }

    [Key(1)]
    public required int TopZoneUid { get; init; }

    [Key(2)]
    public required bool Nefia { get; init; }

    [Key(3)]
    public required int DangerLv { get; init; }

    /// <summary>
    ///     The boss did not fall: it left (Zone.Simulate, a floor entered again after its boss fight was left), and the
    ///     game marked the dungeon conquered. Only that is told: no fanfare, no fame
    /// </summary>
    [Key(4)]
    public bool Left { get; init; }

    /// <summary>
    ///     Security audit 2026-10-27 (item 5): any zone's boss could be declared dead, any dungeon conquered, with a
    ///     depth of the sender's choosing (fame for everyone on that map). Only the game that simulates the zone tells:
    ///     a guest away in its own zones, never one on the host's map (the host's game sees that death itself); the
    ///     dungeon and depth must be the host's own. (A zone with no boss left in a game gives nothing there: each
    ///     game checks its own)
    /// </summary>
    internal override string? OnHostPermit(ElinNetHost host, Chara? sender)
    {
        if (game.spatials.Find(ZoneUid) is not Zone zone) {
            return null;
        }

        if (!host.IsAwayPeer(OriginPeer)) {
            return $"boss of zone {ZoneUid} claimed by a guest that does not simulate it";
        }

        return DangerLv > zone.DangerLv || TopZoneUid != zone.GetTopZone().uid || Nefia != zone.IsNefia
            ? $"boss of zone {ZoneUid} claimed with depth {DangerLv} (host {zone.DangerLv}), top {TopZoneUid}, nefia {Nefia}"
            : null;
    }

    protected override void OnApply(ElinNetBase net)
    {
        if (net is ElinNetHost host) {
            host.SendDeltaToAllExcept(OriginPeer, this);
        }

        if (game.spatials.Find(ZoneUid) is not Zone zone) {
            return;
        }

        if (Left) {
            if (Nefia && game.spatials.Find(TopZoneUid) is Zone conquered) {
                conquered.isConquered = true;
            }

            return;
        }

        // (a game that played the death itself has no boss left there: it had its own fanfare)
        var hadBoss = zone.uidBoss != 0;
        zone.uidBoss = 0;
        if (Nefia && game.spatials.Find(TopZoneUid) is Zone top) {
            top.isConquered = true;
        }

        if (!hadBoss || _zone != zone) {
            return;
        }

        Msg.Say("boss_win", zone.Name);
        if (Nefia) {
            player.stats.nefiaBeaten++;
            SE.Play("Jingle/fanfare2");
            zone.SetBGM(114, refresh: true, 3f);
        }

        // the depth reached is each player's own, raised in its own game as the game does for the one that fights
        if (DangerLv > player.stats.deepest) {
            player.stats.deepest = DangerLv;
            Msg.Say("update_deepest", player.stats.deepest.ToString());
        }

        if (zone is Zone_Void) {
            if (!player.flags.KilledBossInVoid) {
                Msg.Say("boss_win_void", zone.Name);
                player.flags.KilledBossInVoid = true;
            }

            if (-zone.lv > player.stats.deepestVoid) {
                player.stats.deepestVoid = -zone.lv;
                Msg.Say("update_deepest_void", player.stats.deepestVoid.ToString());
            }
        }

        player.ModFame(rndHalf(30 + DangerLv * 2));
        player.ModKarma(5);
    }
}
