global using EmpLog = Serilog.Log;
global using EmpPop = ElinTogether.EmpLogger;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using ElinTogether.Helper;
using ElinTogether.Models;
using ElinTogether.Net;
using ElinTogether.Net.Steam;
using EModding.Helper;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;
using UnityEngine;
using ILogger = Serilog.ILogger;

#pragma warning disable CA2254

namespace ElinTogether;

internal static partial class EmpLogger
{
    private const string LogDirectory = "ElinMP/Logs";
    private const string LogPrefix = "Session_";
    private const int RetainedSessionCount = 10;

    private static readonly ConcurrentDictionary<IPAddress, string> _hashCache = [];

    private static ILogger DefaultLogger => field ??= GetDefaultLoggerConfiguration().CreateLogger();

    internal static string SessionLogPath => field ??= CreateSessionLogPath();

    internal static LoggerConfiguration GetDefaultLoggerConfiguration()
    {
        return new LoggerConfiguration()
#if DEBUG
            .MinimumLevel.Verbose()
#else
            .MinimumLevel.Verbose() // beta testing
#endif
            .ConfigureDestructures()
            .ConfigureEnrichers()
            .ConfigureSinks();
    }

    internal static void InitLogger(ILogger? custom = null)
    {
        EmpLog.Logger = custom ?? DefaultLogger;
    }

    private static string CreateSessionLogPath()
    {
        var dir = Path.Combine(Application.persistentDataPath, LogDirectory);
        Directory.CreateDirectory(dir);
        PruneSessionLogs(dir);

        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var path = Path.Combine(dir, $"{LogPrefix}{stamp}.log");
        using var _ = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite);
        return Path.Combine(dir, $"{LogPrefix}{stamp}_{Process.GetCurrentProcess().Id}.log");
    }

    private static void PruneSessionLogs(string dir)
    {
        try {
            foreach (var stale in Directory.GetFiles(dir, $"{LogPrefix}*.log")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Skip(Math.Max(RetainedSessionCount - 1, 0))) {
                IO.DeleteFile(stale.FullName);
            }
        } catch {
            // noexcept
        }
    }

    extension(LoggerConfiguration lc)
    {
        private LoggerConfiguration ConfigureEnrichers()
        {
            return lc
                .Enrich.FromLogContext()
                .Enrich.With<NetSessionStateEnricher>()
                .Enrich.When(
                    l => l.Level >= LogEventLevel.Warning,
                    lec => lec.WithProperty("EmpVersion", ModInfo.BuildVersion));
        }

        private LoggerConfiguration ConfigureDestructures()
        {
            return lc
                .Destructure.ByTransforming<SteamNetPeer>(p => new {
                    Index = p.Id,
                    PlayerName = p.Colorize(p.User.Name),
                })
                .Destructure.ByTransforming<SteamNetPeerStat>(ps => new {
                    Sent = ps.BytesSent.ToAllocateString(),
                    Received = ps.BytesReceived.ToAllocateString(),
                    ps.PacketsSent,
                    ps.PacketsReceived,
                    ps.LastPingMs,
                    AvgPingMs = Math.Round(ps.AvgPingMs, 1),
                    ps.ConnectionQualityLocal,
                    ps.ConnectionQualityRemote,
                    OutKBps = Math.Round(ps.AvgBpsOut / 1024f, 1),
                    InKBps = Math.Round(ps.AvgBpsIn / 1024f, 1),
                    LastUpdated = ps.LastUpdated.ToString("HH:mm:ss"),
                })
                .Destructure.ByTransforming<NetPeerState>(ps => new {
                    ps.Index,
                    RemoteIdentity = ps.User,
                    PlayerName = ps.User.Name,
                })
                .Destructure.ByTransforming<NetSession>(s => new {
                    Role = s.IsHost ? "Host" : "Client",
                    s.SessionId,
                    s.Tick,
                    s.SyncMode,
                })
                .Destructure.ByTransforming<Point>(p => new {
                    X = p.x,
                    Z = p.z,
                })
                .Destructure.ByTransforming<MapDataRequest>(z => new {
                    ZoneFullName = z.ZoneFullName.TagColor(0x009e73),
                    z.ZoneUid,
                })
                .Destructure.ByTransforming<ZoneDataResponse>(z => new {
                    ZoneFullName = z.ZoneFullName.TagColor(0x009e73),
                    z.ZoneUid,
                })
                .Destructure.ByTransforming<RemoteCard>(p => new {
                    p.Uid,
                });
        }

        private LoggerConfiguration ConfigureSinks()
        {
            return lc
                .WriteTo.Console(
                    outputTemplate: "[EMP][{Level:u4}-{Timestamp:HH:mm:ss}] {Message:lj}{NewLine}{Exception}")
                .WriteTo.File(
                    new CompactJsonFormatter(new PlainJsonValueFormatter()),
                    SessionLogPath,
                    LogEventLevel.Debug,
#if !DEBUG
                    buffered: true,
#endif
                    rollingInterval: RollingInterval.Infinite);
        }
    }

    extension(IPAddress address)
    {
        internal string RedactedIp =>
            _hashCache.GetOrAdd(address,
                ip => Convert.ToBase64String([..ip.ToString().GetSha256Hash()], 0, 6)
                    .TrimEnd('=')
                    .Replace('+', '-')
                    .Replace('/', '_'));
    }

    extension(IPEndPoint endPoint)
    {
        internal string RedactedIp => endPoint.Address.RedactedIp;
    }

    extension(string address)
    {
        internal string RedactedIp =>
            IPAddress.TryParse(address, out var ipv4Or6) ||
            IPAddress.TryParse(address.Split(':')[0], out ipv4Or6)
                ? ipv4Or6.RedactedIp
                : address;
    }
}