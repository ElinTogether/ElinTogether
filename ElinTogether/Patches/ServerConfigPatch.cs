using HarmonyLib;

namespace ElinTogether.Patches;

/// <summary>
///     A hidden server (HiddenServer) is a second Elin on the player's machine, with the same folders: it never writes
///     the player's config.txt (language, display, widgets), whatever it would change in its own memory
/// </summary>
[HarmonyPatch(typeof(CoreConfig), nameof(CoreConfig.Save))]
[AlwaysPatched]
internal static class ServerConfigPatch
{
    [HarmonyPrefix]
    internal static bool SkipOnServer()
    {
        return !EmpServer.Requested;
    }
}
