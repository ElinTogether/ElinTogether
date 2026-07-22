namespace ElinTogether.Elements;

internal class DelegateProgress : Progress_Custom
{
    internal static DelegateProgress Create(AIAct typeAct)
    {
        var d = new DelegateProgress {
            parent = typeAct,
            status = Status.Running,
        };
        typeAct.child = d;
        return d;
    }
}