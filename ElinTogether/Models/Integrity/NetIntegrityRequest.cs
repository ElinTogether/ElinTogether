using ElinTogether.Common;
using MessagePack;

namespace ElinTogether.Models;

[MessagePackObject]
public class NetIntegrityRequest
{
    [Key(0)]
    public required string HostModVersion { get; init; }

    [Key(1)]
    public required string HostGameVersion { get; init; }

    [Key(2)]
    public required BuildVersionIntegrity.APIVersion APIVersion { get; init; }

    public static NetIntegrityRequest Create()
    {
        return new() {
            HostModVersion = ModInfo.BuildVersion,
            HostGameVersion = BuildVersionIntegrity.GameVersion,
            APIVersion = BuildVersionIntegrity.APIVersionLatest,
        };
    }
}