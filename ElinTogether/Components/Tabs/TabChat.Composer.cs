using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ElinTogether.Helper;
using ElinTogether.LangMod;
using UnityEngine;
using UnityEngine.UI;
using YKF;

namespace ElinTogether.Components;

/// <summary>
///     The Chat tab's writing row beyond the box: the emote picker, and the small panel over the row that shows it
///     (drawn over the bottom of the log, never laid out with the page: nothing moves when it opens)
/// </summary>
internal partial class TabChat
{
    private YKHorizontal? _writer;
    private RectTransform? _popup;
    private string _popupKind = "";
    // where the caret was while the box had the focus (a click elsewhere takes the focus, and the caret with it)
    private int _caret;
    private float _focusedAt = -10f;

    // the Chat tab shown, for Shift+click in the inventory (ChatItemLink)
    private static TabChat? _active;

    private void OnDisable()
    {
        if (_active == this) {
            _active = null;
        }

        ClosePopup();
    }

    /// <summary>
    ///     A Shift+click on a thing of the inventory while the Chat box has (or had a moment ago: the click took it)
    ///     the focus puts a link to that thing in the line, in place of the game's Shift+click
    /// </summary>
    internal static bool TryLink(Thing? thing)
    {
        if (thing == null || _active is not { isActiveAndEnabled: true, _field: not null } tab ||
            Time.unscaledTime - tab._focusedAt > 1f) {
            return false;
        }

        tab.Insert(ChatMarkup.LinkOf(thing));
        return true;
    }

    /// <summary>The same test without the link: the game asks first whether the click may happen at all</summary>
    internal static bool WouldLink(Thing? thing) =>
        thing != null && _active is { isActiveAndEnabled: true, _field: not null } tab && Time.unscaledTime - tab._focusedAt <= 1f;

    private void UpdateComposer()
    {
        if (_field == null) {
            return;
        }

        _active = this;
        if (_field.isFocused) {
            _caret = _field.caretPosition;
            _focusedAt = Time.unscaledTime;
        }

        if (_popup != null && Input.GetKeyDown(KeyCode.Escape)) {
            ClosePopup();
        }

        // (Tab writes the first name the list offers)
        if (_popupKind == "mention" && _mentionChoices.Count > 0 && Input.GetKeyDown(KeyCode.Tab)) {
            CompleteMention(_mentionChoices[0]);
        }
    }

    // ---------------------------------------------------------------- the panel over the writing row

    /// <summary>
    ///     An empty panel over the writing row, at its left, sized to what it gets; the one shown before is closed
    /// </summary>
    private RectTransform? Popup(string kind)
    {
        ClosePopup();
        if (_writer == null) {
            return null;
        }

        var go = new GameObject("ChatPopup", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(LayoutElement), typeof(ContentSizeFitter));
        go.transform.SetParent(_writer.transform, false);
        go.GetComponent<LayoutElement>().ignoreLayout = true;
        var fitter = go.GetComponent<ContentSizeFitter>();
        fitter.horizontalFit = fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        var back = go.GetComponent<Image>();
        back.sprite = UIHelper.FindSprite("buttonBig");
        back.type = Image.Type.Sliced;
        back.color = new(1f, 0.97f, 0.9f, 0.98f);
        var rt = (RectTransform)go.transform;
        rt.anchorMin = rt.anchorMax = new(0f, 1f);
        rt.pivot = new(0f, 0f);
        rt.anchoredPosition = new(0f, 4f);
        _popup = rt;
        _popupKind = kind;
        return rt;
    }

    private void ClosePopup()
    {
        if (_popup != null) {
            Destroy(_popup.gameObject);
        }

        foreach (var sprite in _popupSprites) {
            TeamEmblemBaker.Free(sprite);
        }

        _popupSprites.Clear();

        _popup = null;
        _popupKind = "";
    }

    // ---------------------------------------------------------------- emotes

    private void BuildEmoteButton(YKHorizontal row)
    {
        row.IconButton(ChatMarkup.EmoteSprite(nameof(Emo.happy)), ":)", ToggleEmotes, "emp_tip_chat_emotes", size: UIHelper.RowHeight);
    }

    /// <summary>The game's emotes in a grid; a click puts its code where the caret was</summary>
    private void ToggleEmotes()
    {
        if (_popupKind == "emotes") {
            ClosePopup();
            return;
        }

        if (Popup("emotes") is not { } panel) {
            return;
        }

        var grid = panel.gameObject.AddComponent<GridLayoutGroup>();
        grid.cellSize = new(34f, 34f);
        grid.spacing = new(2f, 2f);
        grid.padding = new(8, 8, 8, 8);
        grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
        grid.constraintCount = 9;
        foreach (var name in ChatMarkup.Emotes) {
            var cell = new GameObject("Emote_" + name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
            cell.transform.SetParent(panel, false);
            var image = cell.GetComponent<Image>();
            image.sprite = ChatMarkup.EmoteSprite(name);
            image.preserveAspect = true;
            var code = $":{name}:";
            cell.GetComponent<Button>().onClick.AddListener(() => {
                SE.ClickGeneral();
                Insert(code);
                ClosePopup();
            });
        }
    }

    // ---------------------------------------------------------------- drawings

    // the thumbnails of the drawings' panel, freed when it closes
    private readonly List<Sprite> _popupSprites = [];
    private const int DrawingsPerPage = 17;

    /// <summary>
    ///     The picture button: My drawings to pick one to send. Greyed with the reason while the host keeps pictures
    ///     out of the chat
    /// </summary>
    private void BuildDrawingButton(YKHorizontal row)
    {
        var allowed = ChatImages.Allowed;
        row.IconButton(EditorIcons.Tool(2), "[]", () => {
                if (_popupKind == "drawings") {
                    ClosePopup();
                } else {
                    ShowDrawings();
                }
            }, allowed ? "emp_tip_chat_drawings" : "emp_ui_chat_images_off",
            allowed, size: UIHelper.RowHeight).name = "ChatDrawingsButton";
    }

    /// <summary>My drawings in a grid, newest first, a page at a time; a click sends that one where the line would go</summary>
    private void ShowDrawings(int page = 0)
    {
        if (!ChatImages.Allowed) {
            Tell("emp_ui_chat_images_off".lang());
            return;
        }

        var all = DrawingStore.All;
        if (all.Count == 0) {
            if (ListPopup("drawings") is { } empty) {
                empty.Text("emp_ui_chat_drawings_none".lang()).LayoutElement().preferredWidth = 360f;
                // (a drawing made here, in the editor's window: it is sent from there or from this list)
                var draw = empty.Button("emp_ui_editor_new_drawing".lang(), () => {
                    ClosePopup();
                    DrawingsPage.NewDrawing();
                });
                draw.name = "ChatNewDrawing";
                draw.LayoutElement().minHeight = draw.LayoutElement().preferredHeight = UIHelper.RowHeight;
            }

            return;
        }

        if (Popup("drawings") is not { } panel) {
            return;
        }

        page = Mathf.Clamp(page, 0, (all.Count - 1) / DrawingsPerPage);
        var grid = panel.gameObject.AddComponent<GridLayoutGroup>();
        grid.cellSize = new(46f, 46f);
        grid.spacing = new(4f, 4f);
        grid.padding = new(8, 8, 8, 8);
        grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
        grid.constraintCount = 6;
        // the first cell draws a new one in the editor's window
        var add = new GameObject("ChatNewDrawing", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
        add.transform.SetParent(panel, false);
        add.GetComponent<Image>().color = new(0.28f, 0.22f, 0.13f, 0.15f);
        var plus = new GameObject("Icon", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        plus.transform.SetParent(add.transform, false);
        plus.GetComponent<Image>().sprite = EditorIcons.Tool(EmblemEditor.ToolBrush);
        plus.GetComponent<Image>().preserveAspect = true;
        ((RectTransform)plus.transform).sizeDelta = new(24f, 24f);
        add.GetComponent<Button>().onClick.AddListener(() => {
            SE.ClickGeneral();
            ClosePopup();
            DrawingsPage.NewDrawing();
        });
        foreach (var (_, drawing) in all.Skip(page * DrawingsPerPage).Take(DrawingsPerPage)) {
            Sprite sprite;
            try {
                var tex = Drawing.Bake(drawing);
                sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
            } catch (Exception e) {
                EmpLog.Debug(e, "Chat: a drawing of My drawings could not be baked");
                continue;
            }

            _popupSprites.Add(sprite);
            var cell = new GameObject("Drawing", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
            cell.transform.SetParent(panel, false);
            var image = cell.GetComponent<Image>();
            image.sprite = sprite;
            image.preserveAspect = true;
            var pick = drawing;
            cell.GetComponent<Button>().onClick.AddListener(() => {
                SE.ClickGeneral();
                ClosePopup();
                SendDrawing(pick);
            });
        }

        // (more than a page: the last cells turn the pages)
        if (all.Count > DrawingsPerPage) {
            foreach (var (sign, to) in new[] { ("<", page - 1), (">", page + 1) }) {
                var cell = new GameObject("Page" + sign, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
                cell.transform.SetParent(panel, false);
                cell.GetComponent<Image>().color = new(0.28f, 0.22f, 0.13f, 0.15f);
                var label = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
                label.transform.SetParent(cell.transform, false);
                var text = label.GetComponent<Text>();
                text.text = sign;
                // (the writing box's own font)
                text.font = _field != null && _field.textComponent != null ? _field.textComponent.font : Font.CreateDynamicFontFromOSFont("Arial", 22);
                text.fontSize = 22;
                text.alignment = TextAnchor.MiddleCenter;
                text.color = new(0.2f, 0.15f, 0.1f);
                var lrt = (RectTransform)label.transform;
                (lrt.anchorMin, lrt.anchorMax, lrt.offsetMin, lrt.offsetMax) = (Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
                var button = cell.GetComponent<Button>();
                button.interactable = to >= 0 && to * DrawingsPerPage < all.Count;
                var target = to;
                button.onClick.AddListener(() => {
                    SE.ClickGeneral();
                    ShowDrawings(target);
                });
            }
        }
    }

    /// <summary>A drawing to where the line would go: everyone (or this map) or the team; never a private line</summary>
    private void SendDrawing(Drawing drawing)
    {
        var target = Targets().ElementAtOrDefault(_target);
        if (target.To != 0) {
            Tell("emp_ui_chat_drawings_private".lang());
            return;
        }

        if (ChatImages.SendDrawing(drawing, target.Team) is { } why) {
            Tell(why);
        }
    }

    /// <summary>Text put into the box where the caret was, with a space around it, the box focused again after it</summary>
    private void Insert(string code)
    {
        if (_field == null) {
            return;
        }

        var text = _field.text;
        var at = Mathf.Clamp(_caret, 0, text.Length);
        var before = at > 0 && text[at - 1] != ' ' ? " " : "";
        var after = at < text.Length && text[at] == ' ' ? "" : " ";
        var add = before + code + after;
        if (text.Length + add.Length > _field.characterLimit) {
            SE.BeepSmall();
            return;
        }

        _field.text = text.Insert(at, add);
        _caret = at + add.Length;
        _field.ActivateInputField();
        StartCoroutine(PutCaret(_caret));
    }

    // (the box takes its focus at the next frame: the caret is put after that)
    private IEnumerator PutCaret(int at)
    {
        yield return null;
        if (_field != null) {
            _field.caretPosition = _field.selectionAnchorPosition = _field.selectionFocusPosition = Mathf.Clamp(at, 0, _field.text.Length);
        }
    }

    // ---------------------------------------------------------------- what is being typed

    // "@" and the start of a name at the end of the line
    private static readonly Regex MentionTail = new(@"(?:^|\s)@([^\s@]*)$", RegexOptions.Compiled);
    private List<string> _mentionChoices = [];

    private void OnDraftChanged(string text)
    {
        if (MentionTail.Match(text) is { Success: true } mention) {
            ShowMentions(mention.Groups[1].Value);
            return;
        }

        // (a "/" starting the line, the command not written yet: what the commands are)
        if (text.StartsWith("/") && !text.Contains(' ')) {
            if (_popupKind != "help" && ListPopup("help") is { } list) {
                foreach (var line in HelpLines()) {
                    list.Text(line).LayoutElement().preferredWidth = 420f;
                }
            }

            return;
        }

        if (_popupKind is "mention" or "help") {
            ClosePopup();
        }
    }

    private static IEnumerable<string> HelpLines() => [
        "emp_ui_chat_help_me".lang(),
        "emp_ui_chat_help_w".lang(),
        "emp_ui_chat_help_more".lang(),
    ];

    /// <summary>
    ///     A line starting with "/": /w name words sends a private line to that player, /me goes out as it is (every
    ///     game draws it as an action, ChatMarkup), anything else tells the commands. True when the line is to be sent
    /// </summary>
    private bool RunCommand(string text)
    {
        if (!text.StartsWith("/") || ChatMarkup.IsAction(text)) {
            return true;
        }

        if (text.StartsWith("/w ", StringComparison.OrdinalIgnoreCase)) {
            var rest = text.Substring(3).TrimStart();
            if (!PlayerWhisper.Enabled) {
                Tell("emp_ui_chat_cmd_private_off".lang());
                return false;
            }

            // (the longest player name the words start with: a name may hold spaces)
            // (the name alone, "/w Bob", is a line with no words: said as such, not as an unknown player)
            var to = PrivateTargets().Where(t => t.Name.Length > 0 && (rest.StartsWith(t.Name + " ", StringComparison.OrdinalIgnoreCase) ||
                                                                       string.Equals(rest, t.Name, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(t => t.Name.Length).FirstOrDefault();
            if (to.Uid == 0) {
                var asked = rest.Split(' ')[0];
                Tell("emp_ui_chat_cmd_no_player".Loc(asked.Length > 0 ? asked : "?"));
                return false;
            }

            var words = rest.Substring(to.Name.Length).Trim();
            if (words.Length == 0) {
                Tell("emp_ui_chat_cmd_empty".Loc(to.Name));
            } else {
                PlayerWhisper.Send(to.Uid, to.Name, words);
            }

            return false;
        }

        Tell("emp_ui_chat_cmd_unknown".Loc(text.Split(' ')[0]) + " " + string.Join("  ", HelpLines()));
        return false;
    }

    // a line for this player only, in its log (a command it got wrong)
    private static void Tell(string text) => ChatLog.Add(ChatLog.Server, "/", text, true);

    /// <summary>A list over the writing row, for the mention names and the commands' help</summary>
    private YKVertical? ListPopup(string kind)
    {
        if (_writer == null || Popup(kind) is not { } panel) {
            return null;
        }

        var group = panel.gameObject.AddComponent<VerticalLayoutGroup>();
        group.padding = new(10, 10, 6, 6);
        group.childControlWidth = group.childControlHeight = true;
        group.childForceExpandWidth = group.childForceExpandHeight = false;
        // (made by the row's layout, then moved into the panel, as EmpUi.ScrollList does)
        var list = _writer.Vertical();
        list.transform.SetParent(panel, false);
        list.Layout.spacing = 2f;
        return list;
    }

    /// <summary>The players whose name starts with what follows the "@"; a click or Tab writes the whole name</summary>
    private void ShowMentions(string partial)
    {
        var names = PrivateTargets().Select(t => t.Name).Where(n => n.Length > 0).Distinct()
            .Where(n => n.StartsWith(partial, StringComparison.OrdinalIgnoreCase) && !string.Equals(n, partial, StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Take(6).ToList();
        if (names.Count == 0) {
            if (_popupKind == "mention") {
                ClosePopup();
            }

            return;
        }

        if (_popupKind == "mention" && names.SequenceEqual(_mentionChoices)) {
            return;
        }

        if (ListPopup("mention") is not { } list) {
            return;
        }

        _mentionChoices = names;
        foreach (var name in names) {
            var button = list.Button("@" + name, () => CompleteMention(name));
            var element = button.LayoutElement();
            element.minWidth = element.preferredWidth = 220f;
            element.minHeight = element.preferredHeight = 28f;
        }
    }

    private void CompleteMention(string name)
    {
        if (_field == null || MentionTail.Match(_field.text) is not { Success: true } mention) {
            return;
        }

        var at = mention.Groups[1].Index - 1;
        _field.text = _field.text.Substring(0, at) + "@" + name + " ";
        ClosePopup();
        _caret = _field.text.Length;
        _field.ActivateInputField();
        StartCoroutine(PutCaret(_caret));
    }
}
