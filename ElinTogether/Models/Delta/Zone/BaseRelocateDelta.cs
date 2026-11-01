using System.Linq;
using ElinTogether.Helper;
using ElinTogether.Net;
using ElinTogether.Patches;
using MessagePack;

namespace ElinTogether.Models;

/// <summary>
///     A player on the world map reads a relocation deed and picks a base: the base moves to where it stands, in
///     the world the host keeps, for everyone (hunt 6 item 2). Read by a guest, it moved only in that guest's copy
///     of the world map (the host skips a remote read, TraitOnReadPatch), and came back at the next world copy. <br />
///     From a guest: a request, read again from the host's state (the sender holds the deed, the base is one of the
///     faction's and its team's land, the place is free). From the host: the base moved, to every player
/// </summary>
[MessagePackObject]
public class BaseRelocateDelta : ElinDelta
{
    /// <summary>
    ///     The base (its zone)
    /// </summary>
    [Key(0)]
    public required int ZoneUid { get; init; }

    /// <summary>
    ///     Where it goes, on the world map
    /// </summary>
    [Key(1)]
    public required int X { get; init; }

    [Key(2)]
    public required int Y { get; init; }

    /// <summary>
    ///     The deed read, in the reader's bag
    /// </summary>
    [Key(3)]
    public int DeedUid { get; init; }

    /// <summary>
    ///     The host's answer: it moved (to everyone), or not (to the reader)
    /// </summary>
    [Key(4)]
    public bool Moved { get; init; }

    [Key(5)]
    public bool Refused { get; init; }

    /// <summary>
    ///     The character of the player who read the deed
    /// </summary>
    [Key(6)]
    public int Reader { get; init; }

    internal override string? OnHostPermit(ElinNetHost host, Chara? sender)
    {
        if (Moved || Refused) {
            return "only the host says where a base is";
        }

        return NoCharacter(sender) ?? WhyNot(sender!, out _, out _);
    }

    internal override void OnHostRefused(ElinNetHost host)
    {
        host.SendDeltaTo(OriginPeer, new BaseRelocateDelta { ZoneUid = ZoneUid, X = X, Y = Y, Refused = true });
    }

    protected override void OnApply(ElinNetBase net)
    {
        if (net is ElinNetHost { IsZoneSession: false } host) {
            // all of it read again here, whatever the sender believed (and when the permit only logs)
            string? why = null;
            if (Moved || Refused || host.PlayerChara(OriginPeer) is not { } sender ||
                (why = WhyNot(sender, out var zone, out var deed)) is not null) {
                EmpLog.Information("Base {ZoneUid} not relocated for peer {Peer}: {Why}", ZoneUid, OriginPeer,
                    why ?? (Moved || Refused ? "an answer, not a request" : "no character"));
                OnHostRefused(host);
                return;
            }

            Relocate(host, sender, zone!, deed!, X, Y);
            return;
        }

        if (net is not ElinNetClient) {
            return;
        }

        if (Refused) {
            if (NetSession.Instance.Rules.HostManagesBase) {
                RemoteBasePaidPatch.Refuse(true);
            } else {
                SE.Beep();
                Msg.Say("cannot_use_here");
            }

            return;
        }

        if (!Moved || game.spatials.Find(ZoneUid) is not Zone moved) {
            return;
        }

        // the reader's own deed, in its own bag (alone on the world map, its game carries it back to the host)
        if (Reader == pc.uid) {
            (pc.things.List(t => t.uid == DeedUid, true).FirstOrDefault() ??
             pc.things.List(t => t.trait is TraitDeedRelocate, true).FirstOrDefault())?.ModNum(-1);
            Sound.Play("jingle_embark");
            pc.PlaySound("build");
        }

        Move(moved, X, Y);
    }

    /// <summary>
    ///     Why that player may not move this base there, null when it may: the reasons of the game's own read
    ///     (TraitDeedRelocate.OnRead), from what this game holds
    /// </summary>
    private string? WhyNot(Chara sender, out Zone? zone, out Thing? deed)
    {
        zone = null;
        deed = sender.things.List(t => t.uid == DeedUid && t.trait is TraitDeedRelocate, true).FirstOrDefault() ??
               sender.things.List(t => t.trait is TraitDeedRelocate, true).FirstOrDefault();
        if (NetSession.Instance.Rules.HostManagesBase) {
            return "only the host manages the base";
        }

        if (deed is null) {
            return "the sender holds no relocation deed";
        }

        if (game.spatials.Find(ZoneUid) is not Zone { branch: not null } found ||
            !pc.faction.GetChildren().Any(b => b.owner == found)) {
            return $"zone {ZoneUid} is not a base of the faction";
        }

        if (!Teams.CanTouch(sender, found)) {
            return $"base {ZoneUid} is not the land of the sender's team";
        }

        if (!TeamRanks.MayManageBase(sender, found)) {
            return $"the sender's rank does not manage base {ZoneUid}";
        }

        var map = world.region.elomap;
        if (world.region.GetZoneAt(X, Y) is { } there && there != found && (there is not Zone_Field || there.IsPCFaction) ||
            SiblingLands.LandsAt(X, Y).Any(land => land != found) || map.w > 0 && map.GetCell(X, Y) is null) {
            return $"the place {X},{Y} of the world map is taken";
        }

        zone = found;
        return null;
    }

    /// <summary>
    ///     The game's yes (TraitDeedRelocate.OnRead), in the world the host keeps: the deed is spent, the base goes
    ///     there, every player is told
    /// </summary>
    internal static void Relocate(ElinNetHost host, Chara reader, Zone zone, Thing deed, int x, int y)
    {
        EmpLog.Information("Base {ZoneUid} relocated to {X},{Y} by player {Uid}", zone.uid, x, y, reader.uid);
        using (Simulate()) {
            deed.ModNum(-1);
        }

        if (reader.IsPC) {
            Sound.Play("jingle_embark");
            reader.PlaySound("build");
        }

        Move(zone, x, y);
        host.SendDeltaToAllExcept(-1, new BaseRelocateDelta {
            ZoneUid = zone.uid,
            X = x,
            Y = y,
            DeedUid = deed.uid,
            Moved = true,
            Reader = reader.uid,
        });
    }

    /// <summary>
    ///     The base's place on this game's world map, as the game's yes sets it: a wild field there goes away
    /// </summary>
    private static void Move(Zone zone, int x, int y)
    {
        if (world.region.GetZoneAt(x, y) is Zone_Field { IsPCFaction: false, destryoed: false } field && field != zone) {
            field.Destroy();
        }

        // the world map's cells are made when it is first shown: until then the zone's place is all there is
        var map = world.region.elomap;
        if (map.w > 0) {
            map.SetZone(zone.x, zone.y, null);
        }

        var (fromX, fromY) = (zone.x, zone.y);
        zone.x = x;
        zone.y = y;
        // (several lands per tile: the land that stays there is the tile's icon again)
        SiblingLands.Refresh(fromX, fromY);
        if (map.w > 0) {
            map.SetZone(x, y, zone, updateMesh: _zone.IsRegion);
        }

        Msg.Say("base_relocate", zone.Name);
    }
}
