using System;
using System.Collections.Generic;
using System.Reflection;
using ElinTogether.Components;
using ElinTogether.Helper;
using ElinTogether.Models;
using ElinTogether.Net;
using HarmonyLib;
using HeathenEngineering.SteamworksIntegration;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;
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
            // (a player's own lines make no bubble for the others when the host's rule says so; the same for every
            // player, the host's too)
            if (chara.IsPC && !NetSession.Instance.Rules.ChatBubbles) {
                return true;
            }

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
        // (the pop text draws rich text: a name keeps only its colour, the words none of their tags)
        who = ChatMarkup.SafeName(who);
        var plain = ChatMarkup.Escape(ChatMarkup.Plain(text));
        var sprite = ChatMarkup.AloneEmote(text) is { } emo ? ChatMarkup.EmoteSprite(emo.ToString()) : null;
        WidgetPopText.Say(plain.Length > 0 ? $"{who}: {plain}" : who, sprite: sprite ?? UIHelper.FindSprite("emo2_hint"));
        Remember(text);
    }

    private static void Remember(string text)
    {
        Shown.Enqueue(text);
        while (Shown.Count > 20) {
            Shown.Dequeue();
        }
    }

    // the pop text lines with a picture alive at once, at most (each holds its own small texture)
    private const int MaxPicturePops = 4;

    /// <summary>
    ///     A chat picture over the game (user 2026-11-03): the pop text's line with the picture itself in the place of
    ///     its icon, at a readable size (whole steps, nearest filter); a click on it opens the larger view. The line owns
    ///     <paramref name="picture" /> and frees it when it fades out; without the pop text, or with too many pictures
    ///     shown already, only the words
    /// </summary>
    internal static void PopPicture(string who, string text, Texture2D? picture, Action? open)
    {
        if (picture == null || WidgetPopText.Instance == null || WidgetPopText.Instance.pop == null ||
            PopPictureOwner.Alive >= MaxPicturePops) {
            if (picture != null) {
                Object.Destroy(picture);
            }

            PopChat(who, text);
            return;
        }

        // (as WidgetPopText.Say makes its line, the picture's size set before the line takes its place)
        var pop = WidgetPopText.Instance.pop;
        var sprite = Sprite.Create(picture, new(0f, 0f, picture.width, picture.height), new(0.5f, 0.5f), 100f);
        var item = pop.Get<PopItemText>("PopNotification");
        item.important = false;
        var plain = ChatMarkup.Escape(ChatMarkup.Plain(text));
        item.SetText(plain.Length > 0 ? $"{ChatMarkup.SafeName(who)}: {plain}" : ChatMarkup.SafeName(who), sprite);
        item.gameObject.AddComponent<PopPictureOwner>().Own(sprite, picture);
        if (item.image != null) {
            var scale = ChatPictureView.Fit(picture, PopPictureSide, 4f);
            var size = new Vector2(picture.width * scale, picture.height * scale);
            item.image.SetActive(true);
            item.image.preserveAspect = true;
            item.image.rectTransform.sizeDelta = size;
            var element = item.image.GetComponent<LayoutElement>();
            if (element == null) {
                element = item.image.gameObject.AddComponent<LayoutElement>();
            }

            element.minWidth = element.preferredWidth = size.x;
            element.minHeight = element.preferredHeight = size.y;
            if (open != null) {
                ChatPictureView.Clickable(item.image, open);
            }
        }

        pop.Pop(item);
        Remember(text);
    }

    // the picture's longer side in the pop text
    private const float PopPictureSide = 80f;

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