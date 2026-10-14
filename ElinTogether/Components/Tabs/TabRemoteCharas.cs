using System.Linq;
using ElinTogether.Helper;
using ElinTogether.LangMod;
using ElinTogether.Net;
using HeathenEngineering.SteamworksIntegration;
using YKF;

namespace ElinTogether.Components;

internal class TabRemoteCharas : TabEmpBase
{
    public override void OnLayout()
    {
        var info = this.MakeCard();
        info.TextFlavor("emp_ui_charas_title");
        info.Text("emp_ui_charas_desc".lang());

        var saved = ElinNetHost.SavedRemoteCharas;
        if (saved.Count == 0) {
            info.Text("emp_ui_charas_empty".lang());
            return;
        }

        var list = CharaGrid();
        foreach (var (steamId, charaUid) in saved.ToArray()) {
            BuildCharaCard(list, steamId, charaUid);
        }
    }

    private void BuildCharaCard(YKLayout parent, ulong steamId, int charaUid)
    {
        var playerName = UserData.Get(steamId).Name;
        if (playerName.IsEmpty()) {
            playerName = steamId.ToString();
        }

        var chara = EClass.game.cards.globalCharas.Find(charaUid);
        var invalid = chara is not { IsSavedRemoteChara: true } ? "emp_ui_chara_missing".Loc(charaUid)
            : ElinNetHost.SavedRemoteCharas.Count(kv => kv.Value == charaUid) > 1 ? "emp_ui_chara_shared".Loc(charaUid)
            : null;
        if (invalid is not null) {
            var stale = parent.MakeCard();
            stale.TextFlavor(playerName);
            stale.TextMedium(invalid);
            AddDeleteButton(stale, steamId, "emp_ui_chara_unlink_confirm".Loc(invalid, playerName));
            return;
        }

        var online = chara!.IsActiveRemoteChara;
        var status = online
            ? "emp_ui_chara_online".lang().TagColor(FontColor.Good)
            : "emp_ui_chara_offline".lang();

        var infoGroup = CharaCard(parent, chara);
        infoGroup.TextFlavor(playerName);
        infoGroup.TextMedium(chara.Name);
        infoGroup.Text("emp_ui_chara_status".Loc(chara.LV, status));

        var detail = "emp_ui_chara_delete_confirm".Loc(chara.Name, playerName);
        if (online) {
            detail += "\n\n" + "emp_ui_chara_delete_online".lang();
        }
        AddDeleteButton(infoGroup, steamId, detail);
    }

    private static void AddDeleteButton(YKLayout parent, ulong steamId, string detail)
    {
        var btnRow = parent.Horizontal();
        btnRow.Layout.childForceExpandWidth = true;

        var button = btnRow.Button("emp_ui_chara_delete".lang().TagColor(FontColor.Bad), () => {
            var dialog = Dialog.Choice(detail, d => {
                d.AddButton("cancel".lang());
                d.AddButton("emp_ui_chara_delete".lang(), () => {
                    ElinNetHost.DeleteRemoteChara(steamId);
                    LayerElinTogether.Instance?.Reopen();
                });
            });

            ((UIButton)dialog.list.buttons[1].component).keyText.text = "";
        });
        button.mainText.supportRichText = true;
    }
}