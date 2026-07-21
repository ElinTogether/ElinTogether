using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

[HarmonyPatch(typeof(Core), nameof(Core.Update))]
internal class CoreSynchronizationContext : SynchronizationContext
{
    [HarmonyPrefix]
    internal static void OnCoreUpdate()
    {
        // apply remote delta happened in previous updates before this update
        switch (NetSession.Instance.Connection) {
            case ElinNetHost host:
                host.WorldStateDeltaProcess();
                return;
            case ElinNetClient client:
                GameDelta = 0f;
                client.WorldStateDeltaProcess();
                return;
        }
    }

    [HarmonyPostfix]
    internal static void OnCoreUpdateEnd()
    {
        if (NetSession.Instance.Connection is null || !core.IsGameStarted) {
            return;
        }

        CardCache.Update();
        NetProfileSynchronizationContext.Update();
        QuestSynchronizationContext.Update();
        switch (NetSession.Instance.Connection) {
            case ElinNetHost host:
                if (!EMono.scene.paused) {
                    host.Delta.AddRemote(new GameDelta {
                        Delta = Core.gameDelta,
                    });
                }

                if (CanSendDelta) {
                    CanSendDelta = false;
                    host.WorldStateDeltaUpdate();
                }

                return;
            case ElinNetClient client when CanSendDelta:
                CanSendDelta = false;
                client.WorldStateDeltaUpdate();
                return;
        }
    }
}