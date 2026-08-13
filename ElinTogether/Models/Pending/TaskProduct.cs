using ElinTogether.Patches;

namespace ElinTogether.Models;

internal static class TaskProduct
{
    internal static bool Publish(string slot, Thing? product)
    {
        if (!CharaProgressCompleteEvent.ShouldPack(false)) {
            return false;
        }

        CharaProgressCompleteEvent.Pack(new ThingDelta {
            Thing = product,
            Slot = slot,
        });

        return true;
    }

    internal static bool TryClaim(string slot, out Thing? product)
    {
        product = null;
        if (!CharaProgressCompleteDelta.IsReplaying) {
            return false;
        }

        if (Find(slot) is { } delta) {
            delta.Valid = true;
            product = delta.Thing?.Find() as Thing;
        }

        return true;
    }

    internal static bool WasProduced(string slot)
    {
        return Find(slot)?.Thing is not null;
    }

    private static ThingDelta? Find(string slot)
    {
        if (CharaProgressCompleteDelta.Current is not { } current) {
            return null;
        }

        foreach (var delta in current.DeltaList) {
            if (delta is ThingDelta { Valid: false } product && product.Slot == slot) {
                return product;
            }
        }

        return null;
    }
}