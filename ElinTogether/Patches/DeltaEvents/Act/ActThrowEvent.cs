using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

[HarmonyPatch(typeof(ActThrow), nameof(ActThrow.Throw), typeof(Card), typeof(Point), typeof(Card), typeof(Thing),
    typeof(ThrowMethod))]
internal class ActThrowEvent
{
    [HarmonyPrefix]
    internal static bool OnClientThrow(Card c, Point p, Card target, Thing t, ThrowMethod method, out ActThrowDelta? __state)
    {
        __state = null;

        if (NetSession.Instance.Connection is not { } connection || ElinDelta.IsApplying) {
            return true;
        }

        // perform throw on host via ActThrowDelta
        if (connection.IsHost || c.IsPC) {
            __state = new() {
                Owner = c,
                Point = p,
                Target = target,
                Thing = t,
                Method = method,
            };
        }

        return connection.IsHost;
    }

    [HarmonyPostfix]
    internal static void OnClientThrowEnd(ActThrowDelta? __state)
    {
        if (__state is not null) {
            NetSession.Instance.Connection!.Delta.AddRemote(__state);
        }
    }
}