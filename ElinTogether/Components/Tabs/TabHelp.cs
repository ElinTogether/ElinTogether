using System.Collections.Generic;
using System.Linq;
using ElinTogether.Helper;
using ElinTogether.LangMod;
using UnityEngine;
using YKF;

namespace ElinTogether.Components;

/// <summary>
///     The "Help" page (hub step 7): short "how it works" cards, About (version, the GitHub page) and the bug report,
///     which used to hide at the bottom of the client settings
/// </summary>
internal static class TabHelp
{
    internal const string Repo = "https://github.com/ElinTogether/ElinTogether";

    // topic, the tab where it is done (tab name, its label id)
    private static readonly (string Id, string Tab, string Label)[] Topics = [
        ("hosting", TabPlay.TabName, "emp_ui_tab_play"),
        ("joining", TabPlay.TabName, "emp_ui_tab_play"),
        ("teams", "emp_tab_team", "emp_ui_tab_team"),
        ("lands", "emp_tab_lands", "emp_ui_tab_lands"),
        ("trading", TabPlayers.TabName, "emp_ui_tab_players"),
        ("duels", TabServerConfiguration.RulesLink, "emp_ui_tab_server"),
    ];

    /// <summary>The Help page, the last sub-tab of World rules in a game (About went: Status shows the version)</summary>
    public static List<(string, System.Action<YKVertical>)> Pages()
    {
        return [
            ("emp_ui_sub_how".lang(), BuildHow),
        ];
    }

    // the topic open on the right page
    private static int _topic;

    /// <summary>
    ///     The journal's two pages: the topics on the left (a click opens one), the picked topic's words and the button
    ///     to its page on the right (six cards one under the other ran under the window's edge)
    /// </summary>
    private static void BuildHow(YKVertical page)
    {
        var (left, right) = page.Book();
        var list = left.Card("emp_ui_sub_how".lang());
        var text = right.Vertical();
        text.Fitter.verticalFit = UnityEngine.UI.ContentSizeFitter.FitMode.PreferredSize;
        text.Layout.spacing = UIHelper.RowSpacing;
        var rows = new List<YKHorizontal>();

        void Show(int index, bool relayout = true)
        {
            _topic = index;
            foreach (Transform child in text.transform) {
                Object.Destroy(child.gameObject);
            }

            var (id, tab, label) = Topics[index];
            var card = text.Card($"emp_help_{id}_t".lang());
            card.Text($"emp_help_{id}".lang());
            card.ButtonRow().RowButton("emp_ui_help_open".Loc(label.lang()),
                () => LayerElinTogether.Instance?.Open(tab));
            for (var i = 0; i < rows.Count; i++) {
                if (rows[i].GetComponent<UnityEngine.UI.Image>() is { } shade) {
                    shade.color = i == index ? new(0.28f, 0.22f, 0.13f, 0.14f) : new(0f, 0f, 0f, 0.001f);
                }
            }

            UIHelper.WrapTexts(text);
            if (relayout) {
                page.GetComponentInParent<TabEmpBase>()?.Relayout();
            }
        }

        for (var i = 0; i < Topics.Length; i++) {
            var index = i;
            rows.Add(list.PickLine($"emp_help_{Topics[i].Id}_t".lang(), null, i == _topic, () => Show(index)));
        }

        Show(Mathf.Clamp(_topic, 0, Topics.Length - 1), false);
    }

    // the report's draft, kept while the window is closed and opened again, cleared once sent
    private static int _category;
    private static string _summary = "";
    private static string _detail = "";
    private static int _again = 2;
    private static bool _video = true;
    private static bool _picture = true;

    internal static ReportAnswers Draft => new(_category, _summary, _detail, _again, _video, _picture);

    // something went wrong: a GitHub issue with the player's words and logs, or one zip on the desktop. Laid out as the
    // game's own "Report Bugs" window (LayerFeedback, user 2026-10-30): the form on the left page, thanks and tips on
    // the right one
    internal static void BuildReport(YKVertical page)
    {
        var (left, right) = page.Book();
        var card = left.Card("emp_help_report".lang());
        // (the video stops while this page is shown: it keeps the 30 seconds before the report was opened)
        card.gameObject.AddComponent<ReplayBuffer.Hold>();

        UIButton? send = null;
        UIText? hint = null;

        void Refresh()
        {
            var missing = Draft.Missing();
            send?.interactable = missing is null;
            hint?.SetText(missing is null ? "" : "emp_ui_report_missing".Loc(string.Join(", ", missing.Select(id =>
                $"{id}_need".Loc(id == "emp_ui_report_summary" ? ReportAnswers.SummaryMin : ReportAnswers.DetailMin)))));
        }

        UIInputText Box(string label, string placeholder, string value, int most, float height, System.Action<string> keep)
        {
            card.Text(label.lang());
            var input = card.InputText(value);
            input.field.characterLimit = most;
            input.AllowAnyText();
            input.StyleAsSearch(placeholder.lang());
            if (height > 0f) {
                // the text from the top left, wrapped, Enter a new line
                TextBox(input, height);
            }

            input.field.onValueChanged.AddListener(text => {
                keep(text);
                Refresh();
            });
            return input;
        }

        var category = card.DropdownRow("emp_ui_report_category".lang(),
            [..ReportAnswers.CategoryIds.Select(id => $"emp_ui_report_cat_{id}".lang())], i => _category = i, _category);
        var summary = Box("emp_ui_report_summary", "emp_ui_report_summary_hint", _summary, ReportAnswers.SummaryMax, 0f, t => _summary = t);
        var detail = Box("emp_ui_report_detail", "emp_ui_report_detail_hint", _detail, ReportAnswers.DetailMax, 170f, t => _detail = t);
        var again = card.DropdownRow("emp_ui_report_again".lang(),
            [.."yes,no,untried".Split(',').Select(o => $"emp_ui_report_again_{o}".lang())], i => _again = i, _again);

        // what goes with it (the game's "Attach a screenshot" / "Attach a save data" boxes); the logs always do
        // the video and the picture come from the screen recording (ReplayBuffer): ticking either turns it on, both
        // off turns it off (UX audit 2026-11-01: the boxes stayed grey until a box on the other page was ticked)
        var replay = EmpConfig.Client.RecordReplay;
        UIButton? videoBox = null;
        UIButton? pictureBox = null;
        // (nothing recorded: nothing ticked, so ticking one box does not tick the other too)
        if (!replay.Value) {
            _video = _picture = false;
        }

        void Attach()
        {
            var on = _video || _picture;
            if (replay.Value != on) {
                replay.Value = on;
            }

            videoBox?.SetCheck(_video && on);
            pictureBox?.SetCheck(_picture && on);
        }

        var recording = replay.Value ? null : "emp_ui_report_att_needs_recording".lang();
        videoBox = card.ToggleRow("emp_ui_report_att_video".lang(), recording, _video && replay.Value, v => {
            _video = v;
            Attach();
        });
        videoBox.SetTooltipLang("emp_tip_cfg_record_replay".lang());
        pictureBox = card.ToggleRow("emp_ui_report_att_picture".lang(), null, _picture && replay.Value, v => {
            _picture = v;
            Attach();
        });
        card.ToggleRow("emp_ui_report_att_logs".lang(), "emp_ui_report_att_logs_desc".lang(), true, _ => { }, readOnly: true);

        // the right page: thanks and short tips, as the game's window has them
        var tips = right.Card("emp_ui_report_thanks".lang());
        // (what is removed from the logs is said once, under the buttons: emp_ui_report_send_desc)
        foreach (var tip in new[] { "before", "one", "lang", "host" }) {
            tips.Text(("emp_ui_report_tip_" + tip).lang());
        }

        tips.TextSmall("emp_ui_report_send_desc".lang(), FontColor.Gray);
        tips.TextSmall("emp_ui_report_desc".lang(), FontColor.Gray);

        // Send and Save on the right page, under the words that explain them: at the bottom of the left page the form
        // pushed them under the window's edge at 1280x720 (a guest on the public server, 0.26.1576)
        var buttons = tips.ButtonRow();
        send = buttons.RowButton("emp_ui_report_send".lang(), () => {
            var answers = Draft;
            if (answers.Missing() is not null) {
                return;
            }

            // (each box's change clears its part of the draft)
            summary.Text = "";
            detail.Text = "";
            _summary = _detail = "";
            _category = 0;
            _again = 2;
            category.value = 0;
            again.value = 2;
            Refresh();
            EmpPop.Information("emp_ui_report_sending".lang());
            BugReport.Send(answers, url => {
                if (url is null) {
                    EmpPop.Information("emp_ui_report_page".lang());
                    return;
                }

                Dialog.YesNo("emp_ui_report_sent".Loc(url), () => Application.OpenURL(url));
            });
        });
        buttons.RowButton("emp_ui_report".lang(), () => {
            EmpPop.Information("emp_ui_report_preparing".lang());
            BugReport.Prepare(Draft, path => {
                if (path is not null) {
                    EmpPop.Information("emp_ui_report_done".Loc(path));
                    Util.ShowExplorer(path);
                } else {
                    EmpPop.Information("emp_ui_report_failed".lang());
                }
            });
        });
        hint = tips.TextSmall("", FontColor.Gray);
        Refresh();
        UIHelper.WrapTexts(right);
    }

    /// <summary>
    ///     The game's one-line input field made a box of several lines: its height, its text wrapped from the top left
    ///     (it showed one centred line that ran out sideways, whatever was typed)
    /// </summary>
    internal static void TextBox(UIInputText input, float height)
    {
        var field = input.field;
        field.lineType = UnityEngine.UI.InputField.LineType.MultiLineNewline;
        var element = input.LayoutElement();
        element.minHeight = element.preferredHeight = height;
        element.flexibleWidth = 1f;
        ((RectTransform)input.transform).SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height);
        if (field.transform != input.transform) {
            var rect = (RectTransform)field.transform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
        }

        foreach (var text in new[] { field.textComponent, field.placeholder as UnityEngine.UI.Text }) {
            if (text is null) {
                continue;
            }

            text.alignment = TextAnchor.UpperLeft;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            var rt = text.rectTransform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new(10, 8);
            rt.offsetMax = new(-10, -8);
        }
    }
}
