global using EmpLog = Serilog.Log;
global using EmpPop = ElinTogether.EmpLogger;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using ElinTogether.Helper;
using ElinTogether.Models;
using ElinTogether.Net;
using ElinTogether.Net.Steam;
using EModding.Helper;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Compact;
using UnityEngine;
using ILogger = Serilog.ILogger;

#pragma warning disable CA2254

namespace ElinTogether;

internal static partial class EmpLogger
{
    private static readonly ConcurrentDictionary<IPAddress, string> _hashCache = [];

    private static ILogger DefaultLogger => field ??= GetDefaultLoggerConfiguration().CreateLogger();

    // the mod log's level: Information in a release build, Debug with the player's box "Detailed logs"
    // (Client.DetailedLogs) and always in a debug build (the bench reads Debug lines). Verbose in release builds made
    // and wrote every Debug line of every frame (efficiency audit 2026-11-03)
#if DEBUG
    private const LogEventLevel DefaultLevel = LogEventLevel.Debug;
#else
    private const LogEventLevel DefaultLevel = LogEventLevel.Information;
#endif

    internal static readonly LoggingLevelSwitch Level = new(DefaultLevel);

    /// <summary>
    ///     The player's box "Detailed logs": the Debug lines go to the mod log too
    /// </summary>
    internal static void SetDetailed(bool detailed)
    {
        Level.MinimumLevel = detailed ? LogEventLevel.Debug : DefaultLevel;
    }

    internal static LoggerConfiguration GetDefaultLoggerConfiguration()
    {
        return new LoggerConfiguration()
            .MinimumLevel.ControlledBy(Level)
            .ConfigureDestructures()
            .ConfigureEnrichers()
            .ConfigureSinks();
    }

    // the game's own thread (the plugin starts on it): the game's UI is only touched from there
    internal static int MainThread { get; private set; } = -1;

    internal static void InitLogger(ILogger? custom = null)
    {
        MainThread = System.Threading.Thread.CurrentThread.ManagedThreadId;
        EmpLog.Logger = custom ?? DefaultLogger;
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
                    PeerIndex = p.Id,
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
                    PeerIndex = ps.Index,
                    RemoteIdentity = ps.User,
                    PlayerName = ps.User.Name,
                })
                .Destructure.ByTransforming<NetSession>(s => new {
                    Role = s.IsAway ? "Away" : s.IsHost ? "Host" : "Client",
                    s.SessionId,
                    s.Tick,
                    //s.SyncMode,
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
                    outputTemplate: "[EMP][{Level:u4}-{Timestamp:HH:mm:ss}] {Message:lj}{NewLine}{Exception}",
#if DEBUG
                    restrictedToMinimumLevel: LogEventLevel.Debug)
#else
                    // the console is for what a player may read: the detail stays in the file
                    restrictedToMinimumLevel: LogEventLevel.Information)
#endif
                .WriteTo.File(
                    new CompactJsonFormatter(new PlainJsonValueFormatter()),
                    Path.Combine(Application.persistentDataPath, "ElinMP/Logs/Session_.log"),
                    LogEventLevel.Debug,
#if DEBUG
                    shared: true,
#else
                    buffered: true,
                    // written out every few seconds: a bug report reads the end of this file (BugReport), and with
                    // fewer lines the buffer could hold the last minutes back for long
                    flushToDiskInterval: TimeSpan.FromSeconds(5),
#endif
                    rollingInterval: RollingInterval.Day,
                    // a long-running server: a new file past 100 MB, the last ones kept (reliability audit 2026-10-27:
                    // at 1 GB Serilog stopped writing for the rest of the day, unsaid)
                    fileSizeLimitBytes: 100L * 1024 * 1024,
                    rollOnFileSizeLimit: true,
                    retainedFileCountLimit: 6);
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