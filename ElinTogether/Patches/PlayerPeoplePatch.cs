using System.Collections.Generic;
using System.Linq;
using ElinTogether.Helper;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

/// <summary>
///     The resident board (LayerPeople) of a multiplayer game has a first tab "Players": every player's character, the
///     ones not connected greyed with "offline", and none of them among the residents or the livestock. A player's row
///     offers to go to it only: the resident actions (rename, appearance, maid, livestock, reserve) are not for another
///     player's character. The same on every game (llm-council 2026-10-24: display only, no option)
/// </summary>
[HarmonyPatch]
[AlwaysPatched]
internal static class PlayerPeoplePatch
{
    /// <summary>
    ///     A player's character: the local one, a connected player's, or one that was a player's (the flag the host sets
    ///     when a player joins, kept in the save)
    /// </summary>
    internal static bool IsPlayerChara(Chara c)
    {
        return c.IsPlayer || c.GetBool("remote_chara");
    }

    private static bool Active => NetSession.Instance.Connection is not null;

    /// <summary>
    ///     The adventurers' ranking (the quest board's tab, LayerQuestBoard.RefreshRanking) lists the adventurers and
    ///     "the player" (TraitChara.ShowAdvRank: owner.IsPC): every player of the world now, in every game (user,
    ///     2026-10-27)
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(TraitChara), nameof(TraitChara.ShowAdvRank), MethodType.Getter)]
    internal static void OnShowAdvRank(TraitChara __instance, ref bool __result)
    {
        // (in a guest's game the host's character carries no player mark: the session's player list says it)
        if (!__result && Active && __instance.owner is { } c &&
            (IsPlayerChara(c) || NetSession.Instance.CurrentPlayers.Any(p => p is not null && p.CharaUid == c.uid))) {
            __result = true;
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(LayerPeople), nameof(LayerPeople.OnInit))]
    internal static void OnInit(LayerPeople __instance)
    {
        // (the board of the base only: another use of the layer has made its lists already)
        if (!Active || __instance.multi.owners.Count != 0) {
            return;
        }

        // the game adds its three tabs when there are none yet: added here after the players', the same way
        __instance.multi.AddOwner(0, new ListPeoplePlayers());
        __instance.multi.AddOwner(0, new ListPeople { memberType = FactionMemberType.Default });
        __instance.multi.AddOwner(0, new ListPeople { memberType = FactionMemberType.Livestock });
        __instance.multi.AddOwner(0, new ListPeople { memberType = FactionMemberType.Guest });
        __instance.langHint = "h_residents";
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(BaseListPeople), nameof(BaseListPeople.OnList))]
    internal static void OnList(BaseListPeople __instance)
    {
        if (!Active || __instance.GetType() != typeof(ListPeople) || __instance.memberType == FactionMemberType.Guest) {
            return;
        }

        foreach (var c in __instance.list.objects.OfType<Chara>().Where(IsPlayerChara).ToList()) {
            __instance.list.objects.Remove(c);
        }
    }

    /// <summary>
    ///     ...nor counted with them: a player's character stays a member of the base (RemoveRemoteChara), and the
    ///     game's count (residents tab "4/6", the base's happiness and food, its report) took one slot per character any
    ///     player ever made there (issue #25). A world loaded without a session still has its players' characters:
    ///     those are never residents either
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(FactionBranch), nameof(FactionBranch.CountMembers))]
    internal static void OnCountMembers(FactionBranch __instance, FactionMemberType type, bool onlyAlive, ref int __result)
    {
        foreach (var c in __instance.members) {
            if (c.memberType == type && (!onlyAlive || !c.isDead) && (type != FactionMemberType.Default || c.trait.IsCountAsResident) &&
                (Active ? IsPlayerChara(c) : c.GetBool("remote_chara"))) {
                __result--;
            }
        }
    }

    internal class ListPeoplePlayers : ListPeople
    {
        public override string TextTab => "emp_people_players".lang() + " (" + Players().Count() + ")";

        public override string TextHeader => "_listOf".lang("emp_people_players".lang());

        // (greyed: not connected, wherever the character is; no home and work buttons, the base's business)
        public override bool ShowHome => false;

        public override bool IsDisabled(Chara c)
        {
            return !c.IsPlayer;
        }

        private static IEnumerable<Chara> Players()
        {
            var members = (EClass.Branch ?? EClass.pc.homeBranch)?.members ?? [];
            return members.Concat(EClass.pc.party.members).Concat(EClass._map.charas)
                .Where(IsPlayerChara).Distinct();
        }

        public override void OnList()
        {
            foreach (var c in Players()) {
                list.Add(c);
            }
        }

        public override void SetSubText(Chara a, ItemGeneral b)
        {
            if (!a.IsPlayer) {
                b.SetSubText("emp_people_offline", 300);
                return;
            }

            // its team in place of race or job, when the host has teams (Teams)
            if (NetSession.Instance.Rules.UseTeams) {
                b.SetSubText(Teams.NameOf(Teams.TeamOf(a.uid)), 300);
                return;
            }

            base.SetSubText(a, b);
        }

        public override void OnClick(Chara c, ItemGeneral i)
        {
            // (the local character as the game has it; another player's not here: the game's line where it is)
            if (c.IsPC || !c.IsAliveInCurrentZone) {
                // the team entries go under the game's own for the local character, see OnShowMenu
                _teamMenuFor = c.IsPC ? c : null;
                try {
                    base.OnClick(c, i);
                } finally {
                    _teamMenuFor = null;
                }
                return;
            }

            var menu = EClass.ui.CreateContextMenuInteraction().SetHighlightTarget(i);
            menu.AddButton("findMember", () => {
                EClass.pc.SetAI(new AI_Goto(c, 1));
                layer.Close();
            });
            AddTeamEntries(menu, c, layer);
            menu.Show();
        }
    }

    private static Chara? _teamMenuFor;

    [HarmonyPrefix]
    [HarmonyPatch(typeof(UIContextMenu), nameof(UIContextMenu.Show), new System.Type[0])]
    internal static void OnShowMenu(UIContextMenu __instance)
    {
        if (_teamMenuFor is { } c) {
            _teamMenuFor = null;
            AddTeamEntries(__instance, c, null);
        }
    }

    /// <summary>
    ///     Found, join, leave, accept or refuse (Teams.MenuFor): asked at once, the list shows the new teams once
    ///     the book comes back
    /// </summary>
    private static void AddTeamEntries(UIContextMenu menu, Chara c, Layer? layer)
    {
        foreach (var (label, act) in Teams.MenuFor(c).Concat(PlayerSocial.MenuFor(c))) {
            // (a text already in the player's language: the game's lookup gives it back as it is)
            menu.AddButton(label, () => {
                act();
                SE.Click();
                layer?.Close();
            });
        }
    }
}
