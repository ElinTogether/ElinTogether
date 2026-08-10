namespace ElinTogether.Net;

public partial class ElinNetBase
{
    public enum NetHandshakePhase
    {
        AwaitingVersion,
        AwaitingIntegrity,
        Joined,
        Rejected,
    }
}