using ElinTogether.Net;
using MessagePack;

namespace ElinTogether.Models;

// Card
[Union(100, typeof(CardGenDelta))]
[Union(101, typeof(CardDamageHpDelta))]
[Union(102, typeof(CardPlacedDelta))]
[Union(103, typeof(CardModNumDelta))]
[Union(104, typeof(CardAddThingDelta))]
[Union(105, typeof(CardRemoveThingDelta))]
[Union(106, typeof(CardOnUseDelta))]
[Union(107, typeof(CardTryStackToDelta))]
[Union(109, typeof(CardSetDirDelta))]
[Union(110, typeof(CardUidRebindDelta))]
[Union(111, typeof(CardToggleDelta))]
[Union(112, typeof(CardChargeDelta))]
[Union(113, typeof(CardIdentifyDelta))]
[Union(114, typeof(CardShrineUsedDelta))]
// Chara
[Union(200, typeof(CharaMoveDelta))]
[Union(201, typeof(CharaTickDelta))]
[Union(202, typeof(CharaMakeAllyDelta))]
[Union(203, typeof(CharaPickThingDelta))]
[Union(204, typeof(CharaDieDelta))]
[Union(205, typeof(CharaActPerformDelta))]
[Union(206, typeof(CharaAddConditionDelta))]
[Union(207, typeof(CharaReviveDelta))]
[Union(208, typeof(CharaTickConditionDelta))]
[Union(209, typeof(CharaTaskDelta))]
[Union(210, typeof(CharaBuildDelta))]
[Union(211, typeof(CharaProgressBeginDelta))]
[Union(212, typeof(CharaProgressCompleteDelta))]
[Union(213, typeof(CharaTaskCancelDelta))]
[Union(214, typeof(CharaHitFishDelta))]
[Union(215, typeof(CharaGiveGiftDelta))]
[Union(216, typeof(CharaSwitchHeldDelta))]
[Union(217, typeof(CharaRemoveFromGameDelta))]
[Union(218, typeof(CharaSleepDelta))]
[Union(219, typeof(CharaStaminaDelta))]
[Union(220, typeof(CharaMakeAllyRequestDelta))]
[Union(221, typeof(CharaEquipDelta))]
// Thing
[Union(300, typeof(ThingDelta))]
[Union(301, typeof(ThingRequest))]
[Union(302, typeof(CardModCurrencyDelta))]
// Zone
[Union(400, typeof(SpatialGenDelta))]
[Union(401, typeof(ZoneAddCardDelta))]
// World
[Union(500, typeof(GameDelta))]
[Union(501, typeof(WorldDateAdvanceDelta))]
[Union(502, typeof(ShippingResultDelta))]
[Union(503, typeof(BranchResourceModDelta))]
// Misc
[Union(600, typeof(OnBarterDelta))]
[Union(601, typeof(CardRendererTalkDelta))]
[Union(602, typeof(MsgSayDelta))]
[Union(603, typeof(EnemyVisibilityDelta))]
[Union(604, typeof(PingPointDelta))]
[Union(605, typeof(CombatPhaseDelta))]
[Union(606, typeof(AddRecipeDelta))]
[Union(607, typeof(CombatReadyDelta))]
[Union(608, typeof(SleepRequestDelta))]
[Union(609, typeof(SleepReadyDelta))]
[Union(610, typeof(SleepStartDelta))]
[Union(611, typeof(SleepCancelDelta))]
// Inv
[Union(700, typeof(InvOwnerOnProcessDelta))]
[Union(701, typeof(InvRerollDelta))]
[Union(702, typeof(InvSaveDataDelta))]
[Union(703, typeof(InvPlaceAbilityDelta))]
// Quest
[Union(800, typeof(QuestCreateDelta))]
[Union(801, typeof(QuestSetClientDelta))]
[Union(802, typeof(QuestStartDelta))]
[Union(803, typeof(QuestAcceptDelta))]
[Union(804, typeof(QuestCompleteDelta))]
[Union(805, typeof(QuestUpdateDelta))]
[Union(806, typeof(QuestChangePhaseDelta))]
// Act
[Union(900, typeof(ActThrowDelta))]
// Element
[Union(1000, typeof(ElementChangeDelta))]
public abstract class ElinDelta : EClass
{
    private static int _applyDepth;

    internal virtual OverrideOrder Order { get; } = OverrideOrder.Stack;

    internal virtual bool RequiresGameStarted { get; } = true;

    public static bool IsApplying => _applyDepth > 0;

    // remote applying, ThingRequest is local sim which does not cunt
    public static bool IsRemoteStateLanding => IsApplying && !ThingRequest.IsReplayingIntent;

    internal int OriginPeer { get; set; }

    internal int DeferCount { get; set; }

    protected virtual void OnApply(ElinNetBase net)
    {
    }

    protected virtual bool OnRefresh()
    {
        return true;
    }

    public void Apply(ElinNetBase net)
    {
        _applyDepth++;
        try {
            OnApply(net);
        } finally {
            _applyDepth--;
        }
    }

    internal static ScopeExit Simulate(bool active = true)
    {
        var depth = _applyDepth;
        if (active) {
            _applyDepth = 0;
        }

        return new() {
            OnExit = () => {
                if (active) {
                    _applyDepth = depth;
                }
            },
        };
    }

    public bool Refresh()
    {
        return OnRefresh();
    }

    // for harmony patches
    internal readonly struct PatchScope
    {
        private enum Kind : byte
        {
            None,
            Simulate,
            Pending,
        }

        private readonly Kind _kind;
        private readonly int _restore;

        private PatchScope(Kind kind, int restore)
        {
            _kind = kind;
            _restore = restore;
        }

        internal bool IsActive => _kind != Kind.None;

        internal static PatchScope Simulate(bool active = true)
        {
            if (!active) {
                return default;
            }

            var previous = _applyDepth;
            _applyDepth = 0;
            return new(Kind.Simulate, previous);
        }

        internal static PatchScope Pending(bool active = true)
        {
            if (!active) {
                return default;
            }

            PendingContext.Enter();
            return new(Kind.Pending, 0);
        }

        internal void Exit()
        {
            switch (_kind) {
                case Kind.Simulate:
                    _applyDepth = _restore;
                    break;
                case Kind.Pending:
                    PendingContext.Exit();
                    break;
            }
        }
    }

    internal enum OverrideOrder
    {
        Stack,
        Last,
        First,
    }
}