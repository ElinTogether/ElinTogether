using ElinTogether.Components;
using ElinTogether.Helper;
using ElinTogether.LangMod;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace ElinTogether.Patches;

internal class TitleButtonPatch
{
    private const string DotName = "EmpUpdateDot";

    // the title's Multiplayer button, while the title screen is up
    private static UIButton? _button;

    [ElinPostSceneInit]
    internal static void RegisterTitleButton(Scene.Mode mode)
    {
        if (mode != Scene.Mode.Title) {
            return;
        }

        var title = EMono.ui.GetLayer<LayerTitle>();
        if (title == null) {
            return;
        }

        var grid = title.GetComponentInChildren<GridLayoutGroup>();
        if (grid == null) {
            return;
        }

        var button = grid.transform.Find("UIButton");
        if (button == null) {
            return;
        }

        var empButtonGo = Object.Instantiate(button, grid.transform);
        empButtonGo.SetSiblingIndex(button.GetSiblingIndex() + 2);

        var empButton = empButtonGo.GetComponent<UIButton>();
        // "Multiplayer" says what it is to a new player; the mod's name and version are in its tooltip
        empButton.mainText.text = "LayerElinTogether".lang();
        empButton.SetTooltipLang($"{"emp_ui_title".lang()} {ModInfo.Version}");
        empButton.onClick.SetPersistentListenerState(0, UnityEventCallState.Off);
        empButton.onClick.AddListener(() => LayerElinTogether.OpenPanelSesame());
        AddLogo(empButton);
        _button = empButton;
        ShowUpdate();
    }

    /// <summary>
    ///     A newer release (UpdateCheck): a small dot on the button and a line in its tooltip; nothing otherwise
    /// </summary>
    internal static void ShowUpdate()
    {
        if (_button == null) {
            return;
        }

        var newer = UpdateCheck.Newer;
        var tip = $"{"emp_ui_title".lang()} {ModInfo.Version}";
        _button.SetTooltipLang(newer is null ? tip : tip + "\n" + "emp_update_tooltip".Loc(newer));
        var dot = _button.transform.Find(DotName);
        if (newer is null) {
            if (dot != null) {
                dot.gameObject.SetActive(false);
            }

            return;
        }

        if (dot == null) {
            var go = new GameObject(DotName, typeof(RectTransform), typeof(Image), typeof(LayoutElement));
            go.GetComponent<LayoutElement>().ignoreLayout = true;
            var rt = (RectTransform)go.transform;
            rt.SetParent(_button.transform, false);
            rt.anchorMin = rt.anchorMax = new(1f, 1f);
            rt.pivot = new(1f, 1f);
            rt.sizeDelta = new(10f, 10f);
            rt.anchoredPosition = new(-4f, -4f);
            var image = go.GetComponent<Image>();
            // (the round sprite and colour of the HUD button's dot, HubDot)
            image.sprite = UIHelper.FindSprite("Knob");
            image.color = new(0.85f, 0.25f, 0.15f);
            image.raycastTarget = false;
            dot = rt;
        }

        dot.gameObject.SetActive(true);
    }

    /// <summary>
    ///     The mod's logo just before the words of its line, as tall as the line
    /// </summary>
    private static void AddLogo(UIButton button)
    {
        if (MultiplayerHubPatch.Logo is not { } logo || button.mainText is not { } text) {
            return;
        }

        var go = new GameObject("EmpLogo", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(LayoutElement));
        go.GetComponent<LayoutElement>().ignoreLayout = true;
        var rt = (RectTransform)go.transform;
        rt.SetParent(text.transform, false);
        var image = go.GetComponent<Image>();
        image.sprite = logo;
        image.preserveAspect = true;
        image.raycastTarget = false;

        var height = Mathf.Max(16f, text.fontSize * 1.6f);
        rt.sizeDelta = new(height * logo.rect.width / logo.rect.height, height);
        rt.pivot = new(1f, 0.5f);
        // (the line's words are centred in their box: the logo goes where they start)
        var centred = text.alignment is TextAnchor.MiddleCenter or TextAnchor.UpperCenter or TextAnchor.LowerCenter;
        rt.anchorMin = rt.anchorMax = new(centred ? 0.5f : 0f, 0.5f);
        rt.anchoredPosition = new((centred ? -text.preferredWidth / 2f : 0f) - 6f, 0f);
    }
}