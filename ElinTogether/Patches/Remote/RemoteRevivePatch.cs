using ElinTogether.Helper;
using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

/// <summary>
///     A companion comes back next to "the player" and into the party of "the player" (Chara.GetRevived). When the act
///     of another player brings it back (the barman's list, a scroll, a spell, see <see cref="CharaReviveRequestDelta" />),
///     the host's game asked its own chara: it stood up next to the host. That player stands in, and it is the host's
///     own gesture so that everyone is told. The companion keeps its owner (nothing here changes it) <br />
///     The click of the barman's list in a guest's game sends the request in place of paying
/// </summary>
[HarmonyPatch]
internal static class RemoteRevivePatch
{
    [HarmonyPrefix]
    [HarmonyPatch(typeof(Chara), nameof(Chara.GetRevived))]
    internal static void OnGetRevived(Chara __instance, out ScopeExit? __state)
    {
        __state = null;
        // same rule as an ally made on the way: the player whose delta is applied or whose turn runs
        if (CharaMakeAllyEvent.Recruiter(__instance) is not { } who) {
            return;
        }

        var sent = ElinDelta.Simulate();
        var told = MsgRelayContext.RedirectTo(who);
        var standIn = RemoteCraft.AsCrafter(who);
        __state = new() {
            OnExit = () => {
                standIn.Dispose();
                told.Dispose();
                sent.Dispose();
            },
        };
    }

    [HarmonyFinalizer]
    [HarmonyPatch(typeof(Chara), nameof(Chara.GetRevived))]
    internal static void OnGetRevivedEnd(ScopeExit? __state)
    {
        __state?.Dispose();
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(ListPeopleRevive), nameof(ListPeopleRevive.OnClick))]
    internal static bool OnReviveClick(ListPeopleRevive __instance, Chara c)
    {
        // the host revives a player (permadeath, PermadeathRevive): not into its own party, not next to "the player"
        if (NetSession.Instance.Connection is ElinNetHost && PlayerRevive.Active && PlayerRevive.IsPlayerChara(c)) {
            if (EClass.pc.TryPay(CalcMoney.Revive(c))) {
                PlayerRevive.ReviveOnHost(c, EClass.pc);
                __instance.list.List();
            }

            return false;
        }

        if (NetSession.Instance.Connection is not ElinNetClient client || ElinDelta.IsApplying) {
            return true;
        }

        // the host in a guest's zone cannot ask the world's host: refused, as the base's plans are
        if (client.IsZoneSession) {
            SE.Beep();
            EmpPop.Pop("emp_base_host_only".lang());
            return false;
        }

        client.Delta.AddRemote(new CharaReviveRequestDelta {
            Target = c,
        });
        SE.Click();
        return false;
    }

    /// <summary>
    ///     GitHub issue 59: a guest travelling alone plays its own copy of the world (no connection to apply to), and the
    ///     game's revive (the barman's list, paid here; a scroll, a spell) stood up its copy of the dead player only, in
    ///     its own party: the player stayed dead in the world. The host revives it where it fell; the price was paid
    ///     here, where this player's purse lives while it travels. This copy stands up too (not in the party), so that
    ///     it is not listed and paid again
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    [HarmonyPatch(typeof(Chara), nameof(Chara.GetRevived))]
    internal static bool OnAwayRevive(Chara __instance)
    {
        var session = NetSession.Instance;
        if (session is not { IsAway: true, Connection: null, Transport: ElinNetClient away } || !__instance.isDead ||
            __instance.IsPC || EClass.game?.principal is not { permadeath: true } || !session.Rules.PermadeathRevive ||
            !PlayerRevive.IsPlayerChara(__instance)) {
            return true;
        }

        EmpLog.Information("Player character {Uid} revived from a map held alone: asked to the host", __instance.uid);
        away.SendWhileAway(new CharaReviveRequestDelta {
            Target = __instance,
            PaidAway = true,
        });
        __instance.Revive();
        return false;
    }

    /// <summary>
    ///     Permadeath with PermadeathRevive: the dead players of the world are on the barman's list too, played now or
    ///     not (the game lists the dead of its faction only)
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(ListPeopleRevive), nameof(ListPeopleRevive.OnList))]
    internal static void OnReviveList(ListPeopleRevive __instance)
    {
        if (!PlayerRevive.Active) {
            return;
        }

        foreach (var chara in EClass.game.cards.globalCharas.Values) {
            if (chara.isDead && chara != EClass.pc && PlayerRevive.IsPlayerChara(chara) && !__instance.list.objects.Contains(chara)) {
                __instance.list.Add(chara);
            }
        }
    }
}
