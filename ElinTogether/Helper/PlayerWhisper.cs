using System.Linq;
using ElinTogether.Models;
using ElinTogether.Net;
using ElinTogether.Patches;
using HarmonyLib;

namespace ElinTogether.Helper;

/// <summary>
///     "Talk" on another player's character: the game's chat box, for that player alone. The line goes to it wherever it
///     is and shows like the chat, in the pop text, never in the log (user, 2026-10-24); a host option, the host
///     passes lines on without showing them (llm-council 2026-10-24)
/// </summary>
[HarmonyPatch]
internal static class PlayerWhisper
{
    internal static bool Enabled => NetSession.Instance.Rules.PrivateChat;

    private static string NameOf(int uid, string? fallback)
    {
        var state = NetSession.Instance.CurrentPlayers.FirstOrDefault(p => p.CharaUid == uid);
        if (state is null) {
            // (a player on another map: the server's roster knows its name)
            var far = NetSession.Instance.ServerRoster.Find(p => p.CharaUid == uid)?.Name;
            return string.IsNullOrWhiteSpace(far) ? fallback ?? "?" : far!;
        }

        // (a player without a name of its own: its character's)
        return state.User.Name.IsEmpty(fallback ?? "?").TagColor(PeerColorizer.GetColor(state.Index));
    }

    /// <summary>
    ///     A connected player, on this map or on another one (the server's roster), a line can reach through the host
    /// </summary>
    private static bool Reachable(Chara other)
    {
        return other.IsPlayer || NetSession.Instance.ServerRoster.Exists(p => p.CharaUid == other.uid);
    }

    /// <summary>
    ///     The game's chat box (Dialog.InputName), the hint saying who the line is for
    /// </summary>
    internal static void Open(Chara other)
    {
        if (!Reachable(other)) {
            var offline = NameOf(other.uid, other.Name);
            ChatBubbleEvent.PopChat(offline, "emp_whisper_offline".lang(offline));
            return;
        }

        Open(other.uid, other.Name);
    }

    /// <summary>
    ///     The same for a player by its character uid, also one on another map (the Players tab, the server's roster)
    /// </summary>
    internal static void Open(int uid, string? charaName)
    {
        var name = NameOf(uid, charaName);

        // (Dialog.InputName for the chat, with the hint set before the window shows)
        var d = Layer.Create<Dialog>("DialogInput");
        d.inputType = Dialog.InputType.Chat;
        d.langHint = "emp_whisper_with".lang(name);
        d.input.field.characterLimit = 500;
        d.input.Text = "";
        d.onEnterInput = (cancel, text) => {
            if (!cancel && !text.IsEmpty()) {
                Send(uid, charaName, text);
            }
        };
        d.list.AddButton(null, Lang.Get("ok"), () => {
            d.onEnterInput(false, d.input.Text);
            d.Close();
        });
        d.list.AddButton(null, Lang.Get("cancel"), () => d.Close());
        EClass.ui.AddLayer(d);
    }

    internal static void Send(Chara other, string text)
    {
        if (!Reachable(other)) {
            var offline = NameOf(other.uid, other.Name);
            ChatBubbleEvent.PopChat(offline, "emp_whisper_offline".lang(offline));
            return;
        }

        Send(other.uid, other.Name, text);
    }

    /// <summary>
    ///     A line to a player by its character uid: the host passes it on wherever that player is (Relay)
    /// </summary>
    internal static void Send(int uid, string? charaName, string text)
    {
        var name = NameOf(uid, charaName);
        var delta = new WhisperDelta {
            FromUid = EClass.pc.uid,
            ToUid = uid,
            Text = text,
            // (away on a map of one's own the state's name is empty: the host refused "a line signed ", 2026-10-30)
            Name = EClass.pc.RemoteState?.User.Name is { Length: > 0 } steam ? steam : EClass.pc.Name,
        };
        switch (NetSession.Instance.Transport) {
            case ElinNetHost host:
                Relay(host, delta);
                break;
            case ElinNetClient up:
                // (straight to the host, also from a map of one's own)
                up.SendWhileAway(delta);
                break;
            default:
                return;
        }

        ChatBubbleEvent.PopChat("emp_whisper_to".lang(name), text);
        ChatLog.Add(ChatLog.Private, "emp_whisper_to".lang(name), text, true, uid);
    }

    internal static void Relay(ElinNetHost host, WhisperDelta delta)
    {
        // (a player away from the host's map included: it is still a peer of the host)
        var peers = host.PeerIds.Where(id => host.PlayerChara(id)?.uid == delta.ToUid).ToList();
        if (peers.Count == 0 || !host.SendDeltaTo(peers[0], delta)) {
            EmpLog.Debug("Private chat line for {Uid}: that player is not connected", delta.ToUid);
        }
    }

    internal static void Receive(WhisperDelta delta)
    {
        // an ignored player's line is dropped here, and it is told nothing (my personal list, see Social)
        if (PlayerSocial.IsIgnoredUid(delta.FromUid)) {
            return;
        }

        var name = NameOf(delta.FromUid, delta.Name);
        ChatBubbleEvent.PopChat("emp_whisper_from".lang(name), delta.Text);
        ChatLog.Add(ChatLog.Private, "emp_whisper_from".lang(name), delta.Text, uid: delta.FromUid);
    }

    /// <summary>
    ///     "Talk" in the menu on another player's character
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(ActPlan), nameof(ActPlan._Update))]
    internal static void OnUpdatePlan(ActPlan __instance)
    {
        // (runs at every frame the cursor is on the map: no copy of the list unless there is something to replace)
        if (!Enabled || !__instance.list.Exists(i => i.act is ActChat && i.tc is Chara { IsRemotePlayer: true })) {
            return;
        }

        foreach (var item in __instance.list.ToArray()) {
            if (item.act is not ActChat || item.tc is not Chara { IsRemotePlayer: true } other) {
                continue;
            }

            __instance.list.Remove(item);
            __instance.TrySetAct("emp_act_talk", () => {
                Open(other);
                return false;
            }, other, null, 2);
        }
    }

    /// <summary>
    ///     "Talk" in the window of a member of the base (LayerInteraction)
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(LayerInteraction), nameof(LayerInteraction.GetPage))]
    internal static void OnGetPage(IInspect o, LayerInteraction.Page __result)
    {
        if (!Enabled || o is not Chara { IsRemotePlayer: true } other) {
            return;
        }

        foreach (var item in __result.items.Where(i => i.text == "tTalk".lang())) {
            item.action = () => Open(other);
        }
    }
}
