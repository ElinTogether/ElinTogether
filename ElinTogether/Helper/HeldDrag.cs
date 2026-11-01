using System;

namespace ElinTogether.Helper;

/// <summary>
///     A thing on the mouse (a stack split off, a drag in progress) is in no bag while it is held: with the mod the
///     piece is taken out of its container (ThingRequest on a guest, Split on the host, InvSplitThingEvent). A world
///     copy, a character handed to another game or a save made at that moment does not have it, and the game drops
///     the drag when it is torn down (UI.OnKillGame): the piece was gone (GitHub issue 48, gold split then held while
///     the guest left the host's map). Before any of those, the drag is cancelled the game's own way: it goes back
///     where it was taken from (InvOwner.OnCancelDrag), as a gesture of this game that reaches the others
/// </summary>
internal static class HeldDrag
{
    internal static void PutBack(string why)
    {
        try {
            if (EClass.core?.IsGameStarted != true || EClass.ui?.currentDrag is not DragItemCard drag ||
                drag.from?.thing is not { isDestroyed: false } thing || thing.parent is not null) {
                return;
            }

            EmpLog.Information("Thing {Uid} ({CardId} x{Num}) was held out of any bag ({Why}): put back",
                thing.uid, thing.id, thing.Num, why);

            try {
                EClass.ui.EndDrag(true);
            } catch (Exception ex) {
                // (the window it came from already gone)
                EmpLog.Warning(ex, "Cancelling the drag of {Uid} failed ({Why})", thing.uid, why);
                EClass.ui.currentDrag = null;
                EClass.ui.hud?.SetDragImage(null);
            }

            if (thing.parent is null && !thing.isDestroyed && EClass.pc is { } pc && !IsNpcProperty(drag)) {
                pc.Pick(thing, false);
            }

            // the game refuses to cancel a drag out of a merchant's chest: not picked here (a theft), the host
            // puts what it handed out back where it was (ThingRequest.ReturnDanglingOf)
            if (thing.parent is null) {
                EmpLog.Warning("Thing {Uid} is still held after the cancel ({Why})", thing.uid, why);
            }
        } catch (Exception ex) {
            EmpLog.Warning(ex, "A held thing could not be put back ({Why})", why);
        }
    }

    private static bool IsNpcProperty(DragItemCard drag)
    {
        try {
            return drag.from.invOwner?.Container is not { isNPCProperty: false };
        } catch (Exception) {
            return true;
        }
    }
}
