using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

[HarmonyPatch(typeof(Card), nameof(Card.feat), MethodType.Setter)]
internal static class CharaFeatPointEvent
{
    [HarmonyPrefix]
    internal static void OnSetFeat(Card __instance, out int __state)
    {
        __state = __instance.feat;
    }

    [HarmonyPostfix]
    internal static void OnSetFeatEnd(Card __instance, int __state)
    {
        // client only
        if (NetSession.Instance.Connection is not ElinNetClient client || ElinDelta.IsRemoteStateLanding) {
            return;
        }

        if (__instance is not Chara { IsPC: true } chara || chara.feat == __state) {
            return;
        }

        client.Delta.AddRemote(new CharaFeatPointDelta {
            Owner = chara,
            Feat = chara.feat,
        });
    }
}