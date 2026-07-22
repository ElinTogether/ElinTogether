using ElinTogether.Net;
using HeathenEngineering.SteamworksIntegration;
using MessagePack;

namespace ElinTogether.Models;

[MessagePackObject]
public class SessionReconnectRequest
{
    public static SessionReconnectRequest Current => new() {
        LobbyId = NetSession.Instance.Lobby.Current,
    };

    [Key(0)]
    public required LobbyData LobbyId { get; init; }
}