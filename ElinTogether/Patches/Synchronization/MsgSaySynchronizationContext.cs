using System.Collections.Generic;
using System.Reflection;
using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

[HarmonyPatch]
internal class MsgSaySynchronizationContext
{
    internal static IEnumerable<MethodInfo> TargetMethods()
    {
        return [
            AccessTools.Method(typeof(Card), nameof(Card.TalkRaw)),
            AccessTools.Method(typeof(Chara), nameof(Chara.TalkTopic)),
        ];
    }

    [HarmonyPrefix]
    internal static bool OnBeforeTalk(out int __state)
    {
        __state = EClass.game.log.currentLogIndex;
        return NetSession.Instance.IsHost;
    }

    [HarmonyPostfix]
    internal static void OnAfterTalk(int __state)
    {
        if (NetSession.Instance.Connection is not ElinNetHost host) {
            return;
        }

        if (__state == EClass.game.log.currentLogIndex) {
            return;
        }

        var text = EClass.game.log.dict[EClass.game.log.currentLogIndex - 1].text;
        var color = MsgBlock.lastBlock.txt.color;
        host.Delta.AddRemote(new MsgSayDelta {
            Text = text,
            R = color.r,
            G = color.g,
            B = color.b,
            A = color.a,
        });
    }
}