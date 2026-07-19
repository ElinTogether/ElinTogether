using ElinTogether.Models;
using ElinTogether.Net;

namespace ElinTogether.Patches;

internal class NetProfileSynchronizationContext : SynchronizationContext
{
    private static RemoteCard? _heldMainHand;
    private static RemoteCard? _heldOffHand;

    internal static void Update()
    {
        var delta = CharaSwitchHeldDelta.Create();
        if (_heldMainHand == delta.HeldMainHand && _heldOffHand == delta.HeldOffHand) {
            return;
        }

        _heldMainHand = delta.HeldMainHand;
        _heldOffHand = delta.HeldOffHand;

        NetSession.Instance.Connection!.Delta.AddRemote(delta);
    }
}