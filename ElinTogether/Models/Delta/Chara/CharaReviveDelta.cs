using ElinTogether.Helper;
using ElinTogether.Net;
using MessagePack;

namespace ElinTogether.Models;

[MessagePackObject]
public class CharaReviveDelta : ElinDelta
{
    [Key(0)]
    public required RemoteCard Owner { get; init; }

    [Key(1)]
    public required string? LastWords { get; init; }

    [Key(2)]
    public Position? Pos { get; set; }

    protected override void OnApply(ElinNetBase net)
    {
        if (Owner.Find() is not Chara chara) {
            return;
        }

        if (net is ElinNetHost host) {
            // duplicate
            if (!chara.isDead) {
                return;
            }

            Point point;
            if (Pos is { IsInActiveMapBounds: true } requested) {
                point = requested;
            } else if (chara.IsInActiveMap) {
                point = chara.pos.Copy();
            } else {
                point = pc.pos.GetNearestPoint() ?? pc.pos.Copy();
            }

            if (!chara.pos.IsValid) {
                chara.pos.Set(point.x, point.z);
            }

            chara.Revive(point, true);
            chara.MakeGrave(LastWords);

            Pos = point;
            host.Delta.AddRemote(this);
        } else {
            // duplicate
            if (chara.isDead) {
                if (Pos is { IsInActiveMapBounds: true } pos) {
                    if (!chara.pos.IsValid) {
                        chara.pos.Set(pos.X, pos.Z);
                    }

                    chara.Revive(pos, true);
                } else {
                    chara.Revive(msg: true);
                }
            }

            // recursion
            if (chara.IsPC) {
                player.deathDialog = false;
            }
        }

        EmpLog.Debug("Revive chara {Uid} at {@Pos}",
            chara.uid, Pos);

        // add back to party
        if (chara is { c_wasInPcParty: true, IsPCParty: false }) {
            pc.party.AddMemeber(chara);
        }
    }
}