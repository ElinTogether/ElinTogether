using ElinTogether.LangMod;
using ElinTogether.Models;

namespace ElinTogether.Net;

internal partial class ElinNetClient
{
    private void RegisterWorldCopy()
    {
        Router.RegisterHandler<WorldCopyManifest>(OnWorldCopyOffer);
        Router.RegisterHandler<WorldCopyPiece>(OnWorldCopyPiece);
        Router.RegisterHandler<HostLeaving>(OnHostLeaving);
    }

    /// <summary>
    ///     Net event: the host closes its game. The guest whose turn it is opens the world from its copy; for the
    ///     others, and when nothing can be opened, the link closes right after this as it always did
    /// </summary>
    private void OnHostLeaving(HostLeaving leaving)
    {
        // the host said it leaves: its game may come back (a world reloaded, a restart), joined again by itself as
        // after a dropped link, see ElinNetClient.UpdateHostLeft
        if (!IsZoneSession) {
            HostLeftAt = UnityEngine.Time.realtimeSinceStartup;
        }

        if (IsZoneSession || !Session.Rules.AllowTakeover) {
            return;
        }

        WorldHandover.HostLeft(leaving.Guests, leaving.Next);

        // a peer-hosted world kept with the world's store: the keeper handed the lease to the one it named, who opens
        // the world now; the others follow it (NetReconnect, as below)
        if (Helper.PeerKeeper.Enabled && leaving.Next != 0) {
            var me = Session.Self is { } self ? (ulong)self.User : 0UL;
            if (leaving.Next == me) {
                Helper.PeerKeeper.NamedNext();
            } else {
                EmpLog.Information("The keeper leaves, the world goes to {Next}", leaving.Next);
                EmpPop.Information("emp_peer_moved".Loc(Helper.PeerKeeper.Name(leaving.Next)));
            }

            return;
        }

        if (!WorldHandover.IsMyTurn(0f)) {
            EmpLog.Information("The host leaves, the world is not ours to take over: {State}", WorldHandover.Describe());
            return;
        }

        var why = WorldTakeover.Begin(true);
        if (why.Length > 0) {
            EmpLog.Warning("The host leaves and the world is not taken over: {Why}", why);
            return;
        }

        EmpPop.Information("emp_takeover_begin".lang());
    }

    /// <summary>
    ///     Net event: the host saved, this is what its save holds
    /// </summary>
    private void OnWorldCopyOffer(WorldCopyManifest offer)
    {
        // the host of a zone session is another guest: the world is not its to hand out
        if (!IsZoneSession) {
            WorldCopyReceiver.Offer(offer);
        }
    }

    private void OnWorldCopyPiece(WorldCopyPiece piece)
    {
        if (!IsZoneSession) {
            WorldCopyReceiver.Piece(piece);
        }
    }
}
