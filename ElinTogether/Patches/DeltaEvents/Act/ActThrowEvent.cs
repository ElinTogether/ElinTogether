using ElinTogether.Helper;
using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

[HarmonyPatch(typeof(ActThrow), nameof(ActThrow.Throw), typeof(Card), typeof(Point), typeof(Card), typeof(Thing),
    typeof(ThrowMethod))]
internal class ActThrowEvent
{
    [HarmonyPrefix]
    internal static bool OnClientThrow(ActThrow __instance, Card c, Point p, Card target, Thing t, ThrowMethod method)
    {
        if (NetSession.Instance.Connection is not { } connection || ElinDelta.IsApplying) {
            return true;
        }

        if (connection.IsHost || c.IsPC) {
            // perform throw on host via ActThrowDelta
            connection.Delta.DeferRemote(new ActThrowDelta {
                Owner = c,
                Point = p,
                Target = target,
                Thing = t.SplitContext,
                Method = method,
                SplitNum = t.SplitCount,
            });
        }

        return connection.IsHost;
    }
}