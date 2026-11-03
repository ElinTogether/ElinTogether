using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ElinTogether.Net;

public class TickScheduler
{
    private readonly Queue<TickSubscription> _deferredAdd = [];
    private readonly Queue<Action> _deferredRemove = [];
    private readonly List<TickSubscription> _subscriptions = [];

    public bool IsTicking { get; private set; }

    public void Tick()
    {
        IsTicking = true;
        var now = Time.time;

        for (var i = 0; i < _subscriptions.Count; i++) {
            var sub = _subscriptions[i];
            if (now < sub.NextInvokeTime) {
                continue;
            }

            // never behind now: after a freeze (a join, a save) the job ran on every frame until it caught up, a burst
            // of snapshots to everyone (efficiency audit 2026-10-27)
            sub.NextInvokeTime += sub.TickInterval;
            if (sub.NextInvokeTime <= now) {
                sub.NextInvokeTime = now + sub.TickInterval;
            }

            var start = System.Diagnostics.Stopwatch.GetTimestamp();
            try {
                sub.Update();
            } catch (Exception ex) {
                // the first failure of a job is a warning: a job that fails at every tick froze players with nothing
                // in the log but at Debug (reliability audit 2026-10-27)
                if (sub.Failures++ == 0) {
                    EmpLog.Warning(ex, "Exception at tick update {TickHandler}", sub.Update.Method.Name);
                } else {
                    EmpLog.Information(ex, "Exception at tick update {TickHandler} ({Failures} so far)", sub.Update.Method.Name, sub.Failures);
                }
            }

            sub.Metric.Add(0, start);
        }

        IsTicking = false;

        // must remove first, due to failsafe logic in other parts
        while (_deferredRemove.TryDequeue(out var handler)) {
            _subscriptions.RemoveAll(s => s.Update == handler);
        }

        while (_deferredAdd.TryDequeue(out var handler)) {
            _subscriptions.Add(handler);
        }
    }

    public void Subscribe(Action onUpdate, float hz)
    {
        if (hz <= 0f) {
            throw new ArgumentOutOfRangeException(nameof(hz));
        }

        var interval = 1f / hz;
        _deferredAdd.Enqueue(new(onUpdate, interval) {
            NextInvokeTime = Time.time + interval,
            Metric = NetMetrics.Named("job", $"{onUpdate.Method.DeclaringType?.Name}.{onUpdate.Method.Name}"),
        });
    }

    public void Unsubscribe(Action onUpdate)
    {
        _deferredRemove.Enqueue(onUpdate);
    }

    private sealed record TickSubscription(Action Update, float TickInterval)
    {
        public float NextInvokeTime;
        public NetMetric Metric = null!;
        public int Failures;
    }
}