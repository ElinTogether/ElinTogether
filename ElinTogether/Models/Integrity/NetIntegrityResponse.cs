using ElinTogether.Common;
using MessagePack;

namespace ElinTogether.Models;

[MessagePackObject]
public class NetIntegrityResponse
{
    [Key(0)]
    public required string ClientModVersion { get; init; }

    [Key(1)]
    public required string ClientGameVersion { get; init; }

    [Key(2)]
    public required BuildVersionIntegrity.APIVersion APIVersion { get; init; }

    public static NetIntegrityResponse Create()
    {
        return new() {
            ClientModVersion = ModInfo.BuildVersion,
            ClientGameVersion = BuildVersionIntegrity.GameVersion,
            APIVersion = BuildVersionIntegrity.APIVersionLatest,
        };
    }
}