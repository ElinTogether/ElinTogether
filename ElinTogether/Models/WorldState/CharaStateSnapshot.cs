using System.Collections.Generic;
using ElinTogether.API.SourceValidation;
using ElinTogether.Elements;
using ElinTogether.Helper;
using ElinTogether.Net;
using ElinTogether.Patches;
using MessagePack;

namespace ElinTogether.Models;

[MessagePackObject]
public class CharaStateSnapshot : EClass
{
    [Key(0)]
    public required RemoteCard Owner { get; init; }

    [Key(1)]
    public required Position Pos { get; init; }

    /// <summary>
    ///     0 inside a <see cref="WorldStateSnapshot" />: the zone of that snapshot (zone uids start at 1)
    /// </summary>
    [Key(2)]
    public required int CurrentZoneUid { get; set; }

    [Key(3)]
    public required int Hp { get; set; }

    // player provided
    [Key(4)]
    public PlayerCharaStateSnapshot? State { get; set; }

    [Key(5)]
    public required bool IsDead { get; init; }

    [Key(6)]
    public required Hostility Hostility { get; init; }

    [Key(7)]
    public required Hostility OriginalHostility { get; init; }

    [Key(8)]
    public required int UidMaster { get; init; }

    [Key(9)]
    public required MinionType MinionType { get; init; }

    /// <summary>
    ///     A companion's hunger: the host's game feeds and starves them, each guest's copy kept its own count (hunt 4,
    ///     2026-10-25). Null for anyone else, and from an older host
    /// </summary>
    [Key(10)]
    public int? Hunger { get; init; }

    public static CharaStateSnapshot Create(Chara chara)
    {
        return new() {
            Owner = chara,
            Pos = chara.pos,
            CurrentZoneUid = chara.currentZone.uid,
            Hp = chara.hp,
            IsDead = chara.isDead,
            Hostility = chara.hostility,
            OriginalHostility = chara.c_originalHostility,
            UidMaster = chara.c_uidMaster,
            MinionType = chara.c_minionType,
            Hunger = HungerOf(chara),
        };
    }

    /// <summary>
    ///     <see cref="Hunger" />: a companion's, null for anyone else
    /// </summary>
    internal static int? HungerOf(Chara chara)
    {
        return chara.IsPCParty && !chara.IsPC && !chara.IsRemotePlayer ? chara.hunger.value : null;
    }

    public static CharaStateSnapshot CreateSelf()
    {
        return new() {
            Owner = pc,
            Pos = pc.pos,
            CurrentZoneUid = pc.currentZone.uid,
            Hp = pc.hp,
            IsDead = pc.isDead,
            Hostility = pc.hostility,
            OriginalHostility = pc.c_originalHostility,
            UidMaster = pc.c_uidMaster,
            MinionType = pc.c_minionType,
            State = new() {
                LastAct = ActMappingValidator.Default.ActToIdMapping.GetValueOrDefault(pc.ai.GetType(), 0),
                LastReceivedTick = NetSession.Instance.Tick,
                Speed = pc.Stub_get_Speed(),
                Turbo = AM_Adv.turbo != 0f,
                Talking = LayerDrama.IsActive(),
            },
        };
    }

    /// <summary>
    ///     This only applies to client characters
    ///     We assume host is always right
    /// </summary>
    public void ApplyReconciliation(Chara? remoteChara = null)
    {
        var chara = remoteChara ?? Owner.Find() as Chara;
        if (chara is null) {
            return;
        }

        // this is received from host side
        if (remoteChara is null) {
            chara.hp = Hp;
            if (Hunger is { } hunger && !chara.IsPC) {
                chara.hunger.value = hunger;
            }
        }

        if (chara.IsPC) {
            return;
        }

        // hostility checks
        if (remoteChara is null && !chara.IsRemotePlayer) {
            chara.hostility = Hostility;
            chara.c_originalHostility = OriginalHostility;
            if (chara.c_uidMaster != UidMaster) {
                chara.c_uidMaster = UidMaster;
                chara.master = null;
                chara.FindMaster();
            }

            if (chara.c_minionType != MinionType) {
                chara.c_minionType = MinionType;
            }
        }

        // one more check for lingering death events
        if (!Pos.IsInActiveMapBounds) {
            return;
        }

        // fixes should only be applied to other remote charas
        if (remoteChara is null) {
            if (IsDead) {
                return;
            }

            if (chara.isDead) {
                if (!chara.pos.IsValid) {
                    chara.pos.Set(Pos.X, Pos.Z);
                }

                chara.Revive(Pos, true);
            }
        } else if (chara.isDead || IsDead) {
            return;
        }

        if (chara.currentZone?.uid != CurrentZoneUid ||
            (chara.currentZone?.map is { } map && !map.charas.Contains(chara))) {
            // chara hasn't been brought to the same map yet
            if (CurrentZoneUid == NetSession.Instance.CurrentZone?.uid) {
                var zone = NetSession.Instance.CurrentZone;
                if (zone.map?.charas.Find(c => c.uid == chara.uid) is { } inMap) {
                    if (inMap != chara) {
                        CardCache.Set(inMap);
                    }
                } else {
                    chara.pos.Set(Pos.X, Pos.Z);
                    zone.AddCard(chara, Pos);

                    // a character of the world known by the cache alone is gone with the next load of this map
                    if (remoteChara is null && chara.IsGlobal && game.cards.globalCharas.Find(chara.uid) is null) {
                        game.cards.globalCharas.Add(chara);
                    }
                }
            }

            return;
        }

        // somehow we are riding it, or it rides or carries someone: the ride moves it
        if (chara.host is not null || chara.ride is not null || chara.parasite is not null) {
            _mismatchSeen.Remove(chara.uid);
            return;
        }

        // only apply position fix if on the same map
        if (NetSession.Instance.CurrentZone?.map is null || chara.pos == Pos) {
            _mismatchSeen.Remove(chara.uid);
            return;
        }

        // up to 10 tiles: a move delta on its way, or a task its owner's game walks it through, catches up (not the
        // empty task every copy runs: it pardoned any gap up to 10 tiles for good, parity_ai a3 2026-10-23)
        var dist = chara.pos.Distance(Pos);
        if (dist <= 2 || (dist <= 10 && (CharaMoveDelta.HasRecentMove(chara) ||
                                         chara.ai is GoalRemote { child: { status: AIAct.Status.Running } and not NoGoal }))) {
            _mismatchSeen.Remove(chara.uid);
            return;
        }

        // seen once: a move delta dropped or deferred, a hitch. Only a second snapshot in a row moves it (a character
        // standing still on the host is only in the full snapshots, 1 s apart: within 2.2 s, see WorldStateSnapshot.Partial;
        // 4 s apart with the host box "Large crowds")
        var now = UnityEngine.Time.unscaledTime;
        var window = NetSession.Instance.Rules.LargeCrowds ? CrowdMismatchWindow : MismatchWindow;
        if (!_mismatchSeen.Remove(chara.uid, out var seen) || now - seen > window) {
            _mismatchSeen[chara.uid] = now;
            return;
        }

        EmpLog.Debug("Reconcile force move chara {Uid} from {@FromPos} to {@Pos}",
            chara.uid, chara.pos.Copy(), (Point)Pos);
        chara.Place(Pos);
    }

    private const float MismatchWindow = 2.2f;
    private const float CrowdMismatchWindow = 5.2f;

    // per character uid, when its tile last differed from the snapshot's by more than the snapshot allows
    private static readonly Dictionary<int, float> _mismatchSeen = [];
}