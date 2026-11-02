using System.Collections.Generic;
using System.Reflection.Emit;
using ElinTogether.Helper;
using ElinTogether.Net;
using HarmonyLib;
using MessagePack;

namespace ElinTogether.Models;

/// <summary>
///     Strife (feat 1355): every kill by "the player's" party feeds the player's ConStrife. Damage is settled in the
///     host's game only, so the kills of a guest and its companions fed the host's strife, never the guest's (hunt 4
///     row F25c). The host now tells the game of the player behind the killer, which counts it for its own player
/// </summary>
[MessagePackObject]
public class StrifeKillDelta : ElinDelta
{
    [Key(0)]
    public required RemoteCard Player { get; init; }

    // what ConStrife.AddKill gives for that victim: 1, 2 for a minion of the faction, 30 for a member
    [Key(1)]
    public required int Gain { get; init; }

    protected override void OnApply(ElinNetBase net)
    {
        // (the condition itself came from the host, as every condition of a guest)
        if (net is ElinNetClient && Player.Find() == EClass.pc && EClass.pc.GetCondition<ConStrife>() is { } strife) {
            Add(strife, Gain);
        }
    }

    /// <summary>
    ///     As ConStrife.AddKill does once it knows the gain (it only does it for "the player")
    /// </summary>
    internal static void Add(ConStrife strife, int gain)
    {
        strife.exp += gain;
        while (strife.exp >= strife.ExpToNext) {
            if (strife.lv >= 5) {
                strife.exp = strife.ExpToNext;
                break;
            }

            strife.exp -= strife.ExpToNext;
            strife.lv++;
            strife.GetElementContainer().SetBase(964, strife.lv >= 3 ? (strife.lv - 2) * 5 : 0);
            strife.GetElementContainer().SetBase(662, strife.lv * 10);
            strife.TryApplyParty();
        }

        strife.SetTurn();
    }

    /// <summary>
    ///     Card.DamageHP asks EClass.pc.Evalue(1355) before counting a kill for the player's strife: asked of the
    ///     player behind the killer instead. Another player's kill is sent to its game and not counted here
    /// </summary>
    [HarmonyPatch(typeof(Card), nameof(Card.DamageHP), typeof(long), typeof(int), typeof(int), typeof(AttackSource), typeof(Card),
        typeof(bool), typeof(Thing), typeof(Chara), typeof(int))]
    internal static class Count
    {
        [HarmonyTranspiler]
        internal static IEnumerable<CodeInstruction> OnDamageHP(IEnumerable<CodeInstruction> instructions)
        {
            return new CodeMatcher(instructions)
                .MatchStartForward(
                    new CodeMatch(OpCodes.Call, AccessTools.PropertyGetter(typeof(EClass), nameof(EClass.pc))),
                    new CodeMatch(i => i.LoadsConstant(1355)),
                    new CodeMatch(OpCodes.Callvirt, AccessTools.Method(typeof(Card), nameof(Card.Evalue), [typeof(int)])))
                .ThrowIfInvalid("Card.DamageHP: no EClass.pc.Evalue(1355)")
                // (the labels of the first stay: a branch may land on it)
                .SetAndAdvance(OpCodes.Ldarg_0, null)
                .SetAndAdvance(OpCodes.Ldarg_S, (byte)5)
                .SetAndAdvance(OpCodes.Call, AccessTools.Method(typeof(Count), nameof(StrifeLevel)))
                .InstructionEnumeration();
        }

        internal static int StrifeLevel(Card victim, Card? origin)
        {
            // a kill the host settled, played again here (CardDamageHpDelta): the host counts it for the player it is
            // about and sends this delta. Counted here as well, a guest gained twice for its own kills (once with the
            // delta, once in the replay) and also gained for every other player's kills
            if (NetSession.Instance.Connection is ElinNetClient) {
                return 0;
            }

            if (NetSession.Instance.Connection is not ElinNetHost host) {
                return EClass.pc.Evalue(1355);
            }

            // the player behind the killer; a member of the faction that no player killed (a monster's work): the
            // player it belongs to, as the host's own companion counts for the host (the guest's replay gave it so far)
            var player = PlayerKarma.PlayerBehind(origin) ?? (victim.IsPCFactionOrMinion ? PlayerKarma.PlayerBehind(victim) : null);
            if (player is not { IsRemotePlayer: true }) {
                return EClass.pc.Evalue(1355);
            }

            // the test the game makes next, for that player: its copy here (what its blows are settled with) and its
            // own game (where the strife wears off, OwnPlayerConditionPatch) count it the same way
            if (player.Evalue(1355) > 0 && victim.isChara && (victim.IsPCFactionOrMinion || origin is { IsPCParty: true }) &&
                (player.GetCondition<ConStrife>() ?? player.AddCondition<ConStrife>() as ConStrife) is { } strife) {
                var gain = !victim.IsPCFactionOrMinion ? 1 : victim.Chara.IsMinion ? 2 : 30;
                Add(strife, gain);
                host.Delta.AddRemote(new StrifeKillDelta {
                    Player = player,
                    Gain = gain,
                });
            }

            return 0;
        }
    }
}
