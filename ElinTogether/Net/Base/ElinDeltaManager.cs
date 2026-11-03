using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ElinTogether.Models;

namespace ElinTogether.Net;

public class ElinDeltaManager
{
    private const float Smoothing = 0.5f;
    private const int MaxSnapshots = 200;
    private const int MaxDeferCount = 7200;

    /// <summary>
    ///     Coming in
    /// </summary>
    private readonly List<ElinDelta> _inBuffer = [];

    /// <summary>
    ///     Local deferred
    /// </summary>
    private readonly List<ElinDelta> _inBufferDeferred = [];

    /// <summary>
    ///     Sending out
    /// </summary>
    private readonly List<ElinDelta> _outBuffer = [];

    /// <summary>
    ///     Remote deferred
    /// </summary>
    private readonly List<ElinDelta> _outBufferDeferred = [];

    private readonly List<ElinDelta> _outBufferUnrefreshed = [];

    private readonly List<BatchSnapshot> _snapshots = [];

    public bool HasPendingOut => _outBuffer.Count > 0 || _outBufferDeferred.Count > 0;
    // what is held counts: it is replayed even when nothing else comes in
    public bool HasPendingIn =>
        _inBuffer.Count > 0 || _inBufferDeferred.Count > 0 || _held.Count > 0 || _beforeHold.Count > 0;
    public bool IsIdle => !HasPendingOut && !HasPendingIn;

    public int BatchCount { get; private set; }
    public float AverageOut { get; private set; }
    public float AverageIn { get; private set; }

    public IReadOnlyList<BatchSnapshot> Snapshots => _snapshots;
    public bool IsCapturing { get; private set; }

    /// <summary>
    ///     Sending out
    /// </summary>
    public void AddRemote(ElinDelta delta)
    {
        // (the host: a story message goes to the players of that story only, see TeamOwnStory)
        if (Helper.TeamOwnStory.Divert(this, delta)) {
            return;
        }

        // (the host: a change inside a team's bank or delivery box goes to that team's players only, see TeamBoxes)
        if (Helper.TeamBoxes.Divert(this, delta)) {
            return;
        }

        _outBufferUnrefreshed.Add(delta);
    }

    /// <summary>
    ///     Sending out, insert into buffer
    /// </summary>
    public void AddRemoteImmediate(ElinDelta delta)
    {
        _outBuffer.Add(delta);
    }

    /// <summary>
    ///     Sending out, next flush
    /// </summary>
    public void DeferRemote(ElinDelta delta)
    {
        _outBufferDeferred.Add(delta);
    }

    /// <summary>
    ///     Coming in to process
    /// </summary>
    public void AddLocal(ElinDelta delta)
    {
        _inBuffer.Add(delta);
    }

    /// <summary>
    ///     Local defer, next flush
    /// </summary>
    public void DeferLocal(ElinDelta delta)
    {
        if (++delta.DeferCount > MaxDeferCount) {
            EmpLog.Warning("Dropping delta {DeltaType} after {DeferCount} failed defer\n{@Delta}",
                delta.GetType().Name, delta.DeferCount, delta);
            return;
        }

        _inBufferDeferred.Add(delta);
    }

    public void ProcessLocalBatch(ElinNetBase net)
    {
        var batch = FlushInBuffer();
#if DEBUG
        var clientFiltered = batch
            .Where(d => d is not (DynamicDelta or GameDelta or CharaTickDelta or CharaTickConditionDelta))
            .ToList();
        if (clientFiltered.Count > 0) {
            CaptureBatch(clientFiltered);
        }
#else
        CaptureBatch(batch);
#endif

        var gameStarted = EClass.core.IsGameStarted;
        var hold = _holding && (!gameStarted || _untilPlaced);
        if (hold && gameStarted && UnityEngine.Time.unscaledTime > _holdDeadline) {
            // the host never said where we stand on that map: this game goes on with what it has
            EmpLog.Warning("No placement on the incoming map after {Seconds:F1}s, giving up the hold",
                UnityEngine.Time.unscaledTime - _holdStart);
            hold = false;
        }

        // what was dropped over the limit while loading is missing from this map for good: the map is asked again, once
        // what was kept is applied (reliability audit 2026-10-27, #7)
        var lost = 0;
        if (_holding && !hold) {
            lost = _heldLost;
            if (_held.Count > 0 || _heldLost > 0) {
                EmpLog.Information(
                    "Replaying {Held} held deltas ({Acts} besides game time) after {Seconds:F1}s of loading, " +
                    "hold started by {HoldStart}, {Lost} lost over the limit: {Types}",
                    _held.Count, _held.Count(d => d is not GameDelta), UnityEngine.Time.unscaledTime - _holdStart,
                    _holdKind, _heldLost,
                    string.Join(", ", _held
                        .Where(d => d is not GameDelta)
                        .GroupBy(d => d.GetType().Name)
                        .OrderByDescending(g => g.Count())
                        .Take(6)
                        .Select(g => $"{g.Key} {g.Count()}")));
            }

            // what the others did while the world or the map was loading here, in order, before anything newer
            batch.InsertRange(0, _held);
            _held.Clear();
            _heldLost = 0;
            _holding = false;
            _untilPlaced = false;
        }

        // came in before the map copy that opened the hold and is in that copy: applied first, never kept
        var unheld = _beforeHold.Count;
        batch.InsertRange(0, _beforeHold);
        _beforeHold.Clear();

        var index = 0;
        foreach (var delta in batch) {
            var keep = hold && index++ >= unheld;
            try {
                if (delta is null) {
                    continue;
                }

                // a zone the host just made is never kept back: its map may be the very next thing to arrive, and
                // a map whose zone is unknown here cannot be loaded (it only needs the world, not a running game)
                var zoneNews = delta is SpatialGenDelta && hold && EClass.game is not null;

                if (keep && delta.RequiresGameStarted && !zoneNews) {
                    // happened after the copy being loaded right now was taken: applied once it is there
                    if (_held.Count < MaxHeld) {
                        _held.Add(delta);
                    } else {
                        _heldLost++;
                    }
                } else if (gameStarted || !delta.RequiresGameStarted || zoneNews) {
                    delta.Apply(net);
                }
            } catch (Exception ex) {
                var deltaType = delta.GetType().Name;
                var desyncInfo = ex.ToString();
                if (IsCapturing) {
                    GetLatestSnapshot()?.Desyncs.Add(new(BatchCount, desyncInfo, deltaType));
                }
                net.ReportDesync(desyncInfo);
                CountFailure(net, deltaType, ex, delta);
                // noexcept
            }
        }

        if (lost > 0) {
            EmpLog.Warning("{Lost} deltas received while loading were over the limit of {MaxHeld} and are lost here, asking for the map again",
                lost, MaxHeld);
            if (net is ElinNetClient) {
                NetDesync.OweRepair("deltas kept while loading (over the limit)", lost);
            }
        }

        BatchCount++;
    }

    // a kind of delta failing that often within the window leaves the map different here: a client reloads it
    private const int FailureStrikes = 3;
    private const float FailureWindow = 60f;

    // delta type -> failures in the current window and when that window opened, for this session
    private readonly Dictionary<string, (int Count, float Since)> _failures = [];

    /// <summary>
    ///     The first failure of a kind is a warning with its exception, the next ones stay in the debug journal.
    ///     On a client, <see cref="FailureStrikes" /> failures of the same kind within <see cref="FailureWindow" />
    ///     ask for the map again, under the rule and cooldowns of a checksum repair (NetDesync)
    /// </summary>
    private void CountFailure(ElinNetBase net, string deltaType, Exception ex, ElinDelta delta)
    {
        var now = UnityEngine.Time.unscaledTime;
        var first = !_failures.TryGetValue(deltaType, out var seen);
        var count = !first && now - seen.Since <= FailureWindow ? seen.Count + 1 : 1;
        _failures[deltaType] = (count, count == 1 ? now : seen.Since);

        if (first) {
            EmpLog.Warning(ex, "Exception at processing delta {DeltaType}, further ones in the debug journal\n{@Delta}",
                deltaType, delta);
        } else {
            EmpLog.Information(ex, "Exception at processing delta {DeltaType}\n{@Delta}",
                deltaType, delta);
        }

        if (net is ElinNetClient && count >= FailureStrikes && NetDesync.RepairFailingDelta(deltaType, count)) {
            _failures[deltaType] = (0, now);
        }
    }

    public List<ElinDelta> FlushOutBuffer()
    {
        if (_outBufferUnrefreshed.Count > 0) {
            EmpLog.Warning("Unrefreshed buffer is not emptied");
        }

        var batch = _outBuffer.ToList();
        _outBuffer.Clear();
        _outBuffer.AddRange(_outBufferDeferred);
        _outBufferDeferred.Clear();

        return ApplyOverride(batch);
    }

    public List<ElinDelta> FlushInBuffer()
    {
        var batch = _inBuffer.ToList();
        _inBuffer.Clear();
        _inBuffer.AddRange(_inBufferDeferred);
        _inBufferDeferred.Clear();

        return batch;
    }

    public List<ElinDelta> ApplyOverride(List<ElinDelta> batch)
    {
        // nearly every flush has only deltas that stack: the grouping below allocated at each one for nothing
        // (efficiency audit 2026-10-27)
        var overrides = false;
        for (var i = 0; i < batch.Count && !overrides; i++) {
            overrides = batch[i] is { Order: not ElinDelta.OverrideOrder.Stack };
        }

        if (!overrides) {
            return batch;
        }

        return [
            ..batch
                .Select((delta, index) => new { delta, index })
                // one per character for a delta about one: a batch with two characters keeps both
                .GroupBy(x => (x.delta.GetType(), x.delta.OverrideKey))
                .SelectMany(g => {
                    return g.First().delta.Order switch {
                        ElinDelta.OverrideOrder.Stack => g,
                        ElinDelta.OverrideOrder.First => g.Take(1),
                        ElinDelta.OverrideOrder.Last => g.TakeLast(1),
                        _ => throw new ArgumentOutOfRangeException(),
                    };
                })
                .OrderBy(x => x.index)
                .Select(x => x.delta),
        ];
    }

    public void RefreshBuffer()
    {
        if (_outBufferUnrefreshed.Count > 0) {
            for (var i = 0; i < _outBufferUnrefreshed.Count; i++) {
                if (_outBufferUnrefreshed[i].Refresh()) {
                    _outBuffer.Add(_outBufferUnrefreshed[i]);
                }
            }

            _outBufferUnrefreshed.Clear();
        }

        CardGenDelta.ClearRecordedUids();
        QuestCreateDelta.ClearRecordedUids();
    }

    public void ClearOut()
    {
        _outBuffer.Clear();
        _outBufferDeferred.Clear();
        _outBufferUnrefreshed.Clear();
    }

    public void ClearIn()
    {
        _inBuffer.Clear();
        _inBufferDeferred.Clear();
        _beforeHold.Clear();
        _held.Clear();
        _heldLost = 0;
        _holding = false;
        _untilPlaced = false;
    }

    // (not a constant: the bench lowers it, bench/held_lost_check.py)
    internal static int MaxHeld = 20_000;
    // as long as the client waits for its placement, see ElinNetClient.ActivationWait
    private const float MaxHoldSeconds = 10f;

    private readonly List<ElinDelta> _held = [];
    // what was waiting when a map arrived with the game running: it came before that copy was taken
    private readonly List<ElinDelta> _beforeHold = [];
    private bool _holding;
    // the game was running when the map arrived: only the placement on that map tells it is loaded
    private bool _untilPlaced;
    private int _heldLost;
    private float _holdStart;
    private float _holdDeadline;
    private string _holdKind = "";

    /// <summary>
    ///     A copy of a map just arrived and is about to be loaded, or (<paramref name="world" />) a copy of the
    ///     whole world, whose map follows. What came before is in that copy; what comes from now on happened after
    ///     it was taken and is kept until the map is loaded, instead of being thrown away with the game not started
    ///     or applied to the map this game is about to leave (an item dropped by someone meanwhile would never
    ///     show here, what a player picked up or put on meanwhile would never reach its character here)
    /// </summary>
    public void HoldForIncomingMap(bool world = false)
    {
        var now = UnityEngine.Time.unscaledTime;
        _holdDeadline = now + MaxHoldSeconds;

        var running = EClass.core.IsGameStarted;
        // a map on top of what is already kept (the world it comes with, or an earlier map): the characters of
        // the players are in no map, what is kept about them and what they carry is still to be applied
        if (!world && _holding) {
            // placed on the earlier map but what is kept was not replayed yet: kept until the placement on this one
            _untilPlaced |= running;
            return;
        }

        // a world replaces the game that runs; a map alone leaves it running until the placement
        running &= !world;
        if (running) {
            // already waiting here: it came before this copy was taken and is in it. Applied to the map being
            // left, as it would be without a hold, and not once more after the placement
            _beforeHold.AddRange(_inBuffer);
            _beforeHold.AddRange(_inBufferDeferred);
        } else {
            _beforeHold.Clear();
        }

        _inBuffer.Clear();
        _inBufferDeferred.Clear();

        _held.Clear();
        _heldLost = 0;
        _holding = true;
        _untilPlaced = running;
        _holdStart = now;
        _holdKind = world ? "world copy" : running ? "map copy, game running" : "map copy";
    }

    /// <summary>
    ///     The map that arrived while the game was running is loaded and we stand on it
    /// </summary>
    public void MapPlaced()
    {
        _untilPlaced = false;
    }

    public void UpdateAverages()
    {
        AverageOut = AverageOut * (1f - Smoothing) + _outBuffer.Count * Smoothing;
        AverageIn = AverageIn * (1f - Smoothing) + _inBuffer.Count * Smoothing;
    }

    public (int outBuffer, int outDeferred, int inBuffer, int inDeferred) GetCounts()
    {
        return (_outBuffer.Count, _outBufferDeferred.Count, _inBuffer.Count, _inBufferDeferred.Count);
    }

    public override string ToString()
    {
        UpdateAverages();
        return $"Delta [Out={AverageOut:F1}, In={AverageIn:F1}]";
    }

    public void StartCapture()
    {
        IsCapturing = true;
        EmpLog.Debug("[Delta] Snapshot capture started");
    }

    public void StopCapture()
    {
        IsCapturing = false;
        EmpLog.Debug("[Delta] Snapshot capture stopped ({SnapshotCount} snapshots recorded)",
            _snapshots.Count);
    }

    public void ClearSnapshots()
    {
        _snapshots.Clear();
        EmpLog.Debug("[Delta] Snapshots cleared");
    }

    public string GetSnapshotSummary(int count = 10)
    {
        if (_snapshots.Count == 0) {
            return "No snapshots captured.\n";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"-- Snapshots ({_snapshots.Count}) --");

        foreach (var snap in _snapshots.TakeLast(count)) {
            var typeList = string.Join(", ", snap.DeltaTypes);
            sb.AppendLine($"#{snap.Batch,-6} {snap.DeltaTypes.Count,3} {snap.Timestamp:HH:mm:ss.fff} [{typeList}]");
        }

        return sb.ToString();
    }

    public BatchSnapshot? GetLatestSnapshot()
    {
        return _snapshots is [.., var last] ? last : null;
    }

    private void CaptureBatch(List<ElinDelta> batch)
    {
        if (!IsCapturing) {
            return;
        }

        if (_snapshots.Count >= MaxSnapshots) {
            _snapshots.RemoveAt(0);
        }

        _snapshots.Add(new(
            BatchCount,
            DateTime.Now,
            [..batch.Select(d => d.GetType().Name.Replace("Delta", ""))],
            _outBuffer.Count,
            _inBuffer.Count,
            []
        ));
    }

    public record BatchSnapshot(
        int Batch,
        DateTime Timestamp,
        List<string> DeltaTypes,
        int OutBufferCount,
        int InBufferCount,
        List<DesyncSnapshot> Desyncs
    );

    public record DesyncSnapshot(
        int Batch,
        string Info,
        string DeltaType
    );
}