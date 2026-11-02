using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace ElinTogether.Patches;

/// <summary>
///     The bug report's words are written in a real text box (user, 2026-10-27): the game's input dialog, made big,
///     its text wrapped and shown from the top, and Enter starts a new line there instead of closing it (Ctrl+Enter sends)
/// </summary>
[HarmonyPatch]
[AlwaysPatched]
internal static class ReportBoxPatch
{
    private const float Width = 640;
    private const float Height = 320;

    private static Dialog? _box;

    internal static void Enlarge(Dialog dialog)
    {
        _box = dialog;
        var field = dialog.input.field;
        field.lineType = InputField.LineType.MultiLineNewline;

        // the box the dialog lays out (its height is its own: the column above only sets widths), the field fills it
        var holder = (RectTransform)field.transform.parent;
        var layout = holder.GetComponent<LayoutElement>() ?? holder.gameObject.AddComponent<LayoutElement>();
        layout.minWidth = layout.preferredWidth = Width;
        layout.minHeight = layout.preferredHeight = Height;
        holder.sizeDelta = new(Width, Height);
        var rect = (RectTransform)field.transform;
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = rect.offsetMax = Vector2.zero;

        foreach (var text in new[] { field.textComponent, field.placeholder as Text }) {
            if (text is null) {
                continue;
            }

            text.alignment = TextAnchor.UpperLeft;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            text.rectTransform.offsetMin = new(10, 8);
            text.rectTransform.offsetMax = new(-10, -8);
        }

        dialog.RebuildLayout(true);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Dialog), nameof(Dialog.OnUpdateInput))]
    internal static bool OnUpdateInput(Dialog __instance)
    {
        // (the game closes an input dialog on Enter: here it is a new line, typed by the field itself, and
        // Ctrl+Enter sends; the game's input dialog shows no OK button)
        if (__instance != _box || !(Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))) {
            return true;
        }

        return Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
    }
}
