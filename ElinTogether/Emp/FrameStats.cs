#if DEBUG
using System;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.Profiling;
using Object = UnityEngine.Object;

namespace ElinTogether.Emp;

/// <summary>
///     Debug build only: frame times, managed allocations and collections over a run, for the bench's perf baseline
///     (dev/_tools/perf_baseline.py). <c>ElinTogether.Emp.FrameStats.Start()</c>, play, then <c>Report()</c> (JSON)
///     and <c>Stop()</c>. While it runs the frame cap of a muted bench window (30 fps, EmpDebugListener) is lifted, so
///     the frame time is the game's work and not the cap; Stop puts the cap back
/// </summary>
public static class FrameStats
{
    // 2^16 frames: over 18 minutes at 60 fps, the oldest are overwritten past that
    private const int Capacity = 1 << 16;

    private static Runner? _runner;

    /// <param name="fps">the frame cap during the run: -1 none (default), else that many frames a second</param>
    public static string Start(int fps = -1)
    {
        Stop();
        var go = new GameObject("EmpFrameStats");
        Object.DontDestroyOnLoad(go);
        _runner = go.AddComponent<Runner>();
        _runner.Fps = fps;
        return $"frame stats started (cap {(fps < 0 ? "none" : fps.ToString())}, allocations from {_runner.Source})";
    }

    public static string Stop()
    {
        if (_runner == null) {
            _runner = null;
            return "not running";
        }

        Object.Destroy(_runner.gameObject);
        _runner = null;
        // the muted window's cap again (EmpDebugListener sets it only when vsync comes back)
        Application.targetFrameRate = 30;
        return "stopped";
    }

    /// <summary>
    ///     JSON: frames, seconds, p50/p95/p99/max frame ms, mean KB allocated per frame, GCs (gen 0) per minute
    /// </summary>
    public static string Report()
    {
        return _runner == null ? "{\"running\":false}" : _runner.Report();
    }

    private sealed class Runner : MonoBehaviour
    {
        private readonly float[] _ms = new float[Capacity];
        private readonly float[] _sorted = new float[Capacity];
        private int _count;
        private long _frames;
        private long _allocated;
        private long _lastHeap;
        private int _gc0;
        private float _started;
        private bool _first = true;

        internal int Fps = -1;

        // Unity's Mono with Boehm may not count bytes per thread: then the heap's positive steps (a collection in the
        // frame hides that frame's allocations, so the figure is a floor)
        internal string Source { get; } = AllocatedByThread() > 0 ? "thread" : "heap";

        private void Awake()
        {
            _gc0 = GC.CollectionCount(0);
            _started = Time.realtimeSinceStartup;
            _lastHeap = Read();
        }

        private void Update()
        {
            if (Application.targetFrameRate != Fps) {
                Application.targetFrameRate = Fps;
            }

            var now = Read();
            if (_first) {
                // the frame of Start is not one of the run
                _first = false;
                _lastHeap = now;
                return;
            }

            _ms[_count++ & (Capacity - 1)] = Time.unscaledDeltaTime * 1000f;
            _frames++;
            if (now > _lastHeap) {
                _allocated += now - _lastHeap;
            }

            _lastHeap = now;
        }

        private long Read()
        {
            return Source == "thread" ? AllocatedByThread() : Profiler.GetMonoUsedSizeLong();
        }

        private static long AllocatedByThread()
        {
            try {
                return GC.GetAllocatedBytesForCurrentThread();
            } catch (Exception) {
                return 0;
            }
        }

        internal string Report()
        {
            var n = (int)Math.Min(_frames, Capacity);
            Array.Copy(_ms, _sorted, n);
            Array.Sort(_sorted, 0, n);
            var seconds = Time.realtimeSinceStartup - _started;
            var gcs = GC.CollectionCount(0) - _gc0;
            var json = new StringBuilder(256).Append('{');
            Field(json, "running", "true");
            Field(json, "frames", _frames.ToString(CultureInfo.InvariantCulture));
            Field(json, "seconds", Num(seconds));
            Field(json, "fps", Num(seconds > 0 ? _frames / seconds : 0));
            Field(json, "p50_ms", Num(Pick(n, 0.50)));
            Field(json, "p95_ms", Num(Pick(n, 0.95)));
            Field(json, "p99_ms", Num(Pick(n, 0.99)));
            Field(json, "max_ms", Num(n > 0 ? _sorted[n - 1] : 0));
            Field(json, "kb_per_frame", Num(_frames > 0 ? _allocated / 1024.0 / _frames : 0));
            Field(json, "allocated_mb", Num(_allocated / 1048576.0));
            Field(json, "gc0", gcs.ToString(CultureInfo.InvariantCulture));
            Field(json, "gc_per_min", Num(seconds > 0 ? gcs * 60.0 / seconds : 0));
            Field(json, "heap_mb", Num(Profiler.GetMonoUsedSizeLong() / 1048576.0));
            Field(json, "alloc_source", "\"" + Source + "\"");
            Field(json, "cap", Fps.ToString(CultureInfo.InvariantCulture));
            json.Length--;
            return json.Append('}').ToString();
        }

        private double Pick(int n, double q)
        {
            return n == 0 ? 0 : _sorted[Math.Min(n - 1, (int)Math.Ceiling(q * n) - 1)];
        }

        private static string Num(double v)
        {
            return v.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static void Field(StringBuilder json, string name, string value)
        {
            json.Append('"').Append(name).Append("\":").Append(value).Append(',');
        }
    }
}
#endif
