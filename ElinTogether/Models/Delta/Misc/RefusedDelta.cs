using ElinTogether.Helper;
using ElinTogether.LangMod;
using ElinTogether.Net;
using MessagePack;

namespace ElinTogether.Models;

/// <summary>
///     From the host to one player: an action of that player was refused (ElinDelta.HostPermits under
///     TrustEnforced), so the snap-back it sees comes with a reason (user 2026-11-01). A short toast, at most one
///     every few seconds: "That belongs to {owner}." when the thing or character is another player's, else "Not
///     allowed here."
/// </summary>
[MessagePackObject]
public class RefusedDelta : ElinDelta
{
    // the shortest wait between two toasts, on the host per player and on the player's own game
    internal const float Interval = 3f;

    private static float _shownAt = -Interval;

    /// <summary>
    ///     The name of the player the thing or character belongs to, null when it is nobody's (a merchant's goods, a
    ///     chest out of reach)
    /// </summary>
    [Key(0)]
    public string? Owner { get; init; }

    internal override string? OnHostPermit(ElinNetHost host, Chara? sender)
    {
        return "not a request";
    }

    protected override void OnApply(ElinNetBase net)
    {
        if (net is not ElinNetClient) {
            return;
        }

        var now = UnityEngine.Time.unscaledTime;
        if (now - _shownAt < Interval) {
            return;
        }

        _shownAt = now;
        var text = string.IsNullOrEmpty(Owner) ? "emp_refused_here".lang() : "emp_refused_owner".Loc(Owner!);
        // (a player who hid the pop-text widget gets it in the message log: WidgetPopText.Say does nothing then)
        if (WidgetPopText.Instance) {
            WidgetPopText.Say(text);
        } else {
            Msg.Say(text);
        }
    }
}
