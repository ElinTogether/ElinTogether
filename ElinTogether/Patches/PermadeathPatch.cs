using ElinTogether.Helper;
using ElinTogether.Net;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace ElinTogether.Patches;

/// <summary>
///     The dead player's banner (UX audit 2026-11-01): the wait for a revive said so only in one log line, which
///     scrolled away. A box at the top of the screen says it for as long as it lasts, with a button to leave (the
///     game's own "back to the title" question). Not a layer: the windows, the Escape menu and its keys work as before
///     and open over it
/// </summary>
internal static class DeadBanner
{
    private static GameObject? _banner;

    internal static void Update(bool show)
    {
        if (!show) {
            if (_banner != null) {
                UnityEngine.Object.Destroy(_banner);
            }

            _banner = null;
            return;
        }

        if (_banner == null && EClass.ui is { } ui && ui.rectLayers != null) {
            _banner = Make(ui);
        }
    }

    private static GameObject Make(UI ui)
    {
        var font = SkinManager.Instance?.fontSet?.ui?.source?.font;
        var go = new GameObject("EmpDeadBanner", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image),
            typeof(HorizontalLayoutGroup), typeof(ContentSizeFitter));
        var rt = (RectTransform)go.transform;
        rt.SetParent(ui.transform, false);
        // (under the windows: a dialog or the Escape menu opens over it)
        if (ui.rectLayers.parent == ui.transform) {
            rt.SetSiblingIndex(ui.rectLayers.GetSiblingIndex());
        }

        rt.anchorMin = rt.anchorMax = new(0.5f, 1f);
        rt.pivot = new(0.5f, 1f);
        rt.anchoredPosition = new(0f, -100f);
        go.GetComponent<Image>().color = new(0.1f, 0.06f, 0.05f, 0.88f);
        var row = go.GetComponent<HorizontalLayoutGroup>();
        row.padding = new(18, 18, 10, 10);
        row.spacing = 18f;
        row.childAlignment = TextAnchor.MiddleCenter;
        row.childControlWidth = row.childControlHeight = true;
        row.childForceExpandWidth = row.childForceExpandHeight = false;
        var fitter = go.GetComponent<ContentSizeFitter>();
        fitter.horizontalFit = fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        Label(rt, "emp_permadeath_banner".lang(), font, new(1f, 0.92f, 0.82f));

        var leave = new GameObject("Leave", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button),
            typeof(HorizontalLayoutGroup));
        var leaveRt = (RectTransform)leave.transform;
        leaveRt.SetParent(rt, false);
        var back = leave.GetComponent<Image>();
        back.color = new(0.5f, 0.24f, 0.16f, 1f);
        var inner = leave.GetComponent<HorizontalLayoutGroup>();
        inner.padding = new(16, 16, 6, 6);
        inner.childAlignment = TextAnchor.MiddleCenter;
        inner.childControlWidth = inner.childControlHeight = true;
        inner.childForceExpandWidth = inner.childForceExpandHeight = false;
        Label(leaveRt, "emp_permadeath_leave".lang(), font, Color.white);
        var button = leave.GetComponent<Button>();
        button.targetGraphic = back;
        // (the same as the Escape menu's "Title": the game asks first and saves)
        button.onClick.AddListener(() => {
            SE.ClickGeneral();
            EClass.game?.GotoTitle();
        });

        return go;
    }

    private static void Label(Transform parent, string text, Font? font, Color color)
    {
        var go = new GameObject("Text", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
        go.transform.SetParent(parent, false);
        var label = go.GetComponent<Text>();
        if (font != null) {
            label.font = font;
        }

        label.fontSize = 18;
        label.color = color;
        label.alignment = TextAnchor.MiddleLeft;
        label.horizontalOverflow = HorizontalWrapMode.Overflow;
        label.raycastTarget = false;
        label.text = text;
    }
}

/// <summary>
///     Permadeath with PermadeathRevive (council 2026-10-27): the game deleted the save of whoever died, before its
///     last words, and offered only "game over". The host's death deleted the shared world; a guest's its copy. Here
///     the player dies as in the game (the message, the sounds, its last words once), then its character waits in the
///     world for a revive at a bartender; nothing is deleted. Without the box, or outside permadeath: the game's own
/// </summary>
[HarmonyPatch]
internal static class PermadeathPatch
{
    [HarmonyPrefix]
    [HarmonyPatch(typeof(Scene), nameof(Scene.OnUpdate))]
    internal static void OnSceneUpdate()
    {
        if (!EClass.core.IsGameStarted || EClass.pc is not { isDead: true } pc || EClass.player.deathDialog ||
            !PlayerRevive.Active) {
            return;
        }

        // the game's block that follows only runs while deathDialog is false
        EClass.player.deathDialog = true;
        EClass.player.returnInfo = null;
        EClass.player.ignoreFallOver = 1;

        if (pc.GetBool(PlayerRevive.DeadFlag)) {
            Msg.Say("emp_permadeath_wait".lang());
            return;
        }

        pc.SetBool(PlayerRevive.DeadFlag, true);
        Msg.Say("diebye");
        EClass.Sound.Play("dead_pc2");
        EClass.Sound.Play("dead_pc");
        var lastWord = Lang.GetList("lastWords").RandomItem();
        EClass.ui.CloseLayers();
        EInput.haltInput = false;
        Dialog.InputName("dialogLastword", lastWord, (cancel, text) => {
            if (!cancel) {
                lastWord = text;
            }

            Msg.SetColor(Msg.colors.Talk);
            Msg.Say(lastWord.Bracket(1));
            Msg.Say("emp_permadeath_wait".lang());
        });
    }

    /// <summary>
    ///     No open window or menu when this frame began (the game's input not yet read): an Escape that closes a
    ///     window does not also open the system menu
    /// </summary>
    private static bool _uiWasIdle;

    // the system menu was open as the frame began: Escape closes it (the action mode that would is not run)
    private static bool _menuWasOpen;

    /// <summary>
    ///     A dead player waiting for its revive: the game reads only the windows' input for a dead character
    ///     (Scene.OnUpdate calls UI.UpdateInput, not ActionMode.UpdateInput), because its death dialog came at once.
    ///     Here the wait has no end of its own, and Escape did nothing: no system menu, no Save and Quit, no
    ///     Multiplayer menu (in "mod"), no bug report (GitHub issue 45). The base game mode's own non-world keys
    ///     (AM_BaseGameMode.OnUpdateInput: Escape, help, log) and the read-only character sheet and journal are
    ///     handled here; movement, actions, hotbar and inventory keys stay ignored
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(Scene), nameof(Scene.OnUpdate))]
    [HarmonyPriority(Priority.First)]
    internal static void BeforeSceneUpdate()
    {
        _uiWasIdle = EClass.core.IsGameStarted && EClass.ui is { } ui && ui.layers.Count == 0 && !ui.contextMenu.isActive;
        _menuWasOpen = EClass.core.IsGameStarted && EClass.ui is { } open && open.layers.Count == 0 && open.contextMenu.isActive;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Scene), nameof(Scene.OnUpdate))]
    internal static void AfterSceneUpdate(Scene __instance)
    {
        DeadBanner.Update(IsWaitingForRevive(__instance));

        if (_menuWasOpen && EInput.action == EAction.Cancel && EClass.ui.contextMenu.isActive && IsWaitingForRevive(__instance)) {
            EClass.ui.contextMenu.currentMenu?.Hide();
            EInput.Consume();
            return;
        }

        if (!_uiWasIdle || !IsWaitingForRevive(__instance) || EClass.ui.layers.Count != 0 || EClass.ui.contextMenu.isActive ||
            EClass.ui.BlockInput || EInput.waitReleaseAnyKey) {
            return;
        }

        switch (EInput.action) {
            case EAction.Cancel:
                if (!WidgetSearch.Instance) {
                    HotItemContext.Show("system", EInput.uiMousePosition);
                    EInput.Consume();
                }

                break;
            case EAction.Help:
                LayerHelp.Toggle("general", "1");
                break;
            case EAction.Log:
                WidgetMainText.ToggleLog();
                break;
            case EAction.MenuChara:
                EClass.ui.ToggleLayer<LayerChara>()?.SetChara(EClass.pc);
                break;
            case EAction.MenuJournal:
                EClass.ui.ToggleLayer<LayerJournal>();
                break;
        }
    }

    internal static bool IsWaitingForRevive(Scene scene)
    {
        return EClass.core.IsGameStarted && scene.mode == Scene.Mode.Zone && EClass.pc is { isDead: true } pc &&
               EClass.player.deathDialog && pc.GetBool(PlayerRevive.DeadFlag) && PlayerRevive.Active;
    }

    /// <summary>
    ///     No game in a session deletes a save: a guest's is its copy of the world; the host's is the world, while the
    ///     box keeps its dead players waiting
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(GameIO), nameof(GameIO.DeleteGame))]
    internal static bool OnDeleteGame(string id)
    {
        if (NetSession.Instance.Connection is ElinNetClient) {
            EmpLog.Information("Save {Id} not deleted: a guest's copy of the world", id);
            return false;
        }

        if (PlayerRevive.Active && id == Game.id) {
            EmpLog.Information("Save {Id} not deleted: permadeath with revives at the bartender", id);
            return false;
        }

        return true;
    }
}
