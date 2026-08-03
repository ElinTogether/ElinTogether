using ElinTogether.Helper;
using ElinTogether.LangMod;
using ElinTogether.Net;
using MessagePack;

namespace ElinTogether.Models;

[MessagePackObject]
public class SleepReadyDelta : ElinDelta
{
    [Key(0)]
    public required int PlayerIndex { get; init; }

    [Key(1)]
    public required bool Ready { get; init; }

    [Key(2)]
    public required int ReadyCount { get; init; }

    [Key(3)]
    public required int TotalCount { get; init; }

    protected override void OnApply(ElinNetBase net)
    {
        // host -> client broadcast only
        if (net.IsHost) {
            return;
        }

        Play();
    }

    public void Play()
    {
        var color = PeerColorizer.GetColor(PlayerIndex);
        var player = NetSession.Instance.CurrentPlayers.Find(p => p.Index == PlayerIndex);
        var name = player?.User.Name ?? "emp_ui_unknown_player".Loc(PlayerIndex);
        var key = Ready ? "emp_ui_sleep_wish" : "emp_ui_sleep_cancel";
        WidgetPopText.Say(key.Loc(name.TagColor(color), ReadyCount, TotalCount));
    }
}