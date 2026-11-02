using System.Collections.Generic;
using System.Reflection;
using ElinTogether.Helper;
using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;
using HeathenEngineering.SteamworksIntegration;
using Random = UnityEngine.Random;

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
            if (chara.RemoteState is { } state) {
                PopChat(state.User.Name.TagColor(PeerColorizer.GetColor(state.Index)), text);
            }
            return true;
        }

        return ElinDelta.IsApplying;
    }

    /// <summary>
    ///     Last player lines shown (the bench reads them)
    /// </summary>
    internal static readonly Queue<string> Shown = new();

    /// <summary>
    ///     A player's line, the way the game shows a player speaking: "name: text" in the pop text
    /// </summary>
    internal static void PopChat(string who, string text)
    {
        // (the words only: a line of one emote shows that emote's icon, ChatMarkup)
        var plain = ChatMarkup.Plain(text);
        var sprite = ChatMarkup.AloneEmote(text) is { } emo ? ChatMarkup.EmoteSprite(emo.ToString()) : null;
        WidgetPopText.Say(plain.Length > 0 ? $"{who}: {plain}" : who, sprite: sprite ?? UIHelper.FindSprite("emo2_hint"));
        Shown.Enqueue(text);
        while (Shown.Count > 20) {
            Shown.Dequeue();
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(AM_Adv), nameof(AM_Adv.OnEnterChat))]
    internal static void OnEnterChat(string text)
    {
        // an away player still chats with everyone, straight through the transport
        var away = NetSession.Instance.IsAway ? NetSession.Instance.Transport as ElinNetClient : null;
        var connection = NetSession.Instance.Connection;
        if (connection is null && away is null) {
            return;
        }

        // muted (admin tools): its line stays in its own game, which tells it why
        // (as the host knows this player: a test instance on the bench plays under an identity of its own)
        if (ModerationDelta.IsMuted(NetSession.Instance.Self?.User ?? UserData.Me)) {
            Msg.Say("emp_ui_muted".lang());
            return;
        }

        var delta = new MsgSayDelta {
            Text = text,
            R = 1,
            G = 1,
            B = 1,
            A = 1,
            Chat = true,
            ChatId = Random.Range(1, int.MaxValue),
            SpeakerUid = EClass.pc.uid,
            Name = EClass.pc.RemoteState?.User.Name is { Length: > 0 } me ? me : EClass.pc.Name,
        };
        MsgSayDelta.Remember(delta.ChatId);
        ChatLog.Add(ChatLog.All, delta.Name ?? "?", text, true, EClass.pc.uid);

        if (!NetSession.Instance.Rules.GlobalChat) {
            // map-local: the players of the map we are on (an away player without a map session has none)
            connection?.Delta.AddRemote(delta);
            return;
        }

        // global chat: to every player, wherever they are
        if (away is not null) {
            away.SendChatWhileAway(delta);
        } else if (NetSession.Instance.Transport is ElinNetHost main) {
            main.SendDeltaToAllExcept(int.MinValue, delta);
        } else {
            connection!.Delta.AddRemote(delta);
        }
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