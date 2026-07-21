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
// Thing
[Union(300, typeof(ThingDelta))]
[Union(301, typeof(ThingRequest))]
// Zone
[Union(400, typeof(SpatialGenDelta))]
[Union(401, typeof(ZoneAddCardDelta))]
// World
[Union(500, typeof(GameDelta))]
// Misc
[Union(600, typeof(OnBarterDelta))]
[Union(601, typeof(CardRendererTalkDelta))]
[Union(602, typeof(MsgSayDelta))]
[Union(603, typeof(EnemyVisibilityDelta))]
[Union(604, typeof(PingPointDelta))]
// Inv
[Union(700, typeof(InvOwnerOnProcessDelta))]
[Union(701, typeof(InvRerollDelta))]
[Union(702, typeof(InvSaveDataDelta))]
// Quest
[Union(800, typeof(QuestCreateDelta))]
[Union(801, typeof(QuestSetClientDelta))]
[Union(802, typeof(QuestStartDelta))]
[Union(803, typeof(QuestCreateInstanceZoneDelta))]
[Union(804, typeof(QuestCompleteDelta))]
[Union(805, typeof(QuestUpdateDelta))]
[Union(806, typeof(QuestChangePhaseDelta))]
// Act
[Union(900, typeof(ActThrowDelta))]
// Element
[Union(1000, typeof(ElementChangeDelta))]
public abstract class ElinDelta : EClass
{
    [IgnoreMember]
    internal virtual OverrideOrder Order { get; } = OverrideOrder.Stack;

    public static bool IsApplying { get; private set; }

    protected abstract void OnApply(ElinNetBase net);

    public void Apply(ElinNetBase net)
    {
        IsApplying = true;
        OnApply(net);
        IsApplying = false;
    }

    internal enum OverrideOrder
    {
        Stack,
        Last,
        First,
    }
}