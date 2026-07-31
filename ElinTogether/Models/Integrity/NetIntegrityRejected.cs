using System.Collections.Generic;
using ElinTogether.Common;
using MessagePack;

namespace ElinTogether.Models;

[MessagePackObject]
public class NetIntegrityRejected
{
    public enum NetIntegrityRejectReason
    {
        VersionMismatch,
        ActMappingMismatch,
        IntegrityMismatch,
    }

    [Key(0)]
    public required NetIntegrityRejectReason Reason { get; init; }

    [Key(1)]
    public required string HostModVersion { get; init; }

    [Key(2)]
    public required string HostGameVersion { get; init; }

    [Key(3)]
    public List<string> Details { get; set; } = [];

    public static NetIntegrityRejected Create(NetIntegrityRejectReason reason, IEnumerable<string>? details = null)
    {
        return new() {
            Reason = reason,
            HostModVersion = ModInfo.BuildVersion,
            HostGameVersion = BuildVersionIntegrity.GameVersion,
            Details = details is null ? [] : [..details],
        };
    }
}