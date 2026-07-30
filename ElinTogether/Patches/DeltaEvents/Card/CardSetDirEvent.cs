using System.Collections.Generic;
using System.Reflection;
using ElinTogether.Helper;
using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

[HarmonyPatch]
internal class CardSetDirEvent
{
    internal static IEnumerable<MethodBase> TargetMethods()
    {
        return [
            ..OverrideMethodComparer.FindAllOverrides(typeof(Card), nameof(Card.SetDir), typeof(int)),
            AccessTools.Method(typeof(Chara), nameof(Chara.LookAt), [typeof(Point)]),
        ];
    }

    [HarmonyPostfix]
    internal static void OnSetDir(Card __instance)
    {
        if (NetSession.Instance.Connection is not { } connection || ElinDelta.IsApplying) {
            return;
        }

        connection.Delta.AddRemote(new CardSetDirDelta {
            Card = __instance,
            Dir = __instance.dir,
        });
    }
}