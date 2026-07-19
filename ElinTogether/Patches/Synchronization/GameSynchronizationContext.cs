using System;
using System.Linq;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

[HarmonyPatch(typeof(Game), nameof(Game.OnUpdate))]
internal class GameSynchronizationContext : SynchronizationContext
{
    [HarmonyPrefix]
    internal static void OnGameOnUpdate()
    {
        switch (NetSession.Instance.Connection) {
            // apply game delta as clients
            case ElinNetClient:
                Core.gameDelta = GameDelta;
                break;
            // allow remote players to trigger turbo
            case ElinNetHost when !EMono.scene.paused:
                ActionMode.Adv.SetTurbo();
                break;
            default:
                RefSpeed = pc.Speed;
                return;
        }

        if (NetSession.Instance.CurrentPlayers.All(n => n.Speed == 0)) {
            RefSpeed = pc.Speed;
            return;
        }

        if (NetSession.Instance.Rules.UseSharedSpeed) {
            RefSpeed = NetSession.Instance.SharedSpeed;
        } else {
            var min = (float)NetSession.Instance.CurrentPlayers.Where(n => n.Speed > 0).Min(n => n.Speed);
            var max = (float)NetSession.Instance.CurrentPlayers.Max(n => n.Speed);
            var mult = Math.Sqrt(max / min);

            mult = Math.Min(mult, 8f);

            RefSpeed = (int)(max / mult);
        }
    }
}