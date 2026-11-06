using ElinTogether.Net;
using MessagePack;

namespace ElinTogether.Models;

/// <summary>
///     Hunt 5 H5-10: a Steam achievement the host's game earned for another player (see PlayerAchievements): given
///     in that player's own game, on its own Steam account
/// </summary>
[MessagePackObject]
public class AchievementDelta : ElinDelta
{
    [Key(0)]
    public required int Id { get; init; }

    /// <summary>
    ///     Silver bells that escaped this player in the host's game: counted in this player's own stats, the game's
    ///     10th escape earns BELL (ActEffect.Proc Escape); Id is then 0
    /// </summary>
    [Key(1)]
    public int SilverBells { get; init; }

    internal override string? OnHostPermit(ElinNetHost host, Chara? sender)
    {
        return "a guest does not give achievements";
    }

    protected override void OnApply(ElinNetBase net)
    {
        if (net.IsHost) {
            return;
        }

        if (SilverBells > 0) {
            var stats = EClass.player.stats;
            stats.escapeSilverBell += SilverBells;
            EmpLog.Information("Silver bell escaped this player in the host's game: {Count} now", stats.escapeSilverBell);
            if (stats.escapeSilverBell >= 10) {
                Steam.GetAchievement(ID_Achievement.BELL);
            }
        }

        if (Id == 0) {
            return;
        }

        EmpLog.Information("Achievement {Id} earned in the host's game for this player", (ID_Achievement)Id);
        Steam.GetAchievement((ID_Achievement)Id);
    }
}
