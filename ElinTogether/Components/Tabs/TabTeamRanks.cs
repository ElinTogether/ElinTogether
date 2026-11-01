using System.Collections.Generic;
using System.Linq;
using ElinTogether.Helper;
using ElinTogether.LangMod;
using ElinTogether.Models;
using YKF;

namespace ElinTogether.Components;

/// <summary>
///     The Ranks sub-tab of the team page (lead dev 2026-11-01, <see cref="TeamRanks" />): on the left the team's ranks
///     (picked by a click, with the tools to add, move, rename and remove them) and each member's rank; on the right the
///     picked rank's permissions and daily withdraw limit. What the local player's rank cannot change is grey, with one
///     line saying why; the host checks every change again
/// </summary>
internal static class TabTeamRanks
{
    // the rank whose permissions are open on the right, kept over reopenings
    private static int _picked = -1;

    internal static void Build(YKVertical page, int team, int me)
    {
        var (left, right) = page.Book();
        var book = TeamRanks.BookOf(team);
        var rank = book.Find(_picked) ?? book.Find(TeamRanks.RankOf(me, team).Id) ?? book.Ranks[0];
        var mayEdit = TeamRanks.MayEdit(me, team, rank.Id);
        var managesRanks = TeamRanks.Can(me, TeamPerm.Ranks, team);

        BuildRanks(left, team, book, rank, mayEdit, managesRanks);
        BuildMembers(left, team, me, book);
        BuildPerms(right, team, me, rank, mayEdit);
    }

    private static void BuildRanks(YKLayout page, int team, TeamRanks.Book book, TeamRanks.Rank picked, bool mayEdit, bool managesRanks)
    {
        var card = page.Card("emp_ui_rank_title".lang());
        var members = Teams.MembersOfTeam(team).ToList();
        var list = card.ListArea(book.Ranks.Count, 5, 44f);
        foreach (var rank in book.Ranks) {
            var id = rank.Id;
            var count = members.Count(uid => TeamRanks.RankOf(uid, team).Id == id);
            var detail = "emp_ui_rank_count".Loc(count) + (id == book.Default ? "  ·  " + "emp_ui_rank_default".lang() : "");
            list.PickLine(TeamRanks.NameOf(rank), detail, id == picked.Id, () => {
                _picked = id;
                LayerElinTogether.Instance?.Reopen();
            });
        }

        if (!managesRanks) {
            card.TextSmall("emp_ui_rank_view_only".lang(), FontColor.Gray);
            return;
        }

        var index = book.IndexOf(picked.Id);
        var buttons = card.ButtonRow();
        var add = buttons.RowButton("emp_ui_rank_add".lang(), () => {
            Dialog.InputName("emp_ui_rank_name_ask", "emp_rank_new".lang(), (cancel, text) => {
                if (!cancel) {
                    TeamRanks.Ask(RankOp.Add, text: text);
                }
            }).input.field.characterLimit = TeamRanks.NameMax;
        });
        add.interactable = book.Ranks.Count < TeamRanks.MaxRanks;
        if (!add.interactable) {
            add.SetTooltipLang("emp_ui_rank_full");
        }

        buttons.RowButton("emp_ui_rank_up".lang(), () => TeamRanks.Ask(RankOp.Move, picked.Id, -1)).interactable = mayEdit && index > 1 &&
            index - 1 > TeamRanks.PlaceOf(EClass.pc.uid, team);
        buttons.RowButton("emp_ui_rank_down".lang(), () => TeamRanks.Ask(RankOp.Move, picked.Id, 1)).interactable =
            mayEdit && index < book.Ranks.Count - 1;

        var more = card.ButtonRow();
        more.RowButton("emp_ui_rank_rename".lang(), () => {
            Dialog.InputName("emp_ui_rank_name_ask", TeamRanks.NameOf(picked), (cancel, text) => {
                if (!cancel) {
                    TeamRanks.Ask(RankOp.Rename, picked.Id, text: text);
                }
            }).input.field.characterLimit = TeamRanks.NameMax;
        }).interactable = mayEdit;
        more.RowButton("emp_ui_rank_make_default".lang(), () => TeamRanks.Ask(RankOp.SetDefault, picked.Id)).interactable =
            mayEdit && picked.Id != book.Default;
        var remove = more.RowButton("emp_ui_rank_remove".lang(), () => {
            Dialog.YesNo("emp_ui_rank_remove_confirm".Loc(TeamRanks.NameOf(picked)), () => TeamRanks.Ask(RankOp.Remove, picked.Id));
        });
        remove.interactable = mayEdit && picked.Id != book.Default && book.Ranks.Count > 2;
        if (mayEdit && picked.Id == book.Default) {
            remove.SetTooltipLang("emp_ui_rank_default_stays");
        }
    }

    /// <summary>
    ///     Each member and its rank: a dropdown of the ranks the local player may give, for a member below it
    /// </summary>
    private static void BuildMembers(YKLayout page, int team, int me, TeamRanks.Book book)
    {
        var card = page.Card("emp_ui_kv_members".lang());
        var members = Teams.MembersOfTeam(team).ToList();
        var list = card.ListArea(members.Count, 4, UIHelper.RowHeight);
        foreach (var member in members) {
            var current = TeamRanks.RankOf(member, team);
            var giveable = book.Ranks.Where(r => TeamRanks.MayAssign(me, team, member, r.Id)).ToList();
            if (giveable.Count == 0 || !giveable.Contains(current)) {
                list.KeyValue(Teams.PlayerName(member), TeamRanks.NameOf(current));
                continue;
            }

            var target = member;
            list.DropdownRow(Teams.PlayerName(member), giveable.Select(TeamRanks.NameOf).ToList(), i => {
                if (i >= 0 && i < giveable.Count && giveable[i].Id != current.Id) {
                    TeamRanks.Ask(RankOp.Assign, giveable[i].Id, target);
                }
            }, giveable.IndexOf(current));
        }
    }

    /// <summary>
    ///     The picked rank's permissions, one box each, and its daily withdraw limit
    /// </summary>
    private static void BuildPerms(YKLayout page, int team, int me, TeamRanks.Rank rank, bool mayEdit)
    {
        var card = page.Card(TeamRanks.NameOf(rank));
        // (why the boxes are grey, said once)
        var why = rank.Id == TeamRanks.LeaderId ? "emp_ui_rank_leader_all"
            : !TeamRanks.Can(me, TeamPerm.Ranks, team) ? "emp_ui_rank_view_only"
            : !mayEdit ? "emp_ui_rank_above" : null;
        if (why != null) {
            card.TextSmall(why.lang(), FontColor.Gray);
        }

        var own = Teams.LeaderOf(team) == me ? TeamPerm.All : (TeamPerm)TeamRanks.RankOf(me, team).Perms;
        var perms = (TeamPerm)rank.Perms;
        var list = card.ListArea(TeamRanks.Listed.Length, 9, UIHelper.RowHeight);
        foreach (var perm in TeamRanks.Listed) {
            var flag = perm;
            list.ToggleRow($"emp_rank_perm_{perm.ToString().ToLowerInvariant()}".lang(), null, (perms & flag) != 0,
                on => TeamRanks.Ask(RankOp.SetPerms, rank.Id, (int)(on ? perms | flag : perms & ~flag)),
                readOnly: !mayEdit || (own & flag) == 0);
        }

        var limits = TeamRanks.Limits.Select(l => l == 0 ? "emp_ui_rank_no_limit".lang() : l.ToString()).ToList();
        var at = System.Array.IndexOf(TeamRanks.Limits, rank.Limit);
        card.DropdownSettingRow("emp_ui_rank_limit".lang(), "emp_ui_rank_limit_desc".lang(), limits, at < 0 ? 0 : at,
            i => TeamRanks.Ask(RankOp.SetLimit, rank.Id, TeamRanks.Limits[i]), readOnly: !mayEdit || (own & TeamPerm.Withdraw) == 0);
    }
}
