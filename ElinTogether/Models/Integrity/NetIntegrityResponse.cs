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

    /// <summary>
    ///     The game that started a hidden server on this machine shows the token it wrote for it (HiddenServer): that is
    ///     how the server knows the loopback seat of its own account is the owner's. Null from anyone else
    /// </summary>
    [Key(3)]
    public byte[]? OwnerToken { get; init; }

    /// <summary>
    ///     What this player played alone on the map it held when its link with this host dropped, kept as a checkpoint
    ///     of that moment (ElinNetClient.KeepPlayedAway): applied before the world is sent back, see
    ///     ElinNetHost.ApplyLostAway. Null when there is none
    /// </summary>
    [Key(4)]
    public ZoneLeaseRelease? LostAway { get; init; }

    /// <summary>
    ///     SHA-256 of the host's Steam id and the password this player gave for it (Helper.LobbyPassword), null when
    ///     none was asked. An older host ignores it
    /// </summary>
    [Key(5)]
    public byte[]? LobbyPasswordHash { get; init; }

    public static NetIntegrityResponse Create(byte[]? ownerToken = null, ZoneLeaseRelease? lostAway = null,
        byte[]? lobbyPasswordHash = null)
    {
        return new() {
            ClientModVersion = ModInfo.BuildVersion,
            ClientGameVersion = BuildVersionIntegrity.GameVersion,
            APIVersion = BuildVersionIntegrity.APIVersionLatest,
            OwnerToken = ownerToken,
            LostAway = lostAway,
            LobbyPasswordHash = lobbyPasswordHash,
        };
    }
}