using ElinTogether.Helper;
using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

[HarmonyPatch(typeof(Core), nameof(Core.Update))]
[AlwaysPatched]
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
                // GameDelta = 0f; // moved to GameSyncCtx
                client.WorldStateDeltaProcess();
                return;
        }
    }

    [HarmonyPostfix]
    internal static void OnCoreUpdateEnd()
    {
        PlayerTrade.WatchSession();
        SaveDepot.Update();
        // (HiddenServer.Update runs from EmpMod.Update: it waits for its server with no session open, when a release
        // build has none of these patches)
        PeerKeeper.Update();
        CrossCheck.Update();
        EmpServer.Update();
        EmpAutoHost.Update();
        // (alone on a map of its own the host link is still there: the host still hears it)
        PlayerIdle.Update();
        // (alone too: the host's own team's knowledge is put in place after a load, given back to the world when the
        // box is unticked)
        TeamKnowledge.Update();
        // (alone too: the world team's story is the save's, the host's own team's is put in place after a load)
        TeamOwnStory.Update();
        // (alone too: the host's own player opens its team's boxes, the world's stay in the game's slots)
        TeamBoxes.Update();
        PlayerDuel.Always();

        if (NetSession.Instance.Connection is not { } connection || !core.IsGameStarted) {
            // (alone on a map of its own there is no connection, and the clock of the world is still lived there:
            // bench 2026-10-28, the away guest's date stood still in living world)
            if (NetSession.Instance.IsAway) {
                LivingWorld.Update();
            }

            return;
        }

        CardAddThingEvent.FlushPendingAbilityFakeCard();
        ThingRequest.InvalidateDangling();
        StoryGifts.Flush();
        ElinNetHost.GiveStoryShares();
        QuestJournalDelta.Tell();
        ClientCellSweep.Update();
        WorldHandover.Watch();
        TileStateDelta.Flush();
        PlayerTrade.Update();
        Mailbox.Update();
        PlayerDuel.Update();
        AdventurerBout.Update();
        CardCache.Update();
        CharaSynchronizationContext.Update();
        NetProfileSynchronizationContext.Update();
        RemoteTacticsPatch.Update();
        AutoCombatPerPlayerPatch.Update();
        PlayerTitleDelta.Update();
        PersonalQuests.TellHolder();
        NameWatch.Update();
        AreaWatch.Update();
        QuestSynchronizationContext.Update();
        SleepSynchronizationContext.Update();
        LivingWorld.Update();
        connection.Delta.RefreshBuffer();
        switch (NetSession.Instance.Connection) {
            case ElinNetHost host:
                if (!EMono.scene.paused && ShouldSendGameTime()) {
                    host.Delta.AddRemoteImmediate(new GameDelta {
                        Delta = Core.gameDelta,
                        Turbo = GameSynchronizationContext.WorldTurbo,
                    });
                }

                if (CanSendDelta) {
                    CanSendDelta = false;
                    var start = System.Diagnostics.Stopwatch.GetTimestamp();
                    host.WorldStateDeltaUpdate();
                    NetMetrics.DeltaSend.Add(0, start);
                }

                return;
            case ElinNetClient client when CanSendDelta:
                CanSendDelta = false;
                var sending = System.Diagnostics.Stopwatch.GetTimestamp();
                client.WorldStateDeltaUpdate();
                NetMetrics.DeltaSend.Add(0, sending);
                return;
        }
    }

    // on their own clock the players' games only read the host's fast-forward from it (GameSynchronizationContext,
    // kept 0.5 s): the game time of every frame went to everyone for nothing, and made every delta flush a message
    // to every peer even with nothing else to say (efficiency audit 2026-10-27). Told at 5 Hz while the world
    // fast-forwards, and once when it stops
    private const float TurboTellSeconds = 0.2f;
    private static float _turboToldAt;
    private static bool _turboTold;

    private static bool ShouldSendGameTime()
    {
        if (!NetSession.Instance.Rules.OwnClock) {
            return true;
        }

        if (GameSynchronizationContext.WorldTurbo <= 1f) {
            if (!_turboTold) {
                return false;
            }

            _turboTold = false;
            return true;
        }

        var now = UnityEngine.Time.unscaledTime;
        if (_turboTold && now - _turboToldAt < TurboTellSeconds) {
            return false;
        }

        _turboTold = true;
        _turboToldAt = now;
        return true;
    }
}