using System.Collections.Immutable;
using System.Linq;
using ElinTogether.Helper;
using ElinTogether.Net;
using ElinTogether.Patches;
using MessagePack;

namespace ElinTogether.Models;

[MessagePackObject]
public class WorldDateAdvanceDelta : ElinDelta
{
    [Key(0)]
    public required int Minutes { get; init; }

    [Key(1)]
    public required ImmutableArray<int> GameDate { get; init; }

    /// <summary>
    ///     The sender was catching up with time made pass on another map, see WorldDateAdvanceEvent.CatchUp
    /// </summary>
    [Key(2)]
    public bool CatchUp { get; init; }

    /// <summary>
    ///     Council 2026-10-23: the player whose world map trip made these minutes pass, null for any other time
    /// </summary>
    [Key(3)]
    public string? Traveller { get; init; }

    /// <summary>
    ///     The character of <see cref="Traveller" /> on the host's map, 0 when it travels away from it
    /// </summary>
    [Key(4)]
    public int TravellerUid { get; init; }

    /// <summary>
    ///     Personal Pace: this catch-up pays the lead of the player with this character. Every game postpones its
    ///     quests by it, the traveller's too (they already lost its trip, see WorldMapLead); it alone is not told
    ///     it lost nothing
    /// </summary>
    [Key(5)]
    public int LeadOf { get; init; }

    // minutes the date of this client moved since the last of these messages: the world snapshot sets it too,
    // sometimes first
    private static int _moved;

    // the most of one jump this player lives (its hours and days), see OnApply
    private const int MaxLivedMinutes = 7 * Date.DayToken;

    protected override void OnApply(ElinNetBase net)
    {
        if (net.IsHost) {
            return;
        }

        // time another player made pass (elsewhere, or by a step of this map's holder on the world map) is
        // not taken from this player's quests. A night is its own: every player of the map sleeps it
        // except the steps of this player on the host's world map, which the host passes for it (council 2026-10-23)
        var own = LeadOf == 0 && TravellerUid != 0 && pc?.uid == TravellerUid;
        var others = !own && (CatchUp || (Minutes > 1 && pc?.conSleep is null));
        var myLead = LeadOf != 0 && pc?.uid == LeadOf;
        (string, int)? traveller = Traveller is null || own || myLead ? null : (Traveller, TravellerUid);

        // away from the host: this game simulates its own map and keeps its own date. With one date for the
        // world it catches up when the world went further without it; a visitor hears it from the map's holder
        if (NetSession.Instance.IsAway) {
            if (NetSession.Instance is { IsZoneAuthority: true, Rules.UseSharedWorldTime: true }) {
                // living world: a minute of the world's clock is lived on this map too, not caught up with
                if (CatchUp || Traveller is not null || !LivingWorld.Follow(Minutes, WorldDateAdvanceEvent.Minutes([..GameDate]))) {
                    WorldDateAdvanceEvent.CatchUpNextFrame([..GameDate], traveller);
                }
            } else if (others && net.IsZoneSession) {
                PersonalQuests.Postpone(Minutes);
            }

            _moved = 0;
            return;
        }

        // from the delta itself, not from this game's date: the world snapshot may have set the date already
        var now = WorldDateAdvanceEvent.Minutes([..GameDate]);
        var before = now - Minutes;

        // the date went further than the minutes told: hours that came with no word of their own (the express
        // travel of the map's holder calls GameDate.AdvanceHour, not AdvanceMin). Another player's time too, and the
        // days a long catch-up of the host skipped over (stage 5: up to a year, see WorldDateAdvanceEvent)
        var untold = _moved + now - world.date.GetRaw() - Minutes;
        PersonalQuests.Postpone((others ? Minutes : 0) + (untold is > 0 and <= Date.YearToken ? untold : 0));

        // where this game's date stood before this message (the world snapshot may have set it since: _moved)
        var previous = world.date.GetRaw() - _moved;

        SetClientDate([..GameDate]);
        _moved = 0;

        // the world went on by another player's trip: this one is told it lost nothing
        if (CatchUp && traveller is { } who) {
            WorldDateAdvanceEvent.NoteTrip(who.Item1, Minutes);
        }

        // the timer of a quest's map follows the one who simulates it, which leaves out the time another player
        // made pass elsewhere (WorldDateAdvanceEvent.OnAfterAdvanceMin)
        if (!CatchUp) {
            foreach (var zoneEvent in _zone.events.list) {
                zoneEvent.minElapsed += Minutes;
            }
        }

        // the day ended: the host read it in GameDate.AdvanceDay, and this game never ran that. Counted from the
        // earlier of the two starts: hours that came with no word of their own (AdvanceHour called alone, an
        // express travel) leave the message's own minutes inside the new day, and the boundary lies before them
        var dayFrom = System.Math.Min(before, previous);
        if (now / Date.DayToken > dayFrom / Date.DayToken && now - dayFrom <= 3 * Date.DayToken) {
            Msg.Say("endDay");
        }

        if (pc is not { isDead: false }) {
            return;
        }

        using var _ = Simulate();

        // no turns of hunger or conditions for a jump of the date: the game plays none for its own player
        // either (a night, a catch-up), except for the one who steps on the world map, and that is its step.
        // They were played here, without a limit: a day of someone's travel starved this character

        // what GameDate.AdvanceHour, Day, Month and Year do for the player of a game that owns its date
        // (up to a week: a host's long sleep or trip counted no day at all for its guests when it was over 3 days;
        // a longer jump counts its last week. A catch-up of the host comes a day per message, each counted, the days
        // it skipped over come without a word of their own, as the host did not live them either: stage 5)
        var hours = System.Math.Min(now / Date.HourToken - before / Date.HourToken, MaxLivedMinutes / Date.HourToken);
        if (hours > 0 && !pc.isDead) {
            for (var h = 0; h < hours; h++) {
                player.OnAdvanceHour();
            }
        }

        var days = System.Math.Min(now / Date.DayToken - before / Date.DayToken, MaxLivedMinutes / Date.DayToken);
        if (days > 0 && !pc.isDead) {
            for (var d = 0; d < days; d++) {
                player.stats.days++;
                player.questRerollCost = System.Math.Max(0, player.questRerollCost - 3);
                if (!player.prayed && pc.Evalue(FEAT.featModelBeliever) > 0) {
                    ActPray.TryPray(pc, true);
                }

                player.OnAdvanceDay();
            }

            // the day's report of the player's own base, as the game shows it at each new day (GameDate.AdvanceDay)
            if (pc.homeZone != null && pc.homeZone.mainFaction == pc.faction) {
                WidgetSticky.Add(new StickyHomeReport());
            }
        }

        if (now / Date.MonthToken > before / Date.MonthToken) {
            player.stats.months++;
            player.nums.OnAdvanceMonth();
            if (world.date.month % 2 == 0) {
                player.holyWell++;
            }

            // the season's look of the ground and of the things on it (GameDate.AdvanceMonth): a guest on the host's
            // map kept the old season's until it changed map
            if (_map is { } map) {
                map.RefreshAllTiles();
                foreach (var thing in map.things) {
                    if (thing.renderer.hasActor) {
                        thing.renderer.RefreshSprite();
                    }
                }
            }
        }

        if (now / Date.YearToken > before / Date.YearToken) {
            player.flags.santa = 0;
            player.wellWished = false;
            player.nums.OnAdvanceYear();
        }
    }

    internal static void SetClientDate(int[] raw)
    {
        var date = world.date;
        if (date.raw.SequenceEqual(raw)) {
            return;
        }

        _moved += WorldDateAdvanceEvent.Minutes(raw) - date.GetRaw();

        var hourChanged = date.hour != raw[3];
        date.raw = raw;

        screen.RefreshGrading();
        if (hourChanged) {
            scene.OnChangeHour();
        }
    }
}