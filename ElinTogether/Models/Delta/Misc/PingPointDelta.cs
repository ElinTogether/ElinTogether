using ElinTogether.Helper;
using ElinTogether.LangMod;
using ElinTogether.Net;
using MessagePack;

namespace ElinTogether.Models;

[MessagePackObject]
public class PingPointDelta : ElinDelta
{
    [Key(0)]
    public required int SenderIndex { get; init; }

    [Key(1)]
    public required Position Pos { get; init; }

    // TODO: temp
    [Key(2)]
    public required int Type { get; init; }

    protected override void OnApply(ElinNetBase net)
    {
        if (SenderIndex == NetSession.Instance.Self!.Index) {
            return;
        }

        if (net.IsHost) {
            net.Delta.AddRemote(this);
        }

        Play();
    }

    public void Play()
    {
        if (!core.IsGameStarted) {
            return;
        }

        var color = PeerColorizer.GetColor(SenderIndex);
        var effect = Effect.Get("rod");
        effect.sr.color = color;
        effect.Play(Pos);

        var sender = NetSession.Instance.CurrentPlayers.Find(p => p.Index == SenderIndex);
        WidgetPopText.Say("emp_ui_ping".Loc(sender?.Name ?? $"Player {SenderIndex}"));
    }

    public static PingPointDelta Ping(Point point)
    {
        var ping = new PingPointDelta {
            SenderIndex = NetSession.Instance.Self!.Index,
            Pos = point,
            Type = -1,
        };
        ping.Play();
        return ping;
    }
}