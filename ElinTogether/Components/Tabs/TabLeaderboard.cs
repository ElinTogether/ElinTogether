using System;
using System.Collections.Generic;
using System.Linq;
using ElinTogether.Helper;
using ElinTogether.LangMod;
using ElinTogether.Models;
using ElinTogether.Net;
using UnityEngine;
using YKF;

namespace ElinTogether.Components;

/// <summary>
///     The Leaderboards page (PLAN_hub_features D): four boards the host works out (level, deepest floor, duels won,
///     team wealth), the top ten of each and the player's own rank under it. A board is asked from the host when its
///     sub-tab opens
/// </summary>
internal static class TabLeaderboard
{
    private static readonly string[] Icons = ["icon_medal", "icon_influence"];
    private static readonly string[] Keys = ["emp_ui_lb_level", "emp_ui_lb_deepest", "emp_ui_lb_duels", "emp_ui_lb_wealth"];

    private const float RowH = 44f;

    // the board shown (a dropdown picks it: the Community tab's pills are taken by Journal / Leaderboards)
    private static int _board;
    private static YKVertical? _list;
    private static YKVertical? _you;
    private static Action? _relayout;

    public static bool Enabled => NetSession.Instance is { InGame: true, Rules.Leaderboards: true };

    public static int Board => _board;

    public static void Build(YKVertical page, Action relayout)
    {
        var card = page.Card("emp_ui_tab_leaderboard".lang());
        if (!Enabled) {
            TabTeam.EmptyPage(card, Icons, "emp_ui_leaderboard_off".lang());
            return;
        }

        _relayout = relayout;
        card.DropdownRow("emp_ui_lb_board".lang(), Keys.Select(k => k.lang()).ToList(), i => {
            _board = i;
            Redraw();
            Leaderboards.Ask(i);
        }, _board);
        // the top ten scroll inside the card (seven in view); the player's own rank stays under them
        _list = card.ScrollList(7 * (RowH + UIHelper.RowSpacing) - UIHelper.RowSpacing, true);
        _you = card.Vertical();
        _you.Fitter.verticalFit = UnityEngine.UI.ContentSizeFitter.FitMode.PreferredSize;
        Leaderboards.Changed = b => {
            if (b == _board) {
                Redraw();
            }
        };
        Redraw();
        Leaderboards.Ask(_board);
    }

    public static void Release()
    {
        Leaderboards.Changed = null;
        _list = null;
        _you = null;
        _relayout = null;
    }

    private static void Redraw()
    {
        if (_list == null) {
            return;
        }

        var page = _list;
        var board = _board;

        foreach (Transform child in page.transform) {
            UnityEngine.Object.Destroy(child.gameObject);
        }
        var you = _you ?? page;
        if (_you != null) {
            foreach (Transform child in _you.transform) {
                UnityEngine.Object.Destroy(child.gameObject);
            }
        }

        var answer = Leaderboards.Of(board);
        if (answer is null || answer.Names.Length == 0) {
            page.Text("emp_ui_leaderboard_empty".lang(), FontColor.Gray);
        } else {
            for (var i = 0; i < answer.Names.Length; i++) {
                var uid = answer.Uids.Length > i ? answer.Uids[i] : 0;
                var mine = answer.OwnRank == i + 1;
                var row = page.Row();
                // one fixed height: the portrait and the sprite are scaled to it, not the row to them
                row.LayoutElement().minHeight = row.LayoutElement().preferredHeight = RowH;
                row.Layout.childControlHeight = false;
                row.Layout.childForceExpandHeight = false;
                row.Text($"#{i + 1}").LayoutElement().minWidth = 48f;
                if (board != Leaderboards.Wealth) {
                    row.Portrait(Teams.FindChara(uid), RowH);
                } else {
                    // (the team board: each team's emblem where a player's portrait goes)
                    TeamBanner.Draw(row, uid, RowH);
                }

                row.Text(answer.Names[i], mine ? FontColor.Good : FontColor.Default).LayoutElement().flexibleWidth = 1f;
                row.Text(answer.Scores[i].ToString("N0"), mine ? FontColor.Good : FontColor.Default);
            }

            you.Divider();
            you.KeyValue("emp_ui_lb_you".lang(),
                answer.OwnRank > 0 ? "emp_ui_lb_rank".Loc(answer.OwnRank, Mathf.Max(answer.Names.Length, answer.Total), answer.OwnScore.ToString("N0")) : "emp_ui_lb_unranked".lang());
        }

        _relayout?.Invoke();
    }
}
