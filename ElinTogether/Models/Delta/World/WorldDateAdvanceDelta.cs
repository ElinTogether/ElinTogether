using System.Collections.Immutable;
using System.Linq;
using ElinTogether.Net;
using MessagePack;

namespace ElinTogether.Models;

[MessagePackObject]
public class WorldDateAdvanceDelta : ElinDelta
{
    [Key(0)]
    public required int Minutes { get; init; }

    [Key(1)]
    public required ImmutableArray<int> GameDate { get; init; }

    protected override void OnApply(ElinNetBase net)
    {
        if (net.IsHost) {
            return;
        }

        var hours = world.date.GetRaw() / 60;
        var days = world.date.GetRawDay();

        SetClientDate([..GameDate]);

        foreach (var zoneEvent in _zone.events.list) {
            zoneEvent.minElapsed += Minutes;
        }

        var ticks = Minutes * 4 / 6;
        if (ticks <= 0 || pc.isDead) {
            return;
        }

        EmpLog.Debug("Catching up host time adv {AdvancedMins} {NeedTicks}",
            Minutes, ticks);

        using var _ = Simulate();
        for (var i = 0; i < ticks && !pc.isDead; ++i) {
            pc.TickConditions();
        }

        hours = world.date.GetRaw() / 60 - hours;
        days = world.date.GetRawDay() - days;
        if (hours is > 0 and <= 24 && !pc.isDead) {
            for (var h = 0; h < hours; h++) {
                player.OnAdvanceHour();
            }

            if (!player.prayed && pc.Evalue(FEAT.featModelBeliever) > 0) {
                ActPray.TryPray(pc, true);
            }
        }

        if (days is > 0 and <= 3 && !pc.isDead) {
            for (var d = 0; d < days; d++) {
                player.OnAdvanceDay();
            }
        }
    }

    internal static void SetClientDate(int[] raw)
    {
        var date = world.date;
        if (date.raw.SequenceEqual(raw)) {
            return;
        }

        var hourChanged = date.hour != raw[3];
        date.raw = raw;

        screen.RefreshGrading();
        if (hourChanged) {
            scene.OnChangeHour();
        }
    }
}