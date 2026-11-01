using ElinTogether.Helper;
using UnityEngine;
using UnityEngine.UI;

namespace ElinTogether.Components;

/// <summary>
///     A team's emblem seen up close (lead dev 2026-11-01): a click on an emblem in the team list, a player's card or
///     the leaderboard dims the window and shows it at 256 px with the team's name under it; a click anywhere closes it
/// </summary>
internal static class EmblemZoom
{
    private const float Size = 256f;

    private static GameObject? _open;

    /// <summary>The emblem image opens the large view of that team's emblem when clicked</summary>
    internal static void Zoomable(Image image, int teamId)
    {
        image.raycastTarget = true;
        var button = image.gameObject.AddComponent<Button>();
        button.transition = Selectable.Transition.None;
        button.onClick.AddListener(() => {
            SE.ClickGeneral();
            Show(image, teamId);
        });
    }

    internal static void Close()
    {
        if (_open != null) {
            Object.Destroy(_open);
        }

        _open = null;
    }

    /// <summary>Over the whole canvas of <paramref name="from" />: the emblem as the team has it now, its name</summary>
    internal static void Show(Graphic from, int teamId)
    {
        Close();
        var sprite = TeamBanner.EmblemSprite(teamId);
        if (sprite == null || from.canvas == null) {
            return;
        }

        // (inside the window's layer: it goes when the window closes)
        var root = from.GetComponentInParent<global::Layer>() is { } layer ? layer.transform : from.canvas.rootCanvas.transform;
        var dim = new GameObject("EmblemZoom", typeof(RectTransform), typeof(Image), typeof(Button));
        dim.transform.SetParent(root, false);
        dim.transform.SetAsLastSibling();
        var rt = (RectTransform)dim.transform;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
        dim.GetComponent<Image>().color = new(0f, 0f, 0f, 0.6f);
        var close = dim.GetComponent<Button>();
        close.transition = Selectable.Transition.None;
        close.onClick.AddListener(Close);

        var column = new GameObject("Column", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
        column.transform.SetParent(dim.transform, false);
        var crt = (RectTransform)column.transform;
        crt.anchorMin = crt.anchorMax = crt.pivot = new(0.5f, 0.5f);
        var layout = column.GetComponent<VerticalLayoutGroup>();
        layout.childAlignment = TextAnchor.MiddleCenter;
        layout.spacing = 12f;
        layout.childControlWidth = layout.childControlHeight = true;
        layout.childForceExpandWidth = layout.childForceExpandHeight = false;
        var fitter = column.GetComponent<ContentSizeFitter>();
        fitter.horizontalFit = fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        TeamEmblemBaker.Place(column.transform, sprite, Size);

        // the team's name in the game's own font, at the size of its window titles
        var label = new GameObject("Name", typeof(RectTransform), typeof(Text), typeof(Shadow));
        label.transform.SetParent(column.transform, false);
        var text = label.GetComponent<Text>();
        var sample = from.canvas.rootCanvas.GetComponentInChildren<Text>(true);
        text.font = sample != null ? sample.font : Font.CreateDynamicFontFromOSFont("Arial", 22);
        text.fontSize = Mathf.Max(22, sample != null ? sample.fontSize + 4 : 22);
        text.color = Color.white;
        text.alignment = TextAnchor.MiddleCenter;
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        text.raycastTarget = false;
        text.text = Teams.NameOf(teamId);
        _open = dim;
    }
}
