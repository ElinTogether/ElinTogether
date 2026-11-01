using System;
using System.Collections.Generic;

namespace ElinTogether.Helper;

/// <summary>
///     What the election of the next keeper (PeerKeeper, PLAN_peer_hosted_server step P5) asks of the world's online
///     store (dev/etworld/API.md): who keeps the world, the lease (TAKE, BEAT, RELEASE next=S, 45 s), the head
///     version and its files. Every call blocks (HTTP): PeerKeeper makes them off the game's thread, one at a time.
///     A failure to reach the store is an IOException; a refusal of the store is an answer (<see cref="StoreReply.Error" />)
/// </summary>
internal interface IWorldStore
{
    /// <summary>
    ///     A store is set for this game (the address is known): the election runs on it. False: the takeover of a
    ///     peer-hosted world stays the one of P4 (copy kept by the guests, smallest Steam id first)
    /// </summary>
    bool Ready { get; }

    /// <summary>
    ///     GET who: the lease (null when free), a reservation after a RELEASE next=S, the head, and whether this
    ///     install may keep the world
    /// </summary>
    StoreWho Who();

    /// <summary>
    ///     POST take: this install keeps the world (taking it again renews). Refusals: held, reserved, banned, too_new,
    ///     not_vouched, debug_build
    /// </summary>
    StoreReply<StoreLease> Take();

    /// <summary>
    ///     POST beat, with the succession list the keeper computed (≤ 32 Steam ids). Refusals: not_holder, banned
    /// </summary>
    StoreReply<StoreBeat> Beat(long handover, IReadOnlyList<ulong> succession);

    /// <summary>
    ///     POST release, the next keeper reserved for 15 s when <paramref name="next" /> is not 0
    /// </summary>
    StoreReply<bool> Release(long handover, ulong next);

    /// <summary>
    ///     POST cert: a host certificate naming the keeper's Steam lobby (plan P3), renewed by every BEAT after it
    /// </summary>
    StoreReply<string> Cert(long handover, ulong lobby);

    /// <summary>
    ///     The files of a save folder as a new version over the head <paramref name="parent" /> (0: the first one):
    ///     the blobs the store lacks are sent first (the lease renewed on the way). The version's number. Refusals: not_holder, rolled_back,
    ///     handover_mismatch, parent_mismatch, too_big, banned, rate_limited
    /// </summary>
    StoreReply<long> Put(string folder, long parent, long handover, string reason, string gameDate, string saveTime);

    /// <summary>
    ///     GET head: the current version with its files, null when the world has none yet
    /// </summary>
    StoreVersion? Head();

    /// <summary>
    ///     Writes every file of <paramref name="version" /> into <paramref name="folder" /> (made, empty): a file of
    ///     <paramref name="reuse" /> (a copy kept on this disk) with the same SHA-256 is copied from there, the rest is
    ///     fetched. Every file is checked against its hash. The lease <paramref name="handover" /> is renewed on the way
    ///     (a big world on a slow line takes longer than it lives). Empty when written, else why not
    /// </summary>
    string Download(StoreVersion version, string folder, string? reuse, long handover);
}

internal sealed class StoreReply<T>
{
    internal T? Value { get; init; }

    /// <summary>
    ///     The store's error code (API.md section 6), empty on success
    /// </summary>
    internal string Error { get; init; } = "";

    /// <summary>
    ///     With held / not_holder: the lease the store has
    /// </summary>
    internal StoreLease? Lease { get; init; }

    /// <summary>
    ///     With parent_mismatch: the head the store has; with rolled_back: the rollback version
    /// </summary>
    internal long Number { get; init; }

    internal bool Ok => Error.Length == 0;

    internal static StoreReply<T> Of(T value) => new() { Value = value };

    internal static StoreReply<T> No(string error, StoreLease? lease = null, long number = 0) =>
        new() { Error = error, Lease = lease, Number = number };
}

internal sealed class StoreLease
{
    internal string Key { get; init; } = "";
    internal ulong Steam { get; init; }
    internal string Name { get; init; } = "";
    internal long Since { get; init; }
    internal long Until { get; init; }
    internal long Handover { get; init; }
    internal long Rollback { get; init; }
    internal ulong[] Succession { get; init; } = [];
}

internal sealed class StoreWho
{
    /// <summary>
    ///     The store's clock, Unix seconds
    /// </summary>
    internal long Now { get; init; }

    internal StoreLease? Lease { get; init; }

    internal ulong ReservedFor { get; init; }
    internal long ReservedUntil { get; init; }

    internal StoreVersion? Head { get; init; }

    /// <summary>
    ///     This install may keep the world (who.you.eligible), and why not
    /// </summary>
    internal bool Eligible { get; init; }

    internal string Why { get; init; } = "";

    /// <summary>
    ///     This install's key (who.you.key): a lease of that key is this machine's own
    /// </summary>
    internal string MyKey { get; init; } = "";

    internal bool Free => Lease is null || Lease.Until <= Now;

    internal bool ReservedForOther(ulong me) => ReservedFor != 0 && ReservedFor != me && ReservedUntil > Now;
}

internal sealed class StoreBeat
{
    internal long Until { get; init; }
    internal long Head { get; init; }

    /// <summary>
    ///     An admin rolled the world back to this version during this lease (0: no): the keeper saves nothing more
    /// </summary>
    internal long Rollback { get; init; }

    /// <summary>
    ///     A fresh host certificate for the lobby named by Cert, null: none
    /// </summary>
    internal string? Cert { get; init; }
}

internal sealed class StoreFile
{
    internal string Path { get; init; } = "";
    internal long Size { get; init; }
    internal string Sha256 { get; init; } = "";
    internal string Blob { get; init; } = "";
    internal string Enc { get; init; } = "raw";
}

/// <summary>
///     A version of the world: its summary, and its files when the manifest was read (GET head)
/// </summary>
internal sealed class StoreVersion
{
    internal long N { get; init; }
    internal long Handover { get; init; }
    internal string Reason { get; init; } = "";
    internal long Time { get; init; }
    internal ulong Steam { get; init; }
    internal string Name { get; init; } = "";
    internal string SaveTime { get; init; } = "";
    internal StoreFile[] Files { get; init; } = [];

    /// <summary>
    ///     When the save was made, as the keeper said (save_time), else when the store received it
    /// </summary>
    internal DateTime SavedUtc => DateTime.TryParse(SaveTime, null, System.Globalization.DateTimeStyles.AdjustToUniversal |
                                                                      System.Globalization.DateTimeStyles.AssumeUniversal, out var at)
        ? at
        : DateTimeOffset.FromUnixTimeSeconds(Time).UtcDateTime;
}

/// <summary>
///     The store this game uses: the ETWorld service the save depot names (EtWorldPeerStore, ready only with an
///     "etworld:" DepotPath); the election is not used without it
/// </summary>
internal static class PeerStore
{
    internal static IWorldStore Current { get; set; } = new EtWorldPeerStore();

    /// <summary>
    ///     A store that is never ready (tests)
    /// </summary>
    internal sealed class NoWorldStore : IWorldStore
    {
        public bool Ready => false;

        public StoreWho Who() => throw new System.IO.IOException("no world store set");

        public StoreReply<StoreLease> Take() => StoreReply<StoreLease>.No("no_store");

        public StoreReply<StoreBeat> Beat(long handover, IReadOnlyList<ulong> succession) => StoreReply<StoreBeat>.No("no_store");

        public StoreReply<bool> Release(long handover, ulong next) => StoreReply<bool>.No("no_store");

        public StoreReply<string> Cert(long handover, ulong lobby) => StoreReply<string>.No("no_store");

        public StoreReply<long> Put(string folder, long parent, long handover, string reason, string gameDate, string saveTime) =>
            StoreReply<long>.No("no_store");

        public StoreVersion? Head() => null;

        public string Download(StoreVersion version, string folder, string? reuse, long handover) => "no world store set";
    }
}
