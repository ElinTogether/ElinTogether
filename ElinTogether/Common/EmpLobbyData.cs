namespace ElinTogether.Common;

public static class EmpLobbyData
{
    public const string EmpVersion = "EmpVersion";
    public const string GameBuild = "EmpGameBuild";
    public const string CurrentZone = "CurrentZone";

    /// <summary>
    ///     The mods of the game, see ModList.Compact
    /// </summary>
    public const string Mods = "EmpMods";

    /// <summary>
    ///     Which world this game hosts and how many times it was taken over: a former host of that world finds
    ///     the game that goes on (WorldHandover.Watch)
    /// </summary>
    public const string World = "EmpWorld";

    public const string Handover = "EmpHandover";

    /// <summary>
    ///     "1" when anyone who finds the game can join it (PublicGame), "0" for the host's friends and invited players
    /// </summary>
    public const string Public = "EmpPublic";

    /// <summary>
    ///     What opened the game: "game" (a player's game), "hidden" (the hidden server a player's game started and
    ///     plays on, HiddenServer), "server" (a dedicated -empserver). The list keeps one game per host account and
    ///     prefers the hidden server's lobby
    /// </summary>
    public const string ServerKind = "EmpServerKind";
}
