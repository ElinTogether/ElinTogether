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
    public bool HasPendingIn => _inBuffer.Count > 0 || _inBufferDeferred.Count > 0;
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
        foreach (var delta in batch) {
            try {
                if (delta is null) {
                    continue;
                }

                if (gameStarted || !delta.RequiresGameStarted) {
                    delta.Apply(net);
                }
            } catch (Exception ex) {
                var deltaType = delta.GetType().Name;
                var desyncInfo = ex.ToString();
                if (IsCapturing) {
                    GetLatestSnapshot()?.Desyncs.Add(new(BatchCount, desyncInfo, deltaType));
                }
                net.ReportDesync(desyncInfo);
                EmpLog.Debug(ex, "Exception at processing delta {DeltaType}\n{@Delta}",
                    deltaType, delta);
                // noexcept
            }
        }

        BatchCount++;
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
        return [
            ..batch
                .Select((delta, index) => new { delta, index })
                .GroupBy(x => x.delta.GetType())
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
        _outBuffer.AddRange(_outBufferUnrefreshed.Where(delta => delta.Refresh()));
        _outBufferUnrefreshed.Clear();
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
        EmpLog.Debug("[Delta] Snapshot capture stopped ({Count} snapshots recorded)",
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