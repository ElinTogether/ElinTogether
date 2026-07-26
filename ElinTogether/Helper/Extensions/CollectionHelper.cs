using System;
using System.Collections.Generic;

namespace ElinTogether.Helper.Extensions;

internal static class CollectionHelper
{
    extension(ThingContainer things)
    {
        internal IEnumerable<Thing> Flatten()
        {
            foreach (var t1 in things) {
                yield return t1;

                foreach (var t2 in t1.things.Flatten()) {
                    yield return t2;
                }
            }
        }
    }

    extension<T>(IEnumerable<T> collection)
    {
        internal void ForEach(Action<T> action)
        {
            foreach (var item in collection) {
                action(item);
            }
        }
    }
}