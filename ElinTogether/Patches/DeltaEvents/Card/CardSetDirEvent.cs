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
        return OverrideMethodComparer.FindAllOverrides(typeof(Card), nameof(Card.SetDir), typeof(int));
    }

    [HarmonyPrefix]
    internal static bool OnSetDir(Card __instance, int d)
    {
        if (NetSession.Instance.Connection is not { } connection || ElinDelta.IsApplying) {
            return true;
        }

        connection.Delta.AddRemote(new CardSetDirDelta {
            Card = __instance,
            Dir = d,
        });

        return true;
    }
}