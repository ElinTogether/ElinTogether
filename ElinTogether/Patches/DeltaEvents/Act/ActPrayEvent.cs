using ElinTogether.Helper;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

[HarmonyPatch(typeof(ActPray), nameof(ActPray.TryPray))]
internal static class ActPrayEvent
{
    [HarmonyPrefix]
    internal static bool OnTryPray(Chara c, ref bool __result)
    {
        if (NetSession.Instance.Connection is null || !c.IsRemotePlayer) {
            return true;
        }

        __result = true;

        if (!c.HasCondition<ConWrath>() && c.things.Find<TraitPunishBall>() is { } ball) {
            ball.Destroy();
            c.PlaySound("pray");
            c.PlayEffect("revive");
            c.Say("piety2", c);
            return false;
        }

        var today = EClass.world.date.GetRawDay();
        var profile = c.NetProfile;
        if (profile.LastPrayedDay == today) {
            return false;
        }
        profile.LastPrayedDay = today;

        c.Say("pray2", c, c.faith.Name);
        c.PlaySound("pray");
        c.PlayEffect("revive");
        c.HealHP(999999L);
        c.mana.Mod(999999);
        c.Cure(CureType.Prayer, 999999);
        c.RemoveCondition<ConDeathSentense>();

        return false;
    }
}