using ElinTogether.Net;
using MessagePack;

namespace ElinTogether.Models;

[MessagePackObject]
public class SleepStartDelta : ElinDelta
{
    [Key(0)]
    public required int Hours { get; init; }

    protected override void OnApply(ElinNetBase net)
    {
        // host -> client broadcast only
        if (net.IsHost) {
            return;
        }

        if (pc.isDead || ui.GetLayer<LayerSleep>() is not null) {
            return;
        }

        EmpLog.Debug("Applying party sleep {SleepHours}", Hours);

        using var _ = Simulate();
        ui.AddLayer<LayerSleep>().Sleep(Hours, null);
    }
}