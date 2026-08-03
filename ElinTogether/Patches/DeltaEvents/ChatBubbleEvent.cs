using System.Collections.Generic;
using System.Reflection;
using ElinTogether.Helper;
using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;

namespace ElinTogether.Patches;

[HarmonyPatch]
internal class ChatBubbleEvent
{
    [HarmonyPrefix]
    [HarmonyPatch(typeof(CardRenderer), nameof(CardRenderer.Say))]
    internal static bool OnCardTalkRaw(CardRenderer __instance, string text, float duration)
    {
        if (NetSession.Instance.Connection is not { } connection) {
            return true;
        }

        if ((TalkSpeakerContext.Speaker ?? __instance.owner) is not Chara chara) {
            return true;
        }

        if ((connection.IsHost && !chara.IsRemotePlayer) || chara.IsPC) {
            connection.Delta.AddRemote(new CardRendererTalkDelta {
                Card = chara,
                Text = text,
                Duration = duration,
            });
            if (chara.RemoteState is { } state ) {
                WidgetPopText.Say($"{state.User.Name.TagColor(PeerColorizer.GetColor(state.Index))}: {text}",
                    sprite: UIHelper.FindSprite("emo2_hint"));
            }
            return true;
        }

        return ElinDelta.IsApplying;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(AM_Adv), nameof(AM_Adv.OnEnterChat))]
    internal static void OnEnterChat()
    {
        if (NetSession.Instance.Connection is not { } connection) {
            return;
        }

        var text = EClass.game.log.dict[EClass.game.log.currentLogIndex - 1].text;
        var color = MsgBlock.lastBlock.txt.color;
        connection.Delta.AddRemote(new MsgSayDelta {
            Text = text,
            R = color.r,
            G = color.g,
            B = color.b,
            A = color.a,
        });
    }

    [HarmonyPatch]
    internal static class TalkSpeakerContext
    {
        internal static Card? Speaker { get; private set; }

        internal static ScopeExit Push(Card? speaker)
        {
            var previous = Speaker;
            Speaker = speaker;
            return new() {
                OnExit = () => Speaker = previous,
            };
        }

        internal static IEnumerable<MethodInfo> TargetMethods()
        {
            return [
                AccessTools.Method(typeof(Card), nameof(Card.SayRaw)),
                AccessTools.Method(typeof(Card), nameof(Card.TalkRaw)),
                AccessTools.Method(typeof(Chara), nameof(Chara.TalkTopic)),
            ];
        }

        [HarmonyPrefix]
        internal static void OnBeforeTalk(Card __instance, out Card? __state)
        {
            __state = Speaker;
            Speaker = __instance;
        }

        [HarmonyFinalizer]
        internal static void OnAfterTalk(Card? __state)
        {
            Speaker = __state;
        }
    }
}