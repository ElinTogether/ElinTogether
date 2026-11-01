using System;
using System.Linq;
using ElinTogether.Common;
using ElinTogether.Helper;
using ElinTogether.Models;
using ElinTogether.Patches;
using UnityEngine;

namespace ElinTogether.Net;

/// <summary>
///     GitHub issue 46, host rule SoftRecall: we stay on the map we stand on while another player becomes the one
///     running it (the host left it and that player keeps it, or the player we visited left and the map went to
///     another visitor). We join that player's zone session as before, but keep our game and our scene: the player
///     keeping the map sends <see cref="ZoneSoftRejoin" /> instead of its world (ElinNetHost.TrySendSoftStay), we
///     compare its map with ours and are its client in place. The mirror of the host's SoftRecall, one level down.
///     Anything unexpected, or a map that differs, ends with its world as without the rule (ZoneSoftRejoinFailed)
/// </summary>
internal partial class ElinNetClient
{
    /// <summary>
    ///     Host link: how long nothing moves in this game while the zone session of the player keeping our map is
    ///     joined (its answer, the world or the switch in place). Past it the game runs again, whatever comes
    /// </summary>
    private const float SoftStayWaitSeconds = 20f;

    private float _softStayDeadline;

    /// <summary>
    ///     Host link: we joined the zone session of the map we stand on and wait for its answer: nothing moves here
    ///     meanwhile (PauseGame, IsInTransfer), what happened now would be in neither game
    /// </summary>
    internal bool IsAwaitingSoftStay => _softStayDeadline > 0;

    /// <summary>
    ///     Host link: the zone session we join is about the very map we stand on, as it stands on our screen
    /// </summary>
    private void BeginSoftStay(Zone zone)
    {
        if (!Session.Rules.SoftRecall || !core.IsGameStarted || _zone != zone || _map is null || pc.isDead) {
            return;
        }

        _softStayDeadline = Time.realtimeSinceStartup + SoftStayWaitSeconds;
        EmpLog.Debug("Staying on {ZoneFullName}: waiting for the player keeping it", zone.ZoneFullName);
    }

    /// <summary>
    ///     Host link: the zone session answered (the switch in place, or its world), or is gone
    /// </summary>
    internal void EndSoftStay()
    {
        _softStayDeadline = 0;
    }

    private void UpdateSoftStayWait()
    {
        if (_softStayDeadline > 0 && Time.realtimeSinceStartup >= _softStayDeadline) {
            _softStayDeadline = 0;
            EmpLog.Warning("No answer from the player keeping our map {Seconds:F0}s after joining it, the game runs again",
                SoftStayWaitSeconds);
        }
    }

    /// <summary>
    ///     Zone session client, net event: the player keeping the map we stand on runs it from now on, its world is
    ///     not sent. Applied, or refused and its world asked for
    /// </summary>
    private void OnZoneSoftStay(ZoneSoftRejoin stay)
    {
        // once only, at the join: a second one would adopt the characters and reset the cards again
        if (_handshakePhase == NetHandshakePhase.Joined) {
            EmpLog.Warning("Ignoring a second soft stay on zone {ZoneUid}", stay.ZoneUid);
            return;
        }

        string? refused;
        try {
            refused = ApplySoftStay(stay);
        } catch (Exception ex) {
            EmpLog.Warning(ex, "Staying on zone {ZoneUid} in place could not be applied", stay.ZoneUid);
            refused = $"{ex.GetType().Name}: {ex.Message}";
        }

        if (refused is null) {
            (Session.Transport as ElinNetClient)?.EndSoftStay();
            return;
        }

        // its world comes next (SaveDataProbe, accepted at this stage of the handshake): the game stays still
        // until then, see OnSaveDataProbe
        EmpLog.Warning("Staying on zone {ZoneUid} in place given up ({Reason}), asking for the world of the player keeping it",
            stay.ZoneUid, refused);
        Host.Send(new ZoneSoftRejoinFailed {
            ZoneUid = stay.ZoneUid,
            Reason = refused,
        });
    }

    /// <returns>why the world is needed after all, null when we are the client of the zone session in place</returns>
    private string? ApplySoftStay(ZoneSoftRejoin stay)
    {
        if (stay.Travel || !core.IsGameStarted || !Session.IsGuest || Session.AwayZone is not { } zone ||
            zone.uid != stay.ZoneUid || _zone != zone || _map is null || pc is not { isDead: false }) {
            return "not standing on that map";
        }

        // nothing waited for a number here: our cards have the ones both games know
        if (stay.Rebinds is { Count: > 0 }) {
            return $"{stay.Rebinds.Count} card(s) to renumber";
        }

        // what we carry, as the host handed it over: nothing moved here since (IsAwaitingSoftStay)
        var bag = NetDesync.Bag(pc);
        if (bag != stay.BagMix) {
            return $"bag differs (here {bag:X8}, there {stay.BagMix:X8})";
        }

        // the map really is the same: things on the floor, what they hold, the characters. Any difference is the
        // world (and its map), as without the rule
        var ours = ZoneLeaseState.Sums(_map);
        if (stay.MapSums is not { } theirs || !ours.SequenceEqual(theirs)) {
            return $"map differs (here {ZoneLeaseState.TellSums(ours)} | there {ZoneLeaseState.TellSums(stay.MapSums ?? [])})";
        }

        // from here we are its client: what it sends after this message is applied (ShouldReceiveHostPacket)
        AdvanceHandshake(NetHandshakePhase.Joined);

        if (stay.GameDate is { } date) {
            WorldDateAdvanceDelta.SetClientDate(date);
        }

        // the characters of the world standing on its map (that player, its companions, other visitors) as it
        // runs them: ours went on from our last world
        var arrived = AdoptHostCharas(stay.Charas);

        // as OnZoneDataResponse does for the map we would have loaded
        Session.CurrentZone = zone;
        ZoneLeaseState.Imported.Add(zone.uid);
        game.cards.uidNext = stay.UidNext;

        // every card of the map and of our bag is one of its game now, under the number both games have
        CardCache.Reset();
        CardCache.CacheCurrentZone();
        foreach (var chara in arrived) {
            CardCache.Add(chara);
            CardCache.CacheContainer(chara.things);
        }

        // ability tokens are each game's own
        foreach (var token in pc.things.Where(t => t.trait is TraitAbility).ToList()) {
            CardCache.Remove(token.uid);
        }

        CardAddThingEvent.AbilityLayoutDirty = true;

        Delta.ClearOut();
        Delta.ClearIn();
        NetDesync.HoldOff();

        // its placement only confirms our tile (the one our ack told, see RegisterGuest): no map follows
        _awaitingActivation = 0;
        _softPlaced = zone.uid;

        // after the switch: only what shows a character that was just replaced
        CloseWindowsOf(arrived);

        // as a player whose map is loaded: placed on its tile, settled
        Host.Send(new ZoneDataReceivedResponse {
            ZoneUid = zone.uid,
            ZoneFullName = zone.ZoneFullName,
        });

        EmpLog.Information("Staying on {ZoneFullName} in place: client of the player keeping it, no world copy ({Charas} character(s) taken, map {Sums})",
            zone.ZoneFullName, arrived.Count, ZoneLeaseState.TellSums(ours));
        return null;
    }
}
