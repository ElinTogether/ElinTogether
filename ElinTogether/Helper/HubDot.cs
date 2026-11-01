using System.Linq;
using ElinTogether.Components;
using ElinTogether.Net;
using ElinTogether.Patches;
using UnityEngine;
using UnityEngine.UI;

namespace ElinTogether.Helper;

/// <summary>
///     A small dot on the Multiplayer button of the HUD while something waits in the window (user 2026-11-01): unread
///     chat lines, letters not opened, a team request or invitation (LayerElinTogether.BadgeOf). Checked twice a
///     second; hidden while the window is open, where the tabs show the counts
/// </summary>
internal static class HubDot
{
    private const string DotName = "EmpHubDot";
    private static float _next;

    internal static void Update()
    {
        if (Time.unscaledTime < _next) {
            return;
        }

        _next = Time.unscaledTime + 0.5f;
        if (!EClass.core.IsGameStarted || Button() is not { } button) {
            return;
        }

        var waiting = NetSession.Instance.InGame && EClass.ui.GetLayer<LayerElinTogether>() == null &&
                      LayerElinTogether.BadgeOf(TabChat.TabName) + LayerElinTogether.BadgeOf(TabPlayers.TabName) +
                      LayerElinTogether.BadgeOf("emp_tab_team") > 0;
        var dot = button.transform.Find(DotName);
        if (dot == null) {
            if (!waiting) {
                return;
            }

            dot = MakeDot(button.transform);
        }

        if (dot.gameObject.activeSelf != waiting) {
            dot.gameObject.SetActive(waiting);
        }
    }

    // the HUD's Multiplayer button, null while the main menu bar is not drawn
    private static ButtonHotItem? Button()
    {
        if (EClass.player?.hotbars?.bars is not { } bars || bars.Length <= Hotbar.IDMainMenu || bars[Hotbar.IDMainMenu] is not { } bar) {
            return null;
        }

        var item = bar.pages.SelectMany(p => p.items).OfType<HotItemLayer>().FirstOrDefault(i => i.id == MultiplayerHubPatch.LayerId);
        return item?.button is { } button && button != null && button.gameObject.activeInHierarchy ? button : null;
    }

    private static Transform MakeDot(Transform button)
    {
        var go = new GameObject(DotName, typeof(RectTransform), typeof(Image), typeof(LayoutElement));
        go.GetComponent<LayoutElement>().ignoreLayout = true;
        var rt = (RectTransform)go.transform;
        rt.SetParent(button, false);
        rt.anchorMin = rt.anchorMax = new(1f, 1f);
        rt.pivot = new(1f, 1f);
        rt.sizeDelta = new(10f, 10f);
        rt.anchoredPosition = new(-2f, -2f);
        var image = go.GetComponent<Image>();
        // (the engine's round UI sprite, as the "changed" dot of World rules)
        image.sprite = UIHelper.FindSprite("Knob");
        image.color = new(0.85f, 0.25f, 0.15f);
        image.raycastTarget = false;
        return rt;
    }
}
