using System;
using System.Linq;
using ElinTogether.Elements;
using ElinTogether.Helper;
using ElinTogether.Net;
using HarmonyLib;
using UnityEngine;

namespace ElinTogether.Patches;

[HarmonyPatch(typeof(Game), nameof(Game.OnUpdate))]
internal class GameSynchronizationContext : SynchronizationContext
{
    private const float MaxGameDeltaBuffer = 0.3f;

    /// <summary>
    ///     The host stops sending game time while paused: its last factor is not to outlive it
    /// </summary>
    private const float HostTurboSeconds = 0.5f;

    private static float _hostTurbo;

    /// <summary>
    ///     The factor this frame of the host's world runs at. Read here and not at the end of the frame: the
    ///     game ends a fast-forward its idle player did not ask for, after the frame's game time is counted
    /// </summary>
    internal static float WorldTurbo { get; private set; }

    /// <summary>
    ///     This frame of a player on its own clock was raised to the host's fast-forward
    /// </summary>
    internal static bool FollowsHost { get; private set; }

    private static float _lastDelta;
    private static float _hostTurboTime = -1f;

    internal static void OnHostTurbo(float turbo)
    {
        _hostTurbo = turbo;
        _hostTurboTime = Time.unscaledTime;
    }

    [HarmonyPrefix]
    internal static void OnGameOnUpdate()
    {
        switch (NetSession.Instance.Connection) {
            // its own turns on its own clock, as the host's: fed by the network, the host's game time comes in
            // lumps (none, one or two a frame, in bursts over the Internet) and every step waited for it, while
            // the sprite and the camera glide on the local clock
            case ElinNetClient when NetSession.Instance.Rules.OwnClock:
                GameDelta = 0f;
                // one pace for everyone on a map, as when the world ran on the host's clock: the host
                // fast-forwards, by itself or for another player, and this game follows
                // The game time at hand was counted at the end of the last frame, with this game's own factor
                // at that instant (see Core.Update): raised to the host's, never multiplied on top of its own
                FollowsHost = !NetSession.Instance.IsAway && _hostTurbo > 1f && Core.gameDelta > 0f &&
                              Time.unscaledTime - _hostTurboTime < HostTurboSeconds &&
                              _lastDelta * _hostTurbo > Core.gameDelta * 1.05f;
                if (FollowsHost) {
                    Core.gameDelta = _lastDelta * _hostTurbo;
                }

                _lastDelta = Core.delta;

                break;
            // apply game delta as clients
            case ElinNetClient:
                var buffered = Mathf.Min(GameDelta, MaxGameDeltaBuffer);
                Core.gameDelta = buffered;
                GameDelta = EMono.scene.paused ? buffered : 0f;
                break;
            // allow remote players to trigger turbo
            case ElinNetHost host when !EMono.scene.paused:
                if (ShouldRemoteTurbo(host)) {
                    ActionMode.Adv.SetTurbo();
                }

                WorldTurbo = EMono.scene.actionMode is AM_Adv ? AM_Adv.turbo : 0f;
                break;
            default:
                RefSpeed = pc.Speed;
                return;
        }

        var players = NetSession.Instance.CurrentPlayers;
        var minSpeed = int.MaxValue;
        var maxSpeed = int.MinValue;
        var allZero = true;
        var adminSpeed = 0;
        for (var i = 0; i < players.Count; i++) {
            // (the owner's admin character sets nobody's pace, see Helper.OwnerAdmin; alone, its own)
            if (players[i].OwnerAdmin) {
                adminSpeed = players[i].Speed;
                continue;
            }

            var speed = players[i].Speed;
            if (speed != 0) {
                allZero = false;
            }

            if (speed > 0 && speed < minSpeed) {
                minSpeed = speed;
            }

            if (speed > maxSpeed) {
                maxSpeed = speed;
            }
        }

        if (allZero) {
            RefSpeed = adminSpeed > 0 ? adminSpeed : pc.Speed;
            return;
        }

        // a host alone gets no speed report, so no shared speed (0): measured against 0, every step took a tenth
        // of its time (user, 2026-10-24: "the player is propelled")
        if (NetSession.Instance.Rules.UseSharedSpeed && NetSession.Instance.SharedSpeed > 0) {
            RefSpeed = NetSession.Instance.SharedSpeed;
        } else {
            // (Min over nothing positive threw before: the same here)
            if (minSpeed == int.MaxValue) {
                throw new InvalidOperationException("Sequence contains no elements");
            }

            var min = (float)minSpeed;
            var max = (float)maxSpeed;
            var mult = Math.Sqrt(max / min);

            mult = Math.Min(mult, 8f);

            RefSpeed = (int)(max / mult);
        }
    }

    private static bool ShouldRemoteTurbo(ElinNetHost host)
    {
        if (ActionModeCombat.Activated) {
            return false;
        }

        // a walk (or any act the host only knows as "busy") runs on its player's own clock: no need to speed
        // the host's world up for it, its own character included
        var ownClock = NetSession.Instance.Rules.OwnClock;

        // on its own clock a player's fast-forward is its own: the world follows it, as it follows the host's
        if (ownClock) {
            foreach (var state in NetSession.Instance.CurrentPlayers) {
                if (!state.Turbo) {
                    continue;
                }

                foreach (var c in host.ActiveRemoteCharas.Values) {
                    if (c.uid == state.CharaUid && c.IsInActiveMap) {
                        return true;
                    }
                }
            }
        }

        foreach (var chara in host.ActiveRemoteCharas.Values) {
            if (chara.ai is GoalRemote { child: { status: AIAct.Status.Running } child } &&
                !(ownClock && child is NoGoal) &&
                (child.UseTurbo || child.Current is { UseTurbo: true })) {
                return true;
            }
        }

        return false;
    }
}