using System.Linq;
using ElinTogether.Helper;
using ElinTogether.LangMod;
using ElinTogether.Models;
using YKF;

namespace ElinTogether.Components;

/// <summary>
///     A team accepted the local player's request while it was in another team (founded or joined since it asked,
///     user 2026-11-01): the player is not moved, it chooses. A game dialog when the acceptance arrives, and the same
///     choice on the Team page until it answers or the acceptance expires (<see cref="Teams.OfferSeconds" />)
/// </summary>
internal static class TeamOfferCard
{
    /// <summary>
    ///     "Keep {team}", or the plain refusal for a player who left its team since
    /// </summary>
    internal static string KeepLabel(int mine)
    {
        return mine == Teams.Wanderers ? "emp_ui_team_invite_refuse".lang() : "emp_team_offer_keep".Loc(Teams.NameOf(mine));
    }

    /// <summary>
    ///     What joining does, in one short line: the leadership and lands of the team left (they stay with that team,
    ///     its next member leads it) and the player's own lands (they go with it, up to the host's cap)
    /// </summary>
    internal static string Outcome(int me, int offered)
    {
        var mine = Teams.TeamOf(me);
        var line = "";
        if (mine != Teams.Wanderers) {
            var others = Teams.MembersOfTeam(mine).Where(uid => uid != me).ToList();
            line = Teams.LeaderOf(mine) != me ? "emp_team_offer_leave".Loc(Teams.NameOf(mine))
                : others.Count > 0 ? "emp_team_offer_leave_lead".Loc(Teams.NameOf(mine), Teams.PlayerName(others[0]))
                : "emp_team_offer_leave_empty".Loc(Teams.NameOf(mine));
        }

        if (Teams.LandsOf(-me).Count() is > 0 and var own) {
            line = (line + " " + "emp_team_offer_own_lands".Loc(own, Teams.NameOf(offered))).Trim();
        }

        return line;
    }

    private static string Text(int me, int offered) =>
        ("emp_team_offer".Loc(Teams.NameOf(offered)) + " " + Outcome(me, offered)).Trim();

    /// <summary>
    ///     The dialog: join, keep, or decide later (the Team page keeps the choice)
    /// </summary>
    internal static void Show(int offered)
    {
        var me = EClass.pc.uid;
        Dialog.Choice(Text(me, offered), d => {
            d.AddButton("emp_team_offer_join".Loc(Teams.NameOf(offered)), () => Teams.Ask(TeamAsk.JoinOffered, offered));
            d.AddButton(KeepLabel(Teams.TeamOf(me)), () => Teams.Ask(TeamAsk.KeepTeam, offered));
            d.AddButton("emp_team_offer_later".lang());
        });
    }

    /// <summary>
    ///     The same choice as a card at the top of the Team page
    /// </summary>
    internal static void Build(YKLayout page, int me)
    {
        var offered = Teams.OfferOf(me);
        if (offered == Teams.Wanderers) {
            return;
        }

        var card = page.Card("emp_ui_team_offer_title".lang());
        card.Text(Text(me, offered));
        var row = card.ButtonRow();
        row.RowButton("emp_team_offer_join".Loc(Teams.NameOf(offered)), () => Teams.Ask(TeamAsk.JoinOffered, offered));
        row.RowButton(KeepLabel(Teams.TeamOf(me)), () => Teams.Ask(TeamAsk.KeepTeam, offered));
    }
}
