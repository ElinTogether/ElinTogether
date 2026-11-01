using System.Linq;
using ElinTogether.Net;
using ElinTogether.Patches;
using MessagePack;

namespace ElinTogether.Models;

/// <summary>
///     To the host: Farris came on Nymelle's second floor in the game of the player holding that floor (the game's
///     first visit, or <see cref="NymelleFarrisRepair" />). She is a character of the world: the map handed back never
///     carries her (Map.Save leaves the world's characters out) and nothing else told the host, so the host's world
///     had no Farris, the floor was visited, and the game never made her again (GitHub issue 55). The host makes its
///     own Farris there, under the same number, so the dialog's outcome (StoryOutcomeDelta, QuestExploration_MeetFarris)
///     finds her and every later copy of the world has her
/// </summary>
[MessagePackObject]
public class NymelleFarrisDelta : ElinDelta
{
    [Key(0)]
    public required int ZoneUid { get; init; }

    [Key(1)]
    public required int Uid { get; init; }

    [Key(2)]
    public required int X { get; init; }

    [Key(3)]
    public required int Z { get; init; }

    internal override string? OnHostPermit(ElinNetHost host, Chara? sender)
    {
        if (NoCharacter(sender) is { } none) {
            return none;
        }

        return host.LeasedZones(OriginPeer).Any(z => z?.uid == ZoneUid) ? null : $"Farris on zone {ZoneUid}, not a zone the sender holds";
    }

    protected override void OnApply(ElinNetBase net)
    {
        if (net is not ElinNetHost { IsZoneSession: false }) {
            return;
        }

        if (game.cards.globalCharas.Find("farris") is { } known) {
            if (known.uid != Uid) {
                EmpLog.Information("Farris told on Nymelle by peer {PeerIndex} as {Uid}, the world has her as {Known}: kept",
                    OriginPeer, Uid, known.uid);
            }

            return;
        }

        if (game.spatials.Find(ZoneUid) is not Zone_Nymelle { lv: -2 } zone || Uid <= 0 || CardCache.Find(Uid) is not null ||
            game.cards.globalCharas.ContainsKey(Uid)) {
            EmpLog.Warning("Farris told on zone {ZoneUid} as {Uid} by peer {PeerIndex}: not Nymelle's second floor or a taken number",
                ZoneUid, Uid, OriginPeer);
            return;
        }

        var farris = NymelleFarrisRepair.Create(Uid);
        zone.AddCard(farris, X, Z);
        // (a map not loaded here: Zone.AddGlobalCharasOnActivate places her where she stands when it is)
        farris.pos.Set(X, Z);
        EmpLog.Information("Farris made on Nymelle's second floor ({ZoneUid}) as {Uid}, as in the game of peer {PeerIndex}",
            ZoneUid, Uid, OriginPeer);
    }
}
