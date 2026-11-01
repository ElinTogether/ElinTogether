using System.Linq;
using ElinTogether.Components;
using ElinTogether.LangMod;
using ElinTogether.Net;

namespace ElinTogether.Helper;

/// <summary>
///     The first minutes of a player in a world (user 2026-11-01): after its first join to that world with that
///     character, a small card with three steps, each a button opening its page (who is here, the teams, a land
///     deed). Shown once per world and character, remembered on this PC (EmpConfig.Client.FirstStepsSeen); it closes
///     with one click or by itself
/// </summary>
internal static class FirstSteps
{
    // after the join's 4 s cover fade, so the card is not under the black screen
    private const float ShowAfter = 6f;
    private const float CloseAfter = 40f;
    // the worlds remembered at most (oldest forgotten first)
    private const int Remembered = 50;

    /// <summary>
    ///     Called once the first map of a join is shown (ElinNetClient, first scene init of the session)
    /// </summary>
    internal static void AfterJoin()
    {
        if (NetSession.Instance.Transport is not ElinNetClient || EClass.pc is null) {
            return;
        }

        var key = Key();
        if (key is null) {
            return;
        }

        var seen = EmpConfig.Client.FirstStepsSeen.Value.Split(';').Where(s => s.Length > 0).ToList();
        if (seen.Contains(key)) {
            return;
        }

        seen.Add(key);
        EmpConfig.Client.FirstStepsSeen.Value = string.Join(";", seen.Skip(System.Math.Max(0, seen.Count - Remembered)));
        TweenUtil.Delay(ShowAfter, Show);
    }

    // the host's Steam id and this player's character in that world
    private static string? Key()
    {
        if (NetSession.Instance.CurrentPlayers.FirstOrDefault(p => p.Index == 0) is not { } host) {
            return null;
        }

        ulong hostId = host.User;
        return $"{hostId}:{EClass.pc.uid}";
    }

    private static void Show()
    {
        if (!EClass.core.IsGameStarted || NetSession.Instance.Transport is not ElinNetClient) {
            return;
        }

        var dialog = Dialog.Choice("emp_first_title", d => {
            d.AddButton("emp_first_players".lang(), () => LayerElinTogether.OpenPanelSesame(TabPlayers.TabName));
            if (NetSession.Instance.Rules.UseTeams) {
                d.AddButton("emp_first_team".lang(), () => LayerElinTogether.OpenPanelSesame("emp_tab_team"));
                d.AddButton("emp_first_land".lang(), () => LayerElinTogether.OpenPanelSesame("emp_tab_lands"));
            }
            d.AddButton("emp_first_close".lang());
        });
        TweenUtil.Delay(CloseAfter, () => {
            if (dialog != null) {
                dialog.Close();
            }
        });
    }
}
