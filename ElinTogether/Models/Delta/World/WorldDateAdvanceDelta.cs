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

        SetClientDate([..GameDate]);

        foreach (var zoneEvent in _zone.events.list) {
            zoneEvent.minElapsed += Minutes;
        }

        var ticks = Minutes * 4 / 6;
        if (ticks <= 0 || pc.isDead) {
            return;
        }

        EmpLog.Debug("Catching up needs for host time advance {AdvancedMins} {NeedTicks}",
            Minutes, ticks);

        using var _ = Simulate();
        for (var i = 0; i < ticks && !pc.isDead; ++i) {
            pc.TickConditions();
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