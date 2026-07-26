using ElinTogether.Net;
using ElinTogether.Patches;

namespace ElinTogether.Models;

internal static class TaskProduct
{
    internal static bool IsReplaying => CharaProgressCompleteDelta.Current is not null;

    internal static bool Publish(string slot, Thing? product)
    {
        if (!CharaProgressCompleteEvent.IsHappening || NetSession.Instance.IsClient) {
            return false;
        }

        CharaProgressCompleteEvent.DeltaList.Add(new ThingDelta {
            Thing = product,
            Slot = slot,
        });

        return true;
    }

    internal static bool TryClaim(string slot, out Thing? product)
    {
        product = null;
        if (!IsReplaying) {
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
