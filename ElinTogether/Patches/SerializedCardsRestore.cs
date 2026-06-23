using HarmonyLib;

namespace ElinTogether.Patches;

[HarmonyPatch]
[HarmonyPatch(typeof(SerializedCards), nameof(SerializedCards.Restore))]
internal class SerializedCardsRestore
{
    internal static bool IsHappening = false;

    [HarmonyPrefix]
    internal static void OnRestore()
    {
        IsHappening = true;
    }

    [HarmonyPostfix]
    internal static void OnRestoreEnd()
    {
        IsHappening = false;
    }
}