using ElinTogether.Helper;
using ElinTogether.Net;
using ElinTogether.Patches;
using MessagePack;

namespace ElinTogether.Models;

[MessagePackObject]
public class PartyMemberDelta : ElinDelta
{
    [Key(0)]
    public required RemoteCard Member { get; init; }

    [Key(1)]
    public int DestZoneUid { get; set; }

    [Key(2)]
    public Position? DestPos { get; set; }

    internal Chara? CaptureSource { get; init; }

    protected override void OnApply(ElinNetBase net)
    {
        if (net.IsHost) {
            ApplyHost();
        } else {
            ApplyClient();
        }
    }

    protected override bool OnRefresh()
    {
        if (CaptureSource != null) {
            DestZoneUid = CaptureSource.currentZone?.uid ?? 0;
            DestPos = CaptureSource.pos;
        }

        return true;
    }

    private void ApplyHost()
    {
        if (Member.Find() is not Chara { isDead: false, IsPlayer: false } chara ||
            chara.party is not { } party || party != pc.party) {
            return;
        }

        using var _ = Simulate();
        party.RemoveMember(chara);

        // _leaveParty
        if (chara.homeZone is { } home && home != game.activeZone) {
            chara.MoveZone(home);
        }
    }

    private void ApplyClient()
    {
        if (Member.Find() is not Chara { IsPlayer: false } chara) {
            return;
        }

        if (chara.party is { } party && party.members.Contains(chara)) {
            party.Stub_RemoveMember(chara);
        }

        if (DestZoneUid == 0 || chara.currentZone?.uid == DestZoneUid ||
            game.spatials.Find(DestZoneUid) is not { } dest) {
            return;
        }

        if (chara.IsInActiveMap) {
            _zone.RemoveCard(chara);
        }

        chara.currentZone = dest;
        if (DestPos is { } pos) {
            chara.pos.Set(pos.X, pos.Z);
        }
    }
}