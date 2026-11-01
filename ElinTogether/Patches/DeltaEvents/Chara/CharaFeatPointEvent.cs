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
        if (__instance is not Chara chara || chara.feat == __state) {
            return;
        }

        // a client tells its own character; the host the companions it simulates for a player (see CharaLevelEvent)
        switch (NetSession.Instance.Connection) {
            case ElinNetClient when chara.IsPC:
            case ElinNetHost when CharaLevelEvent.OwnedCompanion(chara):
                NetSession.Instance.Connection.Delta.AddRemote(new CharaFeatPointDelta {
                    Owner = chara,
                    Feat = chara.feat,
                });
                break;
        }
    }
}