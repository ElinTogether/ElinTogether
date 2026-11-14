using System;
using ElinTogether.Net;
using ElinTogether.Patches;
using MessagePack;

namespace ElinTogether.Models;

[MessagePackObject]
public class SpatialGenDelta : ElinDelta
{
    [Key(0)]
    public required string ZoneFullName { get; init; }

    [Key(1)]
    public required int ZoneUid { get; init; }

    [Key(2)]
    public required Position Pos { get; init; }

    [Key(3)]
    public required int ParentZoneUid { get; init; }

    [Key(4)]
    public int Icon { get; init; }

    /// <summary>
    ///     The zone of a quest: it sits on the tile of the town it comes from, without taking its place on the map
    /// </summary>
    [Key(5)]
    public bool IsInstance { get; init; }

    /// <summary>
    ///     A site found by reading a map (TraitScrollMap) is known and has a prefix ("Chilly"), set after it is made:
    ///     each game rolled its own name and the guest's did not show it on the world map (hunt 2 row 21)
    /// </summary>
    [Key(6)]
    public bool IsKnown { get; init; }

    [Key(7)]
    public int IdPrefix { get; init; }

    /// <summary>
    ///     A random site (nefia) has its level and expires: without these the guest's copy had the level of its
    ///     source and never expired, whoever the site was drawn for (H5-04)
    /// </summary>
    [Key(8)]
    public bool IsRandomSite { get; init; }

    [Key(9)]
    public int DangerLv { get; init; }

    [Key(10)]
    public int DateExpire { get; init; }

    public static SpatialGenDelta Create(Zone zone)
    {
        return new() {
            ZoneFullName = zone.ZoneFullName,
            ZoneUid = zone.uid,
            Pos = new() { X = zone.x, Z = zone.y },
            ParentZoneUid = zone.parent?.uid ?? -1,
            Icon = zone.icon,
            // sent a frame after the zone is created, the game has set its instance by then
            IsInstance = zone.IsInstance,
            IsKnown = zone.isKnown,
            IdPrefix = zone.idPrefix,
            IsRandomSite = zone.isRandomSite,
            DangerLv = zone._dangerLv,
            DateExpire = zone.dateExpire,
        };
    }

    protected override void OnApply(ElinNetBase net)
    {
        if (net.IsHost) {
            // reject every single zone creation from clients
            return;
        }

        var remoteZone = game.spatials.Find(ZoneUid);
        if (remoteZone?.ZoneFullName == ZoneFullName && remoteZone.uid == ZoneUid) {
            // we already handled this on zone data response code
            Name(remoteZone);
            return;
        }

        var (_, zoneId, zoneLv) = Zone.ParseZoneFullName(ZoneFullName);
        var parent = game.spatials.Find(ParentZoneUid);
        remoteZone = SpatialGen.Create(zoneId, parent, false, Pos.X, Pos.Z) as Zone;

        if (remoteZone is null) {
            return;
        }

        remoteZone.lv = zoneLv;
        remoteZone.uid = ZoneUid;
        Name(remoteZone);

        game.spatials.uidNext = Math.Max(game.spatials.uidNext, ZoneUid + 1);
        if (game.spatials.Find(ZoneUid) is { } exist && exist != remoteZone) {
            // GitHub issue 28: a zone made here meanwhile (a field of the world map the player stood on) was dropped
            // from the list under the player's feet: it felt like a rollback, then a jump to another map. It keeps
            // its place with a number of its own, as ElinNetClientTravel does when the host's grant renumbers a zone
            game.spatials.map.Remove(ZoneUid);
            game.spatials.AssignUID(exist);
            foreach (var thing in _map?.things ?? []) {
                if (thing.c_uidZone == ZoneUid) {
                    thing.c_uidZone = exist.uid;
                }
            }

            EmpLog.Warning("Zone uid {ZoneUid} taken by local zone {LocalZoneFullName}, which is now {LocalUid}; " +
                           "the host's {ZoneFullName} takes the uid",
                ZoneUid, exist.ZoneFullName, exist.uid, ZoneFullName);
        }

        game.spatials.map[ZoneUid] = remoteZone;

        // update on overworld
        if (parent is Region region && !IsInstance) {
            region.elomap.SetZone(Pos.X, Pos.Z, remoteZone, true);
        }

        SpatialGenEvent.HeldRefZones[ZoneUid] = remoteZone;
    }

    private void Name(Zone zone)
    {
        zone.isKnown |= IsKnown;
        zone.idPrefix = IdPrefix;
        if (IsRandomSite) {
            zone.isRandomSite = true;
            zone._dangerLv = DangerLv;
            zone.dateExpire = DateExpire;
        }
    }
}