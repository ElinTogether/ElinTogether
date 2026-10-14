using ElinTogether.Common;
using ElinTogether.Helper;
using ElinTogether.LangMod;
using ElinTogether.Net;
using UnityEngine;
using UnityEngine.UI;
using YKF;

namespace ElinTogether.Components;

internal class TabSessionInfo : TabEmpBase
{
    public override void OnLayout()
    {
        BuildOverviewSection();
        BuildPlayerList();
    }

    private void BuildOverviewSection()
    {
        var session = NetSession.Instance;
        var overview = this.MakeCard();

        overview.TextFlavor("emp_ui_session_overview");

        var list = overview.Grid()
            .WithConstraintCount(2);
        list.Fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
        list.Layout.cellSize = FitCell(2);

        list.Topic("emp_ui_session_role", (session.IsHost ? "emp_ui_session_host" : "emp_ui_session_client").lang());
        list.Topic("emp_ui_session_player_count", session.CurrentPlayers.Count.ToString());
        list.Topic("emp_ui_sv_cfg_shared_speed", (session.Rules.UseSharedSpeed ? "on" : "off").lang());
        list.Topic("emp_ui_sv_cfg_turn_combat", (session.Rules.UseTurnBasedCombat ? "on" : "off").lang());
    }

    private void BuildPlayerList()
    {
        Header("emp_ui_connected_players");

        var list = CharaGrid();
        foreach (var player in NetSession.Instance.CurrentPlayers) {
            BuildPlayerCard(list, player);
        }
    }

    private void BuildPlayerCard(YKLayout parent, NetPeerState player)
    {
        if (player.FindChara() is not { } chara) {
            parent.Text("emp_ui_invalid_chara".Loc(player.Index, player.CharaUid));
            return;
        }

        var infoGroup = CharaCard(parent, chara);
        infoGroup.TextFlavor(player.User.Name.TagColor(PeerColorizer.GetColor(player.Index)));
        infoGroup.TextMedium(chara.Name);
        infoGroup.Text(BuildPingStat(player));

        // action buttons: only show for non-host players
        if (NetSession.Instance.IsHost ^ player.User.IsMe) {
            var btnRow = infoGroup.Horizontal();
            btnRow.Layout.childForceExpandWidth = true;
            btnRow.Layout.childAlignment = TextAnchor.MiddleCenter;

            btnRow.Button("emp_ui_reconnect".lang(), () => ReconnectPlayer(player.Index));

            if (NetSession.Instance.IsHost) {
                btnRow.Button("emp_ui_kick".lang(), () => KickPlayer(player.Index));
            }
        }
    }

    private static void ReconnectPlayer(int peerIndex)
    {
        switch (NetSession.Instance.Connection) {
            case ElinNetHost host:
                host.RequestClientReconnect(peerIndex);
                break;
            case ElinNetClient client:
                client.ReconnectSelf();
                break;
        }
    }

    private static void KickPlayer(int peerIndex)
    {
        NetSession.Instance.Connection?.DisconnectPeer(peerIndex, EmpDisconnectInfo.HostKick);
        LayerElinTogether.Instance?.Reopen();
    }

    private static string BuildPingStat(NetPeerState player)
    {
        if (player.Index == 0) {
            return "emp_ui_ping_host".lang();
        }

        var ping = player.AvgPingMs > 0 ? player.AvgPingMs : player.LastPingMs;
        var quality = player.ConnectionQualityLocal > 0
            ? "emp_ui_quality_format".Loc(player.ConnectionQualityLocal.ToString("P0"))
            : "";

        return "emp_ui_ping_format".Loc(ping.ToString("F0"), quality);
    }
}