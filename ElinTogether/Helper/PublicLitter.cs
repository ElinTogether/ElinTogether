using System.Collections.Generic;
using System.Linq;
using ElinTogether.Net;

namespace ElinTogether.Helper;

/// <summary>
///     User 2026-11-17 ("delete all the garbage"): on the public worlds, the lands of players who were away piled up
///     the game's litter (GitHub issue 44, FactionBranch.GenerateGarbage while time ran without them). Once per land,
///     by the game that runs its map (the host, or the player holding it away): the litter lying on the ground goes.
///     What is in a trash bin or installed stays
/// </summary>
internal static class PublicLitter
{
    // a land swept already (Spatial.mapInt)
    private const int SweptKey = 9311701;

    private static readonly HashSet<string> Litter = ["trash1", "trash2", "529", "1170", "_poop"];

    internal static void Sweep(Zone zone)
    {
        var session = NetSession.Instance;
        if (zone.branch is null || zone.map is null || zone.GetInt(SweptKey) != 0 || session.Transport is null) {
            return;
        }

        // only the game that runs this map: the host (public world), or a player holding it away (public world)
        var runs = session.Connection is ElinNetHost
            ? ElinNetHost.PublicWorld
            : session.AwayZone == zone && PeerHosting.PublicEntered;
        if (!runs) {
            return;
        }

        zone.SetInt(SweptKey, 1);
        var litter = zone.map.things.Where(t => t is { isDestroyed: false, IsInstalled: false, parent: Zone } &&
                                                (Litter.Contains(t.id) || t.category?.id == "garbage")).ToList();
        foreach (var thing in litter) {
            thing.Destroy();
        }

        if (litter.Count > 0) {
            EmpLog.Information("Public world: {Count} pieces of litter swept from the land {Zone}", litter.Count, zone.ZoneFullName);
        }
    }
}
