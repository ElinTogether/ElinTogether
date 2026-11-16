using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

/// <summary>
///     GitHub issue 35 ("can't delete a save to make a new character"): connected to a world, the game's own load window
///     lists this machine's saves, not the characters of that world. Those are picked, made or deleted at the join
///     (ElinNetClientPlayer's character list): the window says so
/// </summary>
[HarmonyPatch(typeof(LayerLoadGame), nameof(LayerLoadGame.Init))]
internal static class LoadGameHintPatch
{
    [HarmonyPostfix]
    internal static void OnInit()
    {
        if (NetSession.Instance.Transport is ElinNetClient) {
            EmpPop.Information("emp_load_world_characters".lang());
        }
    }
}
