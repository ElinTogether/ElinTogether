using System.Collections.Generic;
using System.Linq;
using ElinTogether.Helper;
using ElinTogether.LangMod;
using ElinTogether.Net;
using UnityEngine;
using UnityEngine.UI;
using YKF;

namespace ElinTogether.Components;

/// <summary>
///     The Chat tab (user 2026-10-30): the lines this game was shown since it joined (ChatLog), filtered by channel, and
///     a box to write to everyone, to one's team or to one player. A line that arrives is added under the others; the
///     tab is never built again for it, so a line being typed keeps its text and its focus
/// </summary>
internal class TabChat : TabEmpBase
{
    public const string TabName = "emp_tab_chat";

    // kept over reopenings while the game runs, never stored
    private static int _filter = -1;
    private static string _draft = "";
    private static int _target;

    private YKVertical? _list;
    private ScrollRect? _scroll;
    private int _shown = -1;
    private int _count;
    // the writing box, focused by the empty list's button
    private InputField? _field;

    public override void OnLayout()
    {
        if (Waits()) {
            return;
        }

        var card = this.Card("emp_ui_tab_chat".lang());
        if (!NetSession.Instance.InGame) {
            TabTeam.EmptyPage(card, ["icon_menu_16"], "emp_ui_chat_off".lang());
            return;
        }

        card.DropdownRow("emp_ui_chat_show".lang(), [..Channels().Select(c => ChannelName(c))], i => {
            _filter = Channels()[i];
            Refill();
        }, Mathf.Max(0, Channels().IndexOf(_filter)));

        // (a fixed frame that takes the room the page leaves, once: a line that arrives only fills it)
        _list = card.ScrollList(330f, true);
        _scroll = _list.GetComponentInParent<ScrollRect>();
        Refill();

        BuildWriter(card);
    }

    // the filters this player has: a team filter only in a team
    private static List<int> Channels()
    {
        var list = new List<int> { -1, ChatLog.All };
        if (InTeam()) {
            list.Add(ChatLog.Team);
        }

        list.Add(ChatLog.Private);
        list.Add(ChatLog.Server);
        return list;
    }

    private static bool InTeam() => NetSession.Instance.Rules.UseTeams && Teams.TeamOf(EClass.pc.uid) != Teams.Wanderers;

    private static string ChannelName(int channel) => channel switch {
        ChatLog.All => (NetSession.Instance.Rules.GlobalChat ? "emp_ui_chat_everyone" : "emp_ui_chat_map").lang(),
        ChatLog.Team => "emp_ui_chat_team".lang(),
        ChatLog.Private => "emp_ui_chat_private".lang(),
        ChatLog.Server => "emp_ui_chat_server".lang(),
        _ => "emp_ui_chat_all".lang(),
    };

    // where a line can go: everyone (or this map), the team, then each other player online when private lines are on:
    // every player of the server, on another map too (the host passes the line on, user 2026-10-30)
    private static List<(string Label, int To, string? Name, bool Team)> Targets()
    {
        var list = new List<(string, int, string?, bool)> { (ChannelName(ChatLog.All), 0, null, false) };
        if (InTeam()) {
            list.Add(("emp_ui_chat_team".lang(), 0, null, true));
        }

        if (NetSession.Instance.Rules.PrivateChat) {
            foreach (var (uid, name) in PrivateTargets().OrderBy(t => t.Name, System.StringComparer.OrdinalIgnoreCase)) {
                list.Add(("emp_ui_chat_to".Loc(name), uid, name, false));
            }
        }

        return list;
    }

    // the other players a private line can go to: the server's roster, or this map's players before one arrived
    private static IEnumerable<(int Uid, string Name)> PrivateTargets()
    {
        var session = NetSession.Instance;
        var me = EClass.pc.uid;
        if (session.ServerRoster.Count > 0) {
            return session.ServerRoster.Where(p => p.CharaUid != me)
                .Select(p => (p.CharaUid, string.IsNullOrWhiteSpace(p.Name) ? p.CharaName : p.Name)).ToList();
        }

        return session.VisiblePlayers.Where(p => p.CharaUid != me && Teams.FindChara(p.CharaUid) is not null)
            .Select(p => (p.CharaUid, p.User.Name.IsEmpty(Teams.FindChara(p.CharaUid)!.Name))).ToList();
    }

    private void BuildWriter(YKLayout card)
    {
        var targets = Targets();
        _target = Mathf.Clamp(_target, 0, targets.Count - 1);
        var row = card.Horizontal();
        row.Layout.childForceExpandWidth = false;
        row.Layout.childAlignment = TextAnchor.MiddleLeft;
        row.Layout.spacing = 8f;

        var to = row.Dropdown([..targets.Select(t => t.Label)], i => _target = i, _target);
        to.LayoutElement().preferredWidth = 170f;
        to.LayoutElement().flexibleWidth = 0f;

        var box = row.Vertical();
        box.LayoutElement().flexibleWidth = 1f;
        box.LayoutElement().preferredWidth = 0f;
        var input = box.InputText(_draft);
        input.field.characterLimit = 500;
        input.AllowAnyText();
        input.StyleAsSearch("emp_ui_chat_hint".lang());
        input.field.onValueChanged.AddListener(text => _draft = text);
        _field = input.field;

        void Send()
        {
            var text = input.field.text.Trim();
            if (text.Length == 0) {
                return;
            }

            var target = Targets().ElementAtOrDefault(_target);
            if (target.Team) {
                ChatLog.SendTeam(text);
            } else if (target.To != 0) {
                PlayerWhisper.Send(target.To, target.Name, text);
            } else {
                // (the way the game's own chat box sends a line: the player says it on the map, the mod passes it on)
                ActionMode.Adv.OnEnterChat(text);
            }

            _draft = "";
            input.field.text = "";
            input.field.ActivateInputField();
        }

        // (Enter sends, as in the game's chat box)
        input.field.onEndEdit.AddListener(_ => {
            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)) {
                Send();
            }
        });
        row.RowButton("emp_ui_chat_send".lang(), Send).LayoutElement().flexibleWidth = 0f;
    }

    private void Refill()
    {
        if (_list == null) {
            return;
        }

        foreach (Transform child in _list.transform) {
            Destroy(child.gameObject);
        }

        _count = 0;
        _shown = ChatLog.Version;
        foreach (var line in ChatLog.Entries.Where(Shows)) {
            AddLine(line);
        }

        if (_count == 0) {
            _list.Text("emp_ui_chat_empty".lang(), FontColor.Gray);
            // (an empty chat says what to do: the box under the list, focused in one click)
            _list.ButtonRow().RowButton("emp_ui_chat_write_first".lang(), () => {
                if (_field != null) {
                    _field.ActivateInputField();
                }
            });
        }

        ToBottom(true);
    }

    private static bool Shows(ChatLog.Line line) => _filter < 0 || line.Channel == _filter;

    private void AddLine(ChatLog.Line line)
    {
        var tag = line.Channel switch {
            ChatLog.Team => "emp_ui_chat_team".lang(),
            ChatLog.Private => "emp_ui_chat_private".lang(),
            ChatLog.Server => "emp_ui_chat_server".lang(),
            _ => "",
        };
        var head = tag.Length > 0 ? $"[{tag}] ".TagColor(ChatLog.ColorOf(line.Channel)) : "";
        var text = _list!.Text($"{line.Time.TagColor(new Color(0.45f, 0.4f, 0.32f))}  {head}{line.Who}: {line.Text}");
        // (the line wraps at the log's width: its prefab's fitter sized it to the unwrapped line, wider than the log)
        UIHelper.WrapTexts(text);
        _count++;
    }

    // the log is at its newest line (or too short to scroll): a new line keeps it there
    private bool AtBottom() => _scroll == null || _scroll.content == null || _scroll.viewport == null ||
                               _scroll.content.rect.height <= _scroll.viewport.rect.height + 1f ||
                               _scroll.verticalNormalizedPosition <= 0.02f;

    // only the log is laid out again (never the window); scrolled to the newest line unless the player scrolled up
    private void ToBottom(bool stick)
    {
        if (_list != null) {
            LayoutRebuilder.ForceRebuildLayoutImmediate((RectTransform)_list.transform);
        }

        if (stick && _scroll != null) {
            _scroll.verticalNormalizedPosition = 0f;
        }
    }

    // a line that arrived while the tab is open: added under the others (the newest at the bottom, scrolled to)
    private void Update()
    {
        if (_list == null) {
            return;
        }

        // (shown: every line is read)
        ChatLog.MarkSeen();
        if (_shown == ChatLog.Version) {
            return;
        }

        var entries = ChatLog.Entries;
        var added = Mathf.Clamp(ChatLog.Version - _shown, 0, entries.Count);
        _shown = ChatLog.Version;
        if (_count == 0) {
            Refill();
            return;
        }

        var stick = AtBottom();
        foreach (var line in entries.Skip(entries.Count - added).Where(Shows)) {
            AddLine(line);
        }

        ToBottom(stick);
    }
}
