using System;

namespace ElinTogether;

/// <summary>
///     A patch class that is in place from the start of the game and never taken away, in every build (GitHub issue
///     66 and its audit, 2026-11-02). The other classes of a release build are only there while a session is open
///     (EmpMod.PatchAll from ElinNetBase.Awake, UnpatchSelf from its OnDestroy), while a debug build, the bench's, has
///     them all from the start: what had to work with no session open (the title screen, a shared world played alone
///     before or after its session, the load of a world, its save) worked on the bench and not for the players. <br />
///     For what acts with no session: the load and the save of a world, the title screen, a server before its
///     session opens. Its own code says when it does nothing. A class nested in a marked class is marked too
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
internal sealed class AlwaysPatchedAttribute : Attribute
{
    internal static bool On(Type type)
    {
        for (var t = type; t is not null; t = t.DeclaringType) {
            if (t.IsDefined(typeof(AlwaysPatchedAttribute), false)) {
                return true;
            }
        }

        return false;
    }
}
