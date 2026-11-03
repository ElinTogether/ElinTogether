using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using ElinTogether.Net.Steam;
using Steamworks;
using UnityEngine;

namespace ElinTogether.Net;

/// <summary>
///     One counter: messages (or calls), bytes and main-thread time, for the period and since the start
/// </summary>
internal sealed class NetMetric
{
    internal long Count;
    internal long Bytes;
    internal long Ticks;
    internal long TotalCount;
    internal long TotalBytes;
    internal long TotalTicks;

    /// <summary>
    ///     One more, with the time since <paramref name="start" /> (a Stopwatch timestamp, 0 for none)
    /// </summary>
    internal void Add(long bytes, long start = 0)
    {
        lock (NetMetrics.Gate) {
            Count++;
            Bytes += bytes;
            if (start != 0) {
                Ticks += Stopwatch.GetTimestamp() - start;
            }
        }
    }

    // under Gate
    internal void Roll()
    {
        TotalCount += Count;
        TotalBytes += Bytes;
        TotalTicks += Ticks;
        Count = Bytes = Ticks = 0;
    }
}

/// <summary>
///     Council 2026-10-27 step 1: what the network and the main thread cost, by message type, peer, scheduler job,
///     save and map dispatch. Every 10 s one CSV line per counter that moved goes to
///     ElinMP/Logs/net_metrics_&lt;date&gt;.csv: timestamp,role,category,key,count,bytes,ms, the totals of those 10 s.
///     Gauges (link, backlog, players) are the value at that instant. `emp.metrics` gives the totals so far
/// </summary>
internal static class NetMetrics
{
    private const float FlushSeconds = 10f;

    // ReSharper disable once ChangeFieldTypeToSystemThreadingLock
    internal static readonly object Gate = new();

    private static readonly Dictionary<uint, NetMetric> _sentByType = new(256);
    private static readonly Dictionary<uint, NetMetric> _receivedByType = new(256);
    private static readonly Dictionary<int, NetMetric> _sentByPeer = new(64);
    private static readonly Dictionary<int, NetMetric> _receivedByPeer = new(64);
    private static readonly Dictionary<(string, string), NetMetric> _named = new(64);

    // the sockets that ticked during the period, for the link gauges
    private static readonly SteamNetManager?[] _sockets = new SteamNetManager?[4];
    private static readonly StringBuilder _line = new(4096);

    internal static readonly NetMetric BroadcastCalls = Named("broadcast", "calls");
    internal static readonly NetMetric BroadcastTargets = Named("broadcast", "targets");
    internal static readonly NetMetric GameSave = Named("save", "game");
    internal static readonly NetMetric MapSave = Named("save", "map");
    internal static readonly NetMetric WorldPack = Named("dispatch", "world_pack");
    internal static readonly NetMetric MapPack = Named("dispatch", "map_pack");
    internal static readonly NetMetric DeltaSend = Named("main", "delta_send");

    private static float _nextFlush = FlushSeconds;

    private static string Folder => Path.Combine(Application.persistentDataPath, "ElinMP/Logs");

    /// <summary>
    ///     A counter of its own, made once: callers keep it
    /// </summary>
    internal static NetMetric Named(string category, string key)
    {
        lock (Gate) {
            if (!_named.TryGetValue((category, key), out var metric)) {
                _named[(category, key)] = metric = new();
            }

            return metric;
        }
    }

    /// <summary>
    ///     A whole message handed to a peer (before it is cut in pieces), its type is in its first 4 bytes
    /// </summary>
    internal static void Sent(int peer, byte[] bytes)
    {
        var type = bytes.Length >= 4 ? BitConverter.ToUInt32(bytes, 0) : 0u;
        lock (Gate) {
            Of(_sentByType, type).Add1(bytes.Length, 0);
            Of(_sentByPeer, peer).Add1(bytes.Length, 0);
        }
    }

    /// <summary>
    ///     A whole message read and handled, with the time since <paramref name="start" />
    /// </summary>
    internal static void Received(int peer, uint type, int size, long start)
    {
        var ticks = Stopwatch.GetTimestamp() - start;
        lock (Gate) {
            Of(_receivedByType, type).Add1(size, ticks);
            Of(_receivedByPeer, peer).Add1(size, 0);
        }
    }

#if DEBUG
    // Debug build only (bench, perf_baseline.py): what each kind of delta weighs inside the WorldStateDeltaList the
    // host sends, rows "sent_delta,<Type>". Counted always; bytes only with DeltaBytes on (each delta serialized once
    // more to measure it, which costs main-thread time and allocations of its own)
    private static readonly Dictionary<Type, NetMetric> _sentByDelta = new(128);

    public static bool DeltaBytes { get; set; }

    /// <summary>
    ///     One list of deltas handed to the players (once per list, whatever the number of players)
    /// </summary>
    internal static void SentDeltas(List<Models.ElinDelta> deltas)
    {
        var bytes = DeltaBytes;
        lock (Gate) {
            foreach (var delta in deltas) {
                if (delta is null) {
                    continue;
                }

                long size = 0;
                if (bytes) {
                    try {
                        size = SteamNetSerializer.SerializePart(delta).Length;
                    } catch (Exception) {
                        size = 0;
                    }
                }

                Of(_sentByDelta, delta.GetType()).Add1(size, 0);
            }
        }
    }
#endif

    internal static void Broadcast(int size, int targets)
    {
        lock (Gate) {
            BroadcastCalls.Add1(size, 0);
            // fan-out: one per target, with the bytes each of them costs
            BroadcastTargets.Count += targets;
            BroadcastTargets.Bytes += (long)size * targets;
        }
    }

    // under Gate
    private static void Add1(this NetMetric metric, long bytes, long ticks)
    {
        metric.Count++;
        metric.Bytes += bytes;
        metric.Ticks += ticks;
    }

    // under Gate
    private static NetMetric Of<TKey>(Dictionary<TKey, NetMetric> map, TKey key)
    {
        if (!map.TryGetValue(key, out var metric)) {
            map[key] = metric = new();
        }

        return metric;
    }

    /// <summary>
    ///     Called every frame by each session component, writes the period every 10 s
    /// </summary>
    internal static void Tick(SteamNetManager socket)
    {
        for (var i = 0; i < _sockets.Length; i++) {
            if (_sockets[i] == socket) {
                break;
            }

            if (_sockets[i] is null) {
                _sockets[i] = socket;
                break;
            }
        }

        if (Time.unscaledTime < _nextFlush) {
            return;
        }

        _nextFlush = Time.unscaledTime + FlushSeconds;
        Flush();
    }

    private static void Flush()
    {
        var session = NetSession.Instance;
        var role = session.Transport is null ? "none" : session.IsAway ? "away" : session.IsHost ? "host" : "client";
        var stamp = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        var line = _line.Clear();

        lock (Gate) {
            foreach (var (type, metric) in _sentByType) {
                Write(line, stamp, role, "sent_type", TypeName(type), metric);
            }

            foreach (var (type, metric) in _receivedByType) {
                Write(line, stamp, role, "recv_type", TypeName(type), metric);
            }

            foreach (var (peer, metric) in _sentByPeer) {
                Write(line, stamp, role, "sent_peer", peer.ToString(), metric);
            }

            foreach (var (peer, metric) in _receivedByPeer) {
                Write(line, stamp, role, "recv_peer", peer.ToString(), metric);
            }

            foreach (var ((category, key), metric) in _named) {
                Write(line, stamp, role, category, key, metric);
            }
#if DEBUG
            foreach (var (type, metric) in _sentByDelta) {
                Write(line, stamp, role, "sent_delta", type.Name, metric);
            }
#endif
        }

        // gauges: the value now. link: count = quality % (local), bytes = Steam's pending reliable bytes, ms = ping;
        // backlog: what waits in our own queue behind Steam's (count = messages, bytes)
        var peers = 0;
        for (var i = 0; i < _sockets.Length; i++) {
            if (_sockets[i] is not { } socket) {
                continue;
            }

            _sockets[i] = null;
            foreach (var peer in socket.PeerList) {
                peers++;
                var status = new SteamNetConnectionRealTimeStatus_t();
                var lanes = new SteamNetConnectionRealTimeLaneStatus_t();
                if (!NetShutdown.IsQuitting &&
                    SteamNetworkingSockets.GetConnectionRealTimeStatus(peer.Connection, ref status, 0, ref lanes) ==
                    EResult.k_EResultOK) {
                    Gauge(line, stamp, role, "link", peer.Id.ToString(), (long)(status.m_flConnectionQualityLocal * 100f),
                        status.m_cbPendingReliable, status.m_nPing);
                }

                Gauge(line, stamp, role, "backlog", peer.Id.ToString(), peer.BacklogCount, peer.BacklogBytes, 0);
            }
        }

        Gauge(line, stamp, role, "players", "session", session.CurrentPlayers.Count, 0, 0);
        Gauge(line, stamp, role, "players", "peers", peers, 0, 0);

        try {
            Directory.CreateDirectory(Folder);
            PruneOld();
            var path = Path.Combine(Folder, $"net_metrics_{DateTime.Now:yyyy-MM-dd}.csv");
            if (!File.Exists(path)) {
                line.Insert(0, "timestamp,role,category,key,count,bytes,ms\n");
            }

            File.AppendAllText(path, line.ToString());
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // the next period
        }
    }

    // under Gate: a line only for a counter that moved, then the period starts again
    private static void Write(StringBuilder line, string stamp, string role, string category, string key, NetMetric metric)
    {
        if (metric.Count != 0 || metric.Bytes != 0 || metric.Ticks != 0) {
            Gauge(line, stamp, role, category, key, metric.Count, metric.Bytes, ToMs(metric.Ticks));
        }

        metric.Roll();
    }

    private static void Gauge(StringBuilder line, string stamp, string role, string category, string key, long count,
        long bytes, double ms)
    {
        line.Append(stamp).Append(',').Append(role).Append(',').Append(category).Append(',').Append(key).Append(',')
            .Append(count).Append(',').Append(bytes).Append(',').Append(ms.ToString("0.###", CultureInfo.InvariantCulture)).Append('\n');
    }

    private static double ToMs(long ticks)
    {
        return ticks * 1000.0 / Stopwatch.Frequency;
    }

    private static string TypeName(uint hash)
    {
        return SteamNetTypeRegistry.Resolve(hash)?.Name.Replace(',', ';') ?? $"0x{hash:X8}";
    }

    /// <summary>
    ///     The totals since the game started (period included), largest first in each category
    /// </summary>
    internal static string Describe()
    {
        var rows = new List<(string category, string key, long count, long bytes, double ms)>();
        lock (Gate) {
            Add(_sentByType.Select(p => ("sent_type", TypeName(p.Key), p.Value)));
            Add(_receivedByType.Select(p => ("recv_type", TypeName(p.Key), p.Value)));
            Add(_sentByPeer.Select(p => ("sent_peer", p.Key.ToString(), p.Value)));
            Add(_receivedByPeer.Select(p => ("recv_peer", p.Key.ToString(), p.Value)));
            Add(_named.Select(p => (p.Key.Item1, p.Key.Item2, p.Value)));
#if DEBUG
            Add(_sentByDelta.Select(p => ("sent_delta", p.Key.Name, p.Value)));
#endif
        }

        var text = new StringBuilder("category key: count, bytes, ms (since start)\n");
        foreach (var row in rows.OrderBy(r => r.category).ThenByDescending(r => r.bytes).ThenByDescending(r => r.ms)) {
            text.Append($"{row.category} {row.key}: {row.count}, {row.bytes}, {row.ms.ToString("0.#", CultureInfo.InvariantCulture)}\n");
        }

        return text.ToString();

        void Add(IEnumerable<(string category, string key, NetMetric metric)> metrics)
        {
            foreach (var (category, key, m) in metrics) {
                if (m.TotalCount + m.Count != 0) {
                    rows.Add((category, key, m.TotalCount + m.Count, m.TotalBytes + m.Bytes, ToMs(m.TotalTicks + m.Ticks)));
                }
            }
        }
    }

    private static bool _pruned;

    // the files of more than a week ago go, once per run (a server writes one a day, 100 MB with 50 players)
    private static void PruneOld()
    {
        if (_pruned) {
            return;
        }

        _pruned = true;
        try {
            foreach (var file in Directory.GetFiles(Folder, "net_metrics_*.csv")) {
                if (File.GetLastWriteTime(file) < DateTime.Now.AddDays(-7)) {
                    File.Delete(file);
                }
            }
        } catch (IOException) {
        } catch (UnauthorizedAccessException) {
        }
    }
}
