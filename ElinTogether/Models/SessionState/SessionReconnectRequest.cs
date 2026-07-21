using ElinTogether.Net;
using MessagePack;

namespace ElinTogether.Models;

[MessagePackObject]
public class SessionReconnectRequest
{
    public static SessionReconnectRequest Current => new() {
        LobbyId = NetSession.Instance.Lobby.Current,
    };

    [Key(0)]
    public required ulong LobbyId { get; init; }
}