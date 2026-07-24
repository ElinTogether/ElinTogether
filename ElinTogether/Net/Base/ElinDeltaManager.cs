using System;
using System.Collections.Generic;
using System.Linq;
using ElinTogether.Models;

namespace ElinTogether.Net;

public class ElinDeltaManager
{
    private const float Smoothing = 0.5f;

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

    // smoothed stat
    private float _averageIn;
    private float _averageOut;

    public bool HasPendingOut => _outBuffer.Count > 0 || _outBufferDeferred.Count > 0;
    public bool HasPendingIn => _inBuffer.Count > 0 || _inBufferDeferred.Count > 0;
    public bool IsIdle => !HasPendingOut && !HasPendingIn;

    public int BatchCount { get; private set; }
    public bool EnableDebugging { get; set; }

    /// <summary>
    ///     Sending out
    /// </summary>
    public void AddRemote(ElinDelta delta)
    {
        _outBufferUnrefreshed.Add(delta);
    }

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
            .Where(d => d is not (DynamicDelta or GameDelta))
            .ToList();
        if (clientFiltered.Count > 0) {
            _ = 0xb;
        }
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
                EmpLog.Debug(ex, "Exception at processing delta {DeltaType}\n{@Delta}",
                    delta.GetType().Name, delta);
                // noexcept
            }
        }
        if (EnableDebugging) {
            EmpLog.Debug("[Delta] ProcessLocalBatch #{Batch}: applied {TotalDeltaCount} deltas\n{DeltaList}",
                BatchCount, batch.Count, string.Join('\n', batch.Select(c => c.GetType().Name)));
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
        _averageOut = _averageOut * (1f - Smoothing) + _outBuffer.Count * Smoothing;
        _averageIn = _averageIn * (1f - Smoothing) + _inBuffer.Count * Smoothing;
    }

    public (int outBuffer, int outDeferred, int inBuffer, int inDeferred) GetCounts()
    {
        return (_outBuffer.Count, _outBufferDeferred.Count, _inBuffer.Count, _inBufferDeferred.Count);
    }

    public override string ToString()
    {
        UpdateAverages();
        return $"Delta Out={_averageOut:F1}\tDelta In={_averageIn:F1}";
    }
}