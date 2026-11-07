using System;
using System.Linq;
using UnityEngine;

namespace ElinTogether.Net;

/// <summary>
///     The keeper's own player cap of a peer-hosted world (council 2026-11-07): the signed Server.MaxPlayers is a
///     ceiling, and a keeper whose PC slows down (frames, or its upload backing up) stops taking new players until it
///     recovers. Nobody already in is ever put out: only a new join is told the server is full
/// </summary>
internal partial class ElinNetHost
{
    private const float LoadStepSeconds = 10f;
    private const int LoadFloor = 25;
    private const int LoadRise = 5;
    private const float LowFps = 10f;
    private const float GoodFps = 18f;
    private const float BackedUpSeconds = 30f;
    private const float ClearSeconds = 60f;

    // the last three 10 s steps (30 s of frames)
    private static readonly float[] _stepFps = [-1f, -1f, -1f];
    private static int _stepAt;
    private static float _stepStart = -1f;
    private static int _stepFrames;
    private static float _backedSince = -1f;
    private static float _clearSince = -1f;

    /// <summary>
    ///     The keeper's own cap, below the signed one; null while its PC keeps up
    /// </summary>
    internal static int? LoadCap { get; private set; }

    // (0 is no limit: the keeper's own cap still holds)
    internal static int EffectiveCap(int signed) => LoadCap is { } own && (signed <= 0 || own < signed) ? own : signed;

    /// <summary>
    ///     PeerHostedTick (every 2 s of the server): frames per second over 30 s and the peers' send queues
    /// </summary>
    private void UpdateLoadCap()
    {
        var now = Time.realtimeSinceStartup;
        // (a server resting at 5 frames a second with nobody in is not slow)
        if (EmpServer.Resting || !PeerIds.Any()) {
            ResetLoad(now);
            return;
        }

        var backed = Socket.Peers.Any(p => p is Steam.SteamNetPeer { BacklogBytes: > 0 });
        _backedSince = backed ? _backedSince < 0f ? now : _backedSince : -1f;

        if (_stepStart < 0f) {
            _stepStart = now;
            _stepFrames = Time.frameCount;
            return;
        }

        if (now - _stepStart < LoadStepSeconds) {
            return;
        }

        _stepFps[_stepAt] = (Time.frameCount - _stepFrames) / (now - _stepStart);
        _stepAt = (_stepAt + 1) % _stepFps.Length;
        _stepStart = now;
        _stepFrames = Time.frameCount;
        if (_stepFps.Any(f => f < 0f)) {
            return;
        }

        var fps = _stepFps.Average();
        var signed = EmpConfig.Server.MaxPlayers.Value;
        var players = Socket.Peers.Count;
        var slow = fps < LowFps || (_backedSince >= 0f && now - _backedSince >= BackedUpSeconds);
        if (slow) {
            _clearSince = -1f;
            var own = Math.Max(LoadFloor, players);
            if (signed > 0 && own >= signed) {
                return;
            }

            if (LoadCap != own) {
                LoadCap = own;
                EmpLog.Information("Peer-hosted world: this PC slows down ({Fps:F1} fps, send queue backed up {Backed}), new players wait: cap {Cap}",
                    fps, _backedSince >= 0f, own);
            }

            return;
        }

        if (LoadCap is not { } cap) {
            return;
        }

        if (fps <= GoodFps || backed) {
            _clearSince = -1f;
            return;
        }

        if (_clearSince < 0f) {
            _clearSince = now;
        }

        if (now - _clearSince < ClearSeconds) {
            return;
        }

        _clearSince = now;
        var next = cap + LoadRise;
        LoadCap = signed > 0 && next >= signed ? null : next;
        EmpLog.Information("Peer-hosted world: this PC keeps up again ({Fps:F1} fps), cap {Cap}", fps, LoadCap?.ToString() ?? "the signed one");
    }

    private static void ResetLoad(float now)
    {
        for (var i = 0; i < _stepFps.Length; i++) {
            _stepFps[i] = -1f;
        }

        _stepStart = -1f;
        _backedSince = -1f;
        _clearSince = now;
    }
}
