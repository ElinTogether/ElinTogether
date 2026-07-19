namespace ElinTogether.Patches;

internal class SynchronizationContext : EClass
{
    internal static float GameDelta { get; set; }
    internal static bool CanSendDelta { get; set; }
    internal static int RefSpeed { get; set; }

    internal static void AllowDeltaSending()
    {
        CanSendDelta = true;
    }
}