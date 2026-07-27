using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using ElinTogether.Models.AI;

namespace ElinTogether.Models;

// TODO: ask Redgeoiz to rework this
internal static class RemoteCraft
{
    private static readonly ConditionalWeakTable<AI_UseCrafter, AIUseCrafterArgs> _selections = new();

    internal static Chara? ProductReceiver { get; set; }

    internal static void Attach(AI_UseCrafter act, AIUseCrafterArgs args)
    {
        _selections.Add(act, args);
    }

    internal static bool TryGet(AI_UseCrafter act, [NotNullWhen(true)] out AIUseCrafterArgs? args)
    {
        return _selections.TryGetValue(act, out args);
    }

    internal static bool IsHostRun(AIAct? act)
    {
        return act is AI_UseCrafter crafter && _selections.TryGetValue(crafter, out _);
    }
}