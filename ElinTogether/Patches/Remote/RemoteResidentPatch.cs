using System;
using System.Linq;
using ElinTogether.Helper;
using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

/// <summary>
///     What a player does to the residents of the base, from the windows of the residents and from their dialog: the
///     maid, resident or livestock, the reserve of the faction, calling back from it, sending away. Each is a lambda of a
///     menu or a step of a dialog that changed this game's copy only, which the host (who runs the residents and keeps
///     the save) never saw <br />
///     A player of the host's map asks the host instead (<see cref="BaseRequestDelta" />) and changes nothing; the host
///     does it once with the game's own function and tells everyone from there (<see cref="BaseStateDelta" />), the
///     requester included: the same hooks tell what the host does by itself. The host in the zone of a guest cannot ask
///     and is refused, as for the rest of the base, see <see cref="RemoteBasePaidPatch" /> <br />
///     Council 7 (2026-10-05): open to every player; sending away destroys an ordinary resident with what it carries,
///     the other players see the game's line for it
/// </summary>
[HarmonyPatch]
internal static class RemoteResidentPatch
{
    private static void Tell(FactionBranch? branch, BaseRequestKind kind, int uid, int value = 0)
    {
        // what a delta made here is not told again; only the base this game is in, the one the host keeps
        if (ElinDelta.IsApplying || branch is null || branch != EClass._zone?.branch ||
            NetSession.Instance.Connection is not ElinNetHost { IsZoneSession: false } host) {
            return;
        }

        host.Delta.AddRemote(BaseStateDelta.ForResident(branch, kind, uid, value));
    }

    /// <summary>
    ///     The maid is a field the game sets from a menu and a dialog step, nothing to hook: told where it is set
    /// </summary>
    internal static void TellMaid(FactionBranch branch)
    {
        Tell(branch, BaseRequestKind.Maid, branch.uidMaid);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(FactionBranch), nameof(FactionBranch.ChangeMemberType))]
    private static void OnMemberType(FactionBranch __instance, Chara c, FactionMemberType type)
    {
        Tell(__instance, BaseRequestKind.MemberType, c.uid, (int)type);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Faction), nameof(Faction.AddReserve))]
    private static void OnReserve(Chara c)
    {
        Tell(EClass._zone?.branch, BaseRequestKind.Reserve, c.uid);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(FactionBranch), nameof(FactionBranch.Recruit))]
    private static void OnRecruit(FactionBranch __instance, Chara c)
    {
        Tell(__instance, BaseRequestKind.Recruit, c.uid);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(FactionBranch), nameof(FactionBranch.BanishMember))]
    private static void OnBanish(FactionBranch __instance, Chara c, bool skipMsg)
    {
        Tell(__instance, BaseRequestKind.Banish, c.uid, skipMsg ? 1 : 0);
    }

    /// <summary>
    ///     Someone of the reserve sent away for good (the trash button of the reserve window: out of the reserve, then
    ///     OnBanish). Every banish goes through OnBanish: told, a game that has the character in its reserve drops it
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(Chara), nameof(Chara.OnBanish))]
    private static void OnDiscard(Chara __instance)
    {
        Tell(EClass._zone?.branch, BaseRequestKind.Discard, __instance.uid);
    }

    /// <summary>
    ///     The trash button of a line of the reserve window: a player of the host's map keeps the game's question
    ///     ("dialogDeleteRecruit") and asks the host for it; the host in a guest's zone is refused. The "yes and do not ask
    ///     again" of the game's own box is not offered to a client
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(ItemGeneral), nameof(ItemGeneral.AddSubButton))]
    private static void OnSubButton(ItemGeneral __instance, string id, ref Action action)
    {
        if (id != "remove" || __instance.card is not Chara c || !EClass.Home.listReserve.Any(h => h.chara?.uid == c.uid)) {
            return;
        }

        if (RemoteBasePaidPatch.IsRequester) {
            action = () => Dialog.YesNo("dialogDeleteRecruit",
                () => BaseRequestDelta.Send(BaseRequestKind.Discard, c.uid.ToString()));
        } else if (RemoteBasePaidPatch.IsVisiting) {
            action = () => RemoteBasePaidPatch.Refuse(true);
        }
    }

    /// <summary>
    ///     The entries of the menu of a resident in the list of the base (BaseListPeople.OnClick): maid, resident or
    ///     livestock, reserve. The name of the reserve entry carries the count, it is found by its start
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(UIContextMenu), nameof(UIContextMenu.AddButton), typeof(string), typeof(Action), typeof(bool))]
    private static void OnAddButton(UIContextMenu __instance, string idLang, ref Action action)
    {
        if (idLang is null || !__instance.highlightTarget ||
            __instance.highlightTarget.GetComponentInParent<ItemGeneral>()?.card is not Chara c ||
            EClass.Branch is not { } branch) {
            return;
        }

        BaseRequestKind kind;
        if (idLang == "makeMaid") {
            kind = BaseRequestKind.Maid;
        } else if (idLang is "daMakeResident" or "daMakeLivestock") {
            kind = BaseRequestKind.MemberType;
        } else if (idLang.StartsWith("addToReserve".lang(), StringComparison.Ordinal)) {
            kind = BaseRequestKind.Reserve;
        } else {
            return;
        }

        // never a player's own character, nor the companion of a player to the reserve (the host refuses the same);
        // alone, it is the game's own party to put in reserve
        if (c.IsPlayer || (kind == BaseRequestKind.Reserve && c.IsPCParty && NetCompany.HasCompany)) {
            action = () => RemoteBasePaidPatch.Refuse(true);
            return;
        }

        // (GitHub issue 87: a guest holding its own map alone put pets in the reserve there; the reserve is the
        // world's, the host never heard of it and gave the pets back to the base. Not while away)
        if (kind == BaseRequestKind.Reserve && ReserveHeldAway) {
            action = () => {
                SE.Beep();
                EmpPop.Pop("emp_reserve_away".lang());
            };
            return;
        }

        var id = kind == BaseRequestKind.Maid && branch.uidMaid == c.uid ? 0 : c.uid;
        var value = kind == BaseRequestKind.MemberType
            ? (int)(idLang == "daMakeResident" ? FactionMemberType.Default : FactionMemberType.Livestock)
            : 0;

        if (RemoteBasePaidPatch.IsRequester) {
            action = () => BaseRequestDelta.Send(kind, id.ToString(), value);
        } else if (RemoteBasePaidPatch.IsVisiting) {
            action = () => RemoteBasePaidPatch.Refuse(true);
        } else if (kind == BaseRequestKind.Maid) {
            var original = action;
            action = () => {
                original();
                TellMaid(branch);
            };
        }
    }

    // a guest's game holding a map alone (or for others): the world's reserve is not this game's to change
    private static bool ReserveHeldAway => NetSession.Instance is { IsZoneAuthority: true, Transport: ElinNetClient };

    /// <summary>
    ///     Calling someone back from the reserve (the window of the hearth stone)
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(ListPeopleCallReserve), nameof(ListPeopleCallReserve.OnClick))]
    private static bool OnCallBack(Chara c)
    {
        if (ReserveHeldAway) {
            SE.Beep();
            EmpPop.Pop("emp_reserve_away".lang());
            return false;
        }

        // (issue 87: called back at the stone of the commons, a guest's pet became a resident of the commons, by the
        // public spawn, and nobody's: called back at a base of its own)
        if (RemoteBasePaidPatch.IsRequester && Helper.Commons.Is(EClass._zone)) {
            SE.Beep();
            EmpPop.Pop("emp_reserve_commons".lang());
            return false;
        }

        if (RemoteBasePaidPatch.IsRequester) {
            BaseRequestDelta.Send(BaseRequestKind.Recruit, c.uid.ToString());
            return false;
        }

        if (!RemoteBasePaidPatch.IsVisiting) {
            return true;
        }

        RemoteBasePaidPatch.Refuse(true);
        return false;
    }

    /// <summary>
    ///     The dialog of a resident: "maid" and "send away" are steps that do their work in a lambda. A client jumping
    ///     to one asks for it instead and the dialog ends (the talk of the step is not played); the host in a guest's zone
    ///     is refused; the host sets the maid and tells it (the step sets the same value a moment later)
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(DramaSequence), nameof(DramaSequence.Play), typeof(string))]
    private static void OnPlayStep(DramaSequence __instance, ref string id)
    {
        if (id is not ("_daMakeMaid" or "_depart1") || __instance.manager?.tg?.chara is not { } c ||
            EClass.Branch is not { } branch) {
            return;
        }

        var banish = id == "_depart1";
        if (RemoteBasePaidPatch.IsRequester) {
            BaseRequestDelta.Send(banish ? BaseRequestKind.Banish : BaseRequestKind.Maid, c.uid.ToString());
            id = "end";
        } else if (RemoteBasePaidPatch.IsVisiting) {
            RemoteBasePaidPatch.Refuse(true);
            id = "end";
        } else if (!banish) {
            branch.uidMaid = c.uid;
            TellMaid(branch);
        }
    }
}
