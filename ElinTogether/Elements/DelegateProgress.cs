using System;

namespace ElinTogether.Elements;

internal class DelegateProgress : Progress_Custom
{
    internal required Type ActType { get; init; }

    internal static DelegateProgress Create(Type actType)
    {
        return new() {
            ActType = actType,
            status = Status.Running,
        };
    }

    internal static bool Represents(AIAct? act, Type actType)
    {
        return act is DelegateProgress d && d.ActType == actType;
    }
}