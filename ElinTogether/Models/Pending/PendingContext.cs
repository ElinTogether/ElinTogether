using System;

namespace ElinTogether.Models;

internal static class PendingContext
{
    private static int _depth;

    internal static bool IsActive => _depth > 0;

    internal static ScopeExit Begin()
    {
        Enter();
        return new() {
            OnExit = Exit,
        };
    }

    internal static void Enter()
    {
        _depth++;
    }

    internal static void Exit()
    {
        _depth--;
    }

    internal static void Reset()
    {
        _depth = 0;
    }
}