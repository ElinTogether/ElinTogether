using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ElinTogether.Helper;
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

    private void UpdateComposer()
    {
        if (_field == null) {
            return;
        }

        if (_field.isFocused) {
            _caret = _field.caretPosition;
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

        if (_popupKind == "mention") {
            ClosePopup();
        }
    }

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
