using System.Linq;
using ElinTogether.Helper;
using ElinTogether.Net;
using HarmonyLib;
using UnityEngine;

namespace ElinTogether.Patches;

[HarmonyPatch]
internal class RemoteMinimapPatch
{
    [HarmonyPrefix]
    [HarmonyPatch(typeof(WidgetMinimap), nameof(WidgetMinimap.EmitParticle))]
    internal static void OnColorizeSelfMarker(Card c, ref Color col)
    {
        if (!NetSession.Instance.HasActiveConnection || NetSession.Instance.Self is not { } self) {
            return;
        }

        if (c is Chara { IsPC: true }) {
            col = PeerColorizer.GetColor(self.Index);
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(WidgetMinimap), nameof(WidgetMinimap.RefreshMarkers))]
    internal static void OnEmitRemotePlayerMarkers(WidgetMinimap __instance)
    {
        if (!NetSession.Instance.HasActiveConnection || !__instance.gameObject.activeInHierarchy) {
            return;
        }

        foreach (var player in NetSession.Instance.CurrentPlayers.Where(player => player.CharaUid != EClass.pc.uid)) {
            if (player.FindChara() is not { IsInActiveMap: true } chara) {
                continue;
            }

            __instance.EmitParticle(chara, __instance.psAlly, PeerColorizer.GetColor(player.Index));
        }
    }
}