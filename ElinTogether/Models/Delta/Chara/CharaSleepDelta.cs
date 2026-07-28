using ElinTogether.Net;
using MessagePack;

namespace ElinTogether.Models;

[MessagePackObject]
public class CharaSleepDelta : ElinDelta
{
    [Key(0)]
    public required int Power { get; init; }

    [Key(1)]
    public required int Days { get; init; }

    protected override void OnApply(ElinNetBase net)
    {
        if (net.IsHost) {
            return;
        }

        if (pc.isDead) {
            return;
        }

        EmpLog.Debug("Applying host sleep {SleepPower}", Power);

        using var _ = Simulate();
        pc.OnSleep(Power, Days, pc.pos.IsSunLit);
    }
}