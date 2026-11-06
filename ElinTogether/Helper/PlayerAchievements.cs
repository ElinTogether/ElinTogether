using ElinTogether.Models;
using ElinTogether.Net;
using ElinTogether.Patches;
using System.Linq;
using HarmonyLib;

namespace ElinTogether.Helper;

/// <summary>
///     Hunt 5 H5-10: a Steam achievement goes to the account of the game whose code earns it. What the host's game
///     settles for another player (a use replayed for a guest, a kill by a guest's party, a priest healing a guest,
///     anything done while a guest stands in as "the player") went to the host's account, and a guest's game
///     replaying another player's use earned it too. The achievement now goes to the player it is about: the host
///     sends it to that player's game (AchievementDelta), a game never takes one earned by another player
/// </summary>
[HarmonyPatch]
internal static class PlayerAchievements
{
    // the player what runs now is about (null: "the player" of this game, as the game reads it)
    private static Chara? _for;

    /// <summary>
    ///     What runs until the scope ends is that player's doing: an achievement it earns is its own
    /// </summary>
    internal static ScopeExit For(Chara? player)
    {
        var before = _for;
        _for = player;
        return new() {
            OnExit = () => _for = before,
        };
    }

    /// <summary>
    ///     The player behind that card: a player's character itself, or the player whose companion or summon it is
    /// </summary>
    internal static Chara? PlayerOf(Card? card)
    {
        return card is Chara chara && (chara.IsPC || chara.IsRemotePlayer) ? chara : PlayerKarma.PlayerBehind(card);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(global::Steam), nameof(global::Steam.GetAchievement))]
    internal static bool OnAchievement(ID_Achievement id)
    {
        if (NetSession.Instance.Connection is not { } connection) {
            return true;
        }

        var owner = _for ?? (id == ID_Achievement.PRIEST ? TownLifePatch.LastPatient : null);
        // the host's game while another player stands in as "the player" (a dialog, a craft, a box opened for it)
        if (owner is null && connection is ElinNetHost && EClass.pc is { } pc && pc.IsRemotePlayer) {
            owner = pc;
        }

        // (the host's own player; a guest standing in as "the player" also reads IsPC, it is still the guest's)
        if (owner is null || owner.IsPC && !owner.IsRemotePlayer) {
            // a server plays no character: nothing it settles is the account's of the machine it runs on
            return !ServerAvatar.Active;
        }

        // another player's: its own game is told by the one that runs the map, never kept here
        if (connection is ElinNetHost) {
            GrantTo(owner, id);
        }

        return false;
    }

    /// <summary>
    ///     The host gives that remote player an achievement the host's own game settled for it (nothing for a player
    ///     this game does not run, or on a guest)
    /// </summary>
    internal static void GrantTo(Chara? player, ID_Achievement id)
    {
        if (player is null || NetSession.Instance.Connection is not ElinNetHost host) {
            return;
        }

        foreach (var (peerId, chara) in host.ActiveRemoteCharas) {
            if (chara == player) {
                host.SendDeltaTo(peerId, new AchievementDelta { Id = (int)id });
                EmpLog.Information("Achievement {Id} goes to player {Uid}", id, player.uid);
                break;
            }
        }
    }

    /// <summary>
    ///     A silver bell that escapes on the host's map from another player who fought it: the escape counts for that
    ///     player (its own stats, its own BELL at the 10th), not for the host's (a guest's
    ///     escapes never counted). The host's counter is put out of reach while the game runs it, then back
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(ActEffect), nameof(ActEffect.Proc), typeof(EffectId), typeof(int), typeof(BlessedState), typeof(Card), typeof(Card), typeof(ActRef))]
    internal static void OnEffect(EffectId id, Card cc, out (Chara Player, int Before)? __state)
    {
        __state = null;
        if (id != EffectId.Escape || cc is not Chara { id: "bell_silver" } bell || NetSession.Instance.Connection is not ElinNetHost ||
            PlayerOf(bell.enemy) is not { IsRemotePlayer: true } player) {
            return;
        }

        var stats = EClass.player.stats;
        __state = (player, stats.escapeSilverBell);
        stats.escapeSilverBell = int.MinValue / 2;
    }

    [HarmonyFinalizer]
    [HarmonyPatch(typeof(ActEffect), nameof(ActEffect.Proc), typeof(EffectId), typeof(int), typeof(BlessedState), typeof(Card), typeof(Card), typeof(ActRef))]
    internal static void OnEffectDone((Chara Player, int Before)? __state)
    {
        if (__state is not var (player, before)) {
            return;
        }

        var stats = EClass.player.stats;
        var escaped = stats.escapeSilverBell - int.MinValue / 2;
        stats.escapeSilverBell = before;
        if (escaped > 0 && NetSession.Instance.Connection is ElinNetHost host) {
            foreach (var (peerId, chara) in host.ActiveRemoteCharas) {
                if (chara == player) {
                    host.SendDeltaTo(peerId, new AchievementDelta { Id = 0, SilverBells = escaped });
                    break;
                }
            }
        }
    }

    /// <summary>
    ///     A guest's fishing is played out by the game that runs the map: the stolen fish (EHE_FISH) is that guest's
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(AI_Fish.ProgressFish), nameof(AI_Fish.ProgressFish.OnProgressComplete))]
    internal static void OnFishDone(AI_Fish.ProgressFish __instance, out ScopeExit? __state)
    {
        __state = NetSession.Instance.Connection is not null && PlayerOf(__instance.owner) is { IsRemotePlayer: true } fisher
            ? For(fisher)
            : null;
        EmpLog.Debug("Fishing of {Uid} played out here, hit {Hit}, for its player {For}", __instance.owner?.uid ?? 0, __instance.hit,
            __state is not null);
    }

    [HarmonyFinalizer]
    [HarmonyPatch(typeof(AI_Fish.ProgressFish), nameof(AI_Fish.ProgressFish.OnProgressComplete))]
    internal static void OnFishDoneEnd(ScopeExit? __state)
    {
        __state?.Dispose();
    }

    /// <summary>
    ///     A meal: what it earns (the angry cats of a churyu, the pie of Lothria) is the eater's own, not the account
    ///     of the game that settles it. The pie's call is guarded by "the player" in the game, which another player
    ///     is not on the host: given to it here
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(FoodEffect), nameof(FoodEffect.Proc))]
    internal static void OnFood(Chara c, Thing food, out ScopeExit? __state)
    {
        __state = null;
        if (NetSession.Instance.Connection is null || PlayerOf(c) is not { } eater) {
            return;
        }

        __state = For(eater);
        if (eater.IsRemotePlayer && EClass._zone is Zone_Lothria && food.id is "681" or "pie_meat" or "pie_fish") {
            GrantTo(eater, ID_Achievement.ASHLAND_PIE);
        }
    }

    [HarmonyFinalizer]
    [HarmonyPatch(typeof(FoodEffect), nameof(FoodEffect.Proc))]
    internal static void OnFoodDone(ScopeExit? __state)
    {
        __state?.Dispose();
    }

    /// <summary>
    ///     The mammoth: the game gives it where the eater "is the player", which another player is not on the host.
    ///     The line the eater says when it eats one (Chara.Talk "eatammoth") is not guarded: the host gives it here
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(Card), nameof(Card.Talk))]
    internal static void OnTalk(Card __instance, string idTopic)
    {
        if (idTopic == "eatammoth" && __instance is Chara { IsRemotePlayer: true } eater) {
            GrantTo(eater, ID_Achievement.MAMMOTH);
        }
    }

    /// <summary>
    ///     The slap of the older-younger sister earns its achievement to the one slapped
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(Chara), nameof(Chara.Slap))]
    internal static void OnSlap(Chara c, out ScopeExit? __state)
    {
        __state = NetSession.Instance.Connection is not null && PlayerOf(c) is { } slapped ? For(slapped) : null;
    }

    [HarmonyFinalizer]
    [HarmonyPatch(typeof(Chara), nameof(Chara.Slap))]
    internal static void OnSlapDone(ScopeExit? __state)
    {
        __state?.Dispose();
    }

    /// <summary>
    ///     The snail torturer's whip: the game gives it to the whipped one when it "is the player", which another player
    ///     is not on the host
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(Card), nameof(Card.Say), typeof(string), typeof(Card), typeof(Card), typeof(string), typeof(string))]
    internal static void OnWhip(string lang, Card c2)
    {
        // (the whip's line, AI_Idle "torture_snail": the game's own unlock right after it asks IsPC)
        if (lang == "use_whip3" && c2 is Chara { IsRemotePlayer: true } whipped) {
            GrantTo(whipped, ID_Achievement.SNAIL);
        }
    }

    /// <summary>
    ///     A death settled here: what it earns is the killer's player's (the putit, the game asks "the player's party")
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(Chara), nameof(Chara.Die))]
    internal static void OnDie(Card? origin, out ScopeExit? __state)
    {
        __state = NetSession.Instance.Connection is not null && PlayerOf(origin) is { } killer ? For(killer) : null;
    }

    // a finalizer: the scope ends even if the death throws
    [HarmonyFinalizer]
    [HarmonyPatch(typeof(Chara), nameof(Chara.Die))]
    internal static void OnDieDone(ScopeExit? __state)
    {
        __state?.Dispose();
    }
}
