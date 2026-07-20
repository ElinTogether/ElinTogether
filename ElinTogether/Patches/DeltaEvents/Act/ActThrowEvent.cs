using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

[HarmonyPatch(typeof(ActThrow), nameof(ActThrow.Throw), typeof(Card), typeof(Point), typeof(Card), typeof(Thing),
    typeof(ThrowMethod))]
internal class ActThrowEvent
{
    [HarmonyPrefix]
    internal static bool OnClientThrow(Card c, Point p, Card target, Thing t, ThrowMethod method)
    {
        if (NetSession.Instance.Connection is not { } connection || ElinDelta.IsApplying) {
            return true;
        }

        // perform throw on host via ActThrowDelta
        if (connection.IsHost || c.IsPC) {
            // split
            var thing = t;
            if (t.uid < 0 && CardCache.Find(-t.uid) is Thing split) {
                thing = split;
            }

            connection.Delta.DeferRemote(new ActThrowDelta {
                Owner = c,
                Point = p,
                Target = target,
                Thing = thing,
                Method = method,
                SplitNum = t.Num,
            });
        }

        return connection.IsHost;
    }
}