using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;
using YKF;

namespace ElinTogether.Helper;

public static class UIHelper
{
    private static readonly Dictionary<string, Sprite> _lookup = [];

    // the settings charter (dev/PLAN_settings_charter.md, rules 9-12): one card, one row, one button for every tab
    public const float RowHeight = 32f;
    public const float RowSpacing = 6f;
    public const float ControlWidth = 320f;
    public const float ButtonWidth = 150f;
    public const int SubIndent = 32;

    public static RectOffset CardPadding => new(20, 20, 12, 12);

    private static int? _labelSize;

    /// <summary>
    ///     The size of the game's own box labels (ButtonToggle, as on its option screens): dropdown labels use it too
    /// </summary>
    public static int LabelSize => _labelSize ??= Resources.Load<UIButton>("UI/Element/Button/ButtonToggle")?.mainText?.size ?? 0;

    /// <summary>
    ///     A box row: the same height as a dropdown row
    /// </summary>
    public static UIButton AsRow(this UIButton toggle)
    {
        toggle.Rect().SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, RowHeight);
        return toggle;
    }

    /// <summary>
    ///     A button at its normal width, never a full-width bar
    /// </summary>
    public static UIButton FixedWidth(this UIButton button)
    {
        var element = button.LayoutElement();
        // the normal width, wider when the label needs it (a long label ran out of its button)
        var wanted = button.mainText != null ? button.mainText.preferredWidth + 48f : 0f;
        element.preferredWidth = Mathf.Max(ButtonWidth, wanted);
        element.flexibleWidth = 0f;
        return button;
    }

    /// <summary>
    ///     A button row on a page of the game's own (UINote): the game's buttons, moved into it, side by side
    /// </summary>
    public static YKHorizontal NoteButtonRow(UINote note)
    {
        var row = YK.Create<YKHorizontal>();
        row.transform.SetParent(note.layout.transform, false);
        row.OnLayout();
        SetUpButtonRow(row);
        return row;
    }

    public static UIButton InRow(this UIButton button, YKHorizontal row)
    {
        button.transform.SetParent(row.transform, false);
        return button.FixedWidth();
    }

    private static void SetUpButtonRow(YKHorizontal row)
    {
        row.Layout.childForceExpandWidth = false;
        row.Layout.childAlignment = TextAnchor.MiddleLeft;
        row.Layout.spacing = 10f;
        // (the game's note texts start a few pixels left of their box: the buttons start where the words do)
        row.Layout.padding = new(-5, 0, 2, 2);
    }

    /// <summary>
    ///     YK Framework's TextSmall/TextMedium come from game prefabs whose ContentSizeFitter sizes the width to the
    ///     unwrapped line, over the card's layout group: long lines were cut at the card's edge (JP, CN, and English at
    ///     1280x800, 2026-10-25). Under this root the group sets the width and the fitter only the height
    /// </summary>
    public static void WrapTexts(Component root)
    {
        foreach (var fitter in root.GetComponentsInChildren<ContentSizeFitter>(true)) {
            if (fitter.horizontalFit != ContentSizeFitter.FitMode.PreferredSize ||
                // (a section header keeps its one line: wrapped, its first words hid above the line)
                fitter.GetComponentInParent<UIHeader>() != null ||
                !fitter.TryGetComponent<Text>(out var text) || text.horizontalOverflow != HorizontalWrapMode.Wrap ||
                fitter.transform.parent?.GetComponent<HorizontalOrVerticalLayoutGroup>() is not { childControlWidth: true }) {
                continue;
            }

            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        }
    }

    /// <summary>
    ///     A flexible part of a row (a title column beside its box, a page of the book, a field beside its button)
    ///     reports a preferred width of 0, so the row gives it what the fixed parts leave and never what its words
    ///     ask for. Its preferred width was the unwrapped width of its longest text: when the words were longer than
    ///     the row, Unity's layout shrank every part of the row by the same share, so a column, a button or a card
    ///     changed width whenever a label or a value changed (lead dev 2026-11-01: "menus change size whenever you
    ///     touch anything"). Only where the row's own width comes from outside (a stretched column, a flexible part
    ///     of a row whose width is given): a row sized to its children keeps them as they are
    /// </summary>
    public static void StableWidths(Component root)
    {
        foreach (var element in root.GetComponentsInChildren<LayoutElement>(true)) {
            if (element.ignoreLayout || element.flexibleWidth <= 0f || element.preferredWidth >= 0f ||
                element.transform.parent is not RectTransform parent ||
                parent.GetComponent<HorizontalLayoutGroup>() is not { enabled: true, childControlWidth: true } ||
                FitsOwnWidth(element.transform) || !WidthIsGiven(parent)) {
                continue;
            }

            element.preferredWidth = 0f;
        }
    }

    // a horizontal fitter on the object: its width is its content's, whatever its parent says
    private static bool FitsOwnWidth(Transform t) =>
        t.GetComponents<ContentSizeFitter>().Any(f => f.enabled && f.horizontalFit != ContentSizeFitter.FitMode.Unconstrained);

    // the width of this object comes from its parent, not from its children
    private static bool WidthIsGiven(Transform t, int depth = 0)
    {
        if (depth > 40 || FitsOwnWidth(t)) {
            return false;
        }

        if (t.parent == null || t.GetComponent<LayoutElement>() is { ignoreLayout: true }) {
            return true;
        }

        // (a scroll view's content, or what it holds directly, as a tab's root: as wide as the view)
        if (t.parent.GetComponentInParent<ScrollRect>() is { } scroll && (scroll.content == t || scroll.content == t.parent)) {
            return !FitsOwnWidth(scroll.content);
        }

        var flexible = t.GetComponent<LayoutElement>() is { flexibleWidth: > 0f };
        return t.parent.GetComponent<HorizontalOrVerticalLayoutGroup>() switch {
            null or { enabled: false } => true,
            VerticalLayoutGroup v => v.childControlWidth && (v.childForceExpandWidth || flexible) && WidthIsGiven(t.parent, depth + 1),
            HorizontalLayoutGroup h => h.childControlWidth && flexible && WidthIsGiven(t.parent, depth + 1),
            _ => false,
        };
    }

    public static Sprite FindSprite(string name)
    {
        if (_lookup.TryGetValue(name, out var sprite)) {
            return sprite;
        }

        sprite = Resources.FindObjectsOfTypeAll<Sprite>()
            .LastOrDefault(s => s.name == name);

        return _lookup[name] = sprite!;
    }

    extension(UIInputText input)
    {
        /// <summary>
        ///     A search box that reads as an input: the game's field background, a visible grey placeholder, one row high
        /// </summary>
        public void StyleAsSearch(string placeholder)
        {
            var field = input.field;
            var le = input.GetComponent<LayoutElement>() ?? input.gameObject.AddComponent<LayoutElement>();
            le.minHeight = le.preferredHeight = RowHeight;
            if (field.targetGraphic is Image back && back.sprite == null) {
                back.sprite = FindSprite("buttonBig");
                back.type = Image.Type.Sliced;
                back.color = new(1f, 1f, 1f, 0.55f);
            }

            var hint = field.placeholder as Text;
            if (hint == null) {
                var template = field.textComponent;
                var go = new GameObject("Placeholder", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
                go.transform.SetParent(template.transform.parent, false);
                var rt = (RectTransform)go.transform;
                var src = template.rectTransform;
                (rt.anchorMin, rt.anchorMax, rt.offsetMin, rt.offsetMax) = (src.anchorMin, src.anchorMax, src.offsetMin, src.offsetMax);
                hint = go.GetComponent<Text>();
                hint.font = template.font;
                hint.fontSize = template.fontSize;
                hint.alignment = template.alignment;
                field.placeholder = hint;
            }

            hint.text = placeholder;
            hint.fontStyle = FontStyle.Italic;
            hint.color = new(0.35f, 0.3f, 0.22f, 0.75f);
            hint.raycastTarget = false;
            hint.gameObject.SetActive(true);
            if (field.textComponent != null) {
                field.textComponent.color = new(0.15f, 0.1f, 0.05f, 1f);
            }
        }

        // the game's text box prefab may filter what is typed (issue #29: no space in the bug report): free text
        public void AllowAnyText()
        {
            input.field.contentType = InputField.ContentType.Standard;
            input.field.inputType = InputField.InputType.Standard;
            input.field.characterValidation = InputField.CharacterValidation.None;
        }
    }

    extension(YKLayout layout)
    {
        public UIInputText AddPair(string idLang, string text)
        {
            var pair = layout.Horizontal();
            pair.Layout.childForceExpandWidth = true;

            pair.Text(idLang);
            var input = pair.InputText(text);

            input.type = UIInputText.Type.Name;
            input.field.characterLimit = 150;
            input.AllowAnyText();

            input.Text = text;

            return input;
        }

        public YKVertical MakeCard()
        {
            var card = layout.Vertical();
            card.LayoutElement().flexibleWidth = 1f;
            card.Fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            card.Layout.padding = CardPadding;
            card.Layout.spacing = RowSpacing;

            var cardBg = card.Layout.gameObject.AddComponent<Image>();
            cardBg.sprite = FindSprite("buttonBig");
            cardBg.type = Image.Type.Sliced;

            return card;
        }

        /// <summary>
        ///     One setting: its row and the lines under it, close together (rows are RowSpacing apart in the card);
        ///     indented under the choice it belongs to
        /// </summary>
        public YKVertical RowGroup(bool indented = false)
        {
            var group = layout.Vertical();
            group.Fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            if (indented) {
                group.Layout.padding = new(SubIndent, 0, 0, 0);
            }
            return group;
        }

        /// <summary>
        ///     A dropdown row: the label takes the free width, the dropdown a fixed width on the right, so that every
        ///     dropdown of a card starts at the same place
        /// </summary>
        public UIDropdown DropdownRow(string label, List<string> options, Action<int> onPick, int value)
        {
            var row = layout.Horizontal();
            row.Layout.childForceExpandWidth = false;
            row.Layout.childAlignment = TextAnchor.MiddleLeft;
            row.Layout.spacing = 10f;

            var text = row.Text(label);
            text.SetSize(LabelSize);
            // (the room the dropdown leaves, at least enough for a short label: never the width of its words, which
            // made the dropdown narrower or wider with the label's language)
            text.LayoutElement().flexibleWidth = 1f;
            text.LayoutElement().preferredWidth = 0f;
            text.LayoutElement().minWidth = 120f;

            var dropdown = row.Dropdown(options, onPick, value);
            var element = dropdown.LayoutElement();
            element.preferredWidth = ControlWidth;
            element.flexibleWidth = 0f;
            dropdown.Rect().SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, RowHeight);
            return dropdown;
        }

        /// <summary>
        ///     Buttons side by side, left-aligned, each FixedWidth (danger buttons last)
        /// </summary>
        public YKHorizontal ButtonRow()
        {
            var row = layout.Horizontal();
            SetUpButtonRow(row);
            return row;
        }

        public UIButton RowButton(string text, Action action)
        {
            return layout.Button(text, action).FixedWidth();
        }

        public UIItem AddImageCard(Component parent, Sprite sprite)
        {
            var item = Util.Instantiate<UIItem>("UI/Element/Deco/ImageNote", parent);
            var bg = item.image1;
            var rt = bg.rectTransform;

            bg.sprite = sprite;
            if (bg.sprite != null) {
                bg.SetNativeSize();
                (rt.parent as RectTransform)?.sizeDelta = rt.sizeDelta;
            }
            return item;
        }
    }
}