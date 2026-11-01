using System;
using System.Linq;
using ElinTogether.Helper;
using ElinTogether.LangMod;
using ElinTogether.Models;
using ElinTogether.Net;
using UnityEngine;
using UnityEngine.UI;
using YKF;

namespace ElinTogether.Components;

/// <summary>
///     "My settings" (hub step 7): this player's own options in cards, each a title with a grey line under it, then
///     the Friends and the Ignored lists (moved here from the Server page: they are the player's own, not the host's)
/// </summary>
internal class TabClientConfiguration : TabEmpBase
{
    public override void OnLayout()
    {
        if (Waits()) {
            return;
        }

        // three pages that each fit the window (one long page ran far under it): controls then mods, the
        // depot, then the friends and the ignored
        SubTabs("client", [
            (Both("emp_ui_cl_sec_controls", "emp_ui_cl_sec_mods"), page => {
                var (left, right) = page.Stacked();
                BuildControls(left);
                BuildMods(right);
            }),
            ("emp_ui_cl_sec_depot".lang(), BuildDepot),
            (Both("emp_info_friends", "emp_info_ignored"), page => {
                var (left, right) = page.Stacked();
                BuildFriends(left);
                BuildIgnored(right);
            }),
        ]);
    }

    private static string Both(string a, string b) => a.lang() + " & " + b.lang();

    private static void BuildControls(YKVertical page)
    {
        var card = page.Card("emp_ui_cl_sec_controls".lang());
        var pingKey = new EInput.KeyMap {
            action = EAction.None,
            key = KeyCode.P,
            required = true,
        };
        card.ButtonSetting("emp_ui_ping_action".lang(), "emp_ui_cl_desc_ping".lang(),
            EmpConfig.Client.PingKeybind.Value.ToString(), () => {
                var l = global::Layer.Create<Dialog>("DialogKeymap");
                l.textDetail.SetText("dialog_keymap".lang("emp_ui_ping_action".lang()));
                l.keymap = pingKey;
                l.SetOnKill(() => {
                    EmpConfig.Client.PingKeybind.Value = pingKey.key;
                    LayerElinTogether.Instance?.Reopen();
                });
                EMono.ui.AddLayer(l);
            }, "emp_tip_cfg_ping_key".lang());
    }

    // the mods of the game joined, fetched without subscribing (ModFetch): this player's own choice
    private static void BuildMods(YKVertical page)
    {
        var card = page.Card("emp_ui_cl_sec_mods".lang());
        var fetch = EmpConfig.Client.FetchMods;
        var fetchBox = card.ToggleRow("emp_ui_cl_fetch_mods".lang(), "emp_ui_cl_desc_fetch_mods".lang(), fetch.Value,
            v => fetch.Value = v);
        fetchBox.SetTooltipLang("emp_tip_cfg_fetch_mods".lang());

        // ... and kept: subscribed to, so that the next join needs no restart
        // (keeping them means nothing without fetching them: greyed out until then)
        var keep = EmpConfig.Client.KeepMods;
        var keepBox = card.ToggleRow("emp_ui_cl_keep_mods".lang(), "emp_ui_cl_desc_keep_mods".lang(), keep.Value,
            v => keep.Value = v);
        keepBox.SetTooltipLang("emp_tip_cfg_keep_mods".lang());
        keepBox.interactable = fetch.Value;

        // security: mods with code (a DLL) are fetched only with this, off (the code runs on this PC)
        var code = EmpConfig.Client.FetchCodeMods;
        var codeBox = card.ToggleRow("emp_ui_cl_code_mods".lang(), "emp_ui_cl_desc_code_mods".lang(), code.Value,
            v => code.Value = v);
        codeBox.SetTooltipLang("emp_tip_cfg_code_mods".lang());
        codeBox.interactable = fetch.Value;
    }

    // the shared save folder (the depot): optional, not needed to play together
    private static void BuildDepot(YKVertical page)
    {
        var card = page.Card("emp_ui_cl_sec_depot".lang());
        card.TextSmall("emp_ui_cl_desc_depot".lang(), FontColor.Gray);

        var depot = EmpConfig.Client.DepotPath;
        card.ButtonSetting("emp_ui_depot_folder".lang(), depot.Value.Length > 0 ? depot.Value : "-",
            "emp_hub_change".lang(), () => {
                Dialog.InputName("emp_ui_depot_folder_ask", depot.Value, (cancel, text) => {
                    if (!cancel) {
                        depot.Value = text.Trim();
                        LayerElinTogether.Instance?.Reopen();
                    }
                }).input.field.characterLimit = 0;
            }, "emp_tip_cfg_depot_path".lang());

        card.Divider();
        var password = EmpConfig.Client.DepotPassword;
        card.ButtonSetting("emp_ui_depot_password".lang(), password.Value.Length > 0 ? "***" : "-",
            "emp_hub_change".lang(), () => {
                // a password or an access key is never shown again: the box opens empty. Left empty, a GitHub key is
                // kept (a depot of Elin Together Server may have no password: there, empty removes it)
                Dialog.InputName("emp_ui_depot_password_ask", "", (cancel, text) => {
                    if (!cancel && !(SaveDepot.GitHub && text.Trim().Length == 0)) {
                        password.Value = text.Trim();
                        LayerElinTogether.Instance?.Reopen();
                    }
                }).input.field.characterLimit = 0;
            }, "emp_tip_cfg_depot_password".lang());

        // how a private GitHub repository becomes the depot, where it is set
        // (one text per line: a text with line breaks was measured one line high and drew over the rows above it)
        foreach (var line in "emp_ui_depot_gh_help".lang().Split('\n')) {
            card.TextSmall(line, FontColor.Gray);
        }
    }

    private static void BuildFriends(YKVertical page)
    {
        var card = page.Card("emp_info_friends".lang());
        var online = NetSession.Instance.CurrentPlayers.Where(p => !p.User.IsMe && PlayerSocial.IsFriend(p.User)).ToList();
        // the others are known by the name they had when they were added
        var offline = PlayerSocial.FriendList.Where(f => online.All(p => (ulong)p.User != f.Key)).ToList();
        var list = card.ListArea(online.Count + offline.Count, 7, 44f);
        foreach (var player in online) {
            var user = (ulong)player.User;
            var name = player.User.Name;
            PersonRow(list, name, "emp_hub_online".Loc(player.FindChara()?.Name ?? "?"), "emp_ui_unfriend", () => PlayerSocial.SetFriend(user, name, false));
        }

        foreach (var (user, name) in offline) {
            PersonRow(list, name.IsEmpty(user.ToString()), "emp_hub_offline".lang(), "emp_ui_unfriend", () => PlayerSocial.SetFriend(user, name, false));
        }

        if (PlayerSocial.FriendList.Count == 0) {
            card.TextSmall("emp_info_friends_none".lang(), FontColor.Gray);
        }
    }

    private static void BuildIgnored(YKVertical page)
    {
        var card = page.Card("emp_info_ignored".lang());
        card.TextSmall("emp_info_ignored_note".lang(), FontColor.Gray);
        var ignored = PlayerSocial.IgnoredList.ToList();
        var list = card.ListArea(ignored.Count, 7, 44f);
        foreach (var (user, name) in ignored) {
            PersonRow(list, name.IsEmpty(user.ToString()), null, "emp_ui_unignore", () => PlayerSocial.SetIgnored(user, name, false));
        }
    }

    /// <summary>
    ///     A person of a list: the name and a grey line, then the game's small trash button (its word as the hover
    ///     text) instead of a wide button on every row
    /// </summary>
    private static void PersonRow(YKLayout list, string name, string? detail, string removeLang, Action remove)
    {
        var row = list.Row();
        row.TextColumn(name, detail);
        row.IconButton(EmpUi.GameIcons?.trash, "x", () => {
            remove();
            LayerElinTogether.Instance?.Reopen();
        }, removeLang);
    }
}
