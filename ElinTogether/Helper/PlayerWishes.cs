using System.Collections.Generic;
using ElinTogether.LangMod;
using ElinTogether.Models;
using ElinTogether.Net;
using UnityEngine;

namespace ElinTogether.Helper;

/// <summary>
///     Host box "Players write a wishes line" (EmpConfig.Server.PlayerWishes, GitHub issue 21, council 2026-11-14): one
///     short line each player writes about its own character (favourite items, requests, "looking for birds to watch"),
///     shown to every player on its row of the Players page. The keeper of the world (the host, never a zone session)
///     holds the lines by character uid in its save, checks who writes (own character only, 140 characters, one change
///     per 10 s, never a muted player) and sends the whole table to everyone; each other game keeps the last table it
///     got. Rewards for a wish stay player to player through the Mailbox
/// </summary>
internal static class PlayerWishes
{
    internal const int MaxLength = 140;
    internal const float Interval = 10f;

    // refusal codes of PlayerWishDelta.Refusal
    internal const int TooFast = 1;
    internal const int TooLong = 2;
    internal const int Muted = 3;

    /// <summary>The keeper's book: character uid -> its line (saved with the world)</summary>
    [ElinGameIOProperty("player_wishes")]
    internal static Dictionary<int, string> Table
    {
        get => field ??= [];
        set;
    }

    /// <summary>The last table a guest got from the keeper</summary>
    internal static Dictionary<int, string> Received { get; set; } = [];

    // character uid -> when its line last changed, on the keeper
    private static readonly Dictionary<int, float> _last = [];

    /// <summary>The box as this game knows it: the host's own setting, a guest's from the host's rules</summary>
    internal static bool On => NetSession.Instance is { InGame: true } net && EClass.pc != null &&
                               (net.Transport is ElinNetHost ? EmpConfig.Server.PlayerWishes.Value : net.Rules is { PlayerWishes: true });

    /// <summary>The line of that character, as this game has it</summary>
    internal static string? Of(int uid)
    {
        var book = NetSession.Instance.Transport is ElinNetHost { IsZoneSession: false } ? Table : Received;
        return On && book.TryGetValue(uid, out var text) && text.Length > 0 ? text : null;
    }

    /// <summary>This game's own player writes its line: the host checks it at once, a guest asks the host</summary>
    internal static void Ask(string text)
    {
        if (!On) {
            return;
        }

        switch (NetSession.Instance.Transport) {
            case ElinNetHost { IsZoneSession: false } host:
                if (EClass.pc is { } pc && Set(host, pc.uid, false, text) is var code and not 0) {
                    Say(code);
                }

                break;
            case ElinNetClient client:
                var request = new PlayerWishDelta { Text = text };
                // (also from a map of one's own: straight to the host, which answers away players too)
                if (NetSession.Instance.IsAway) {
                    client.SendWhileAway(request);
                } else {
                    client.Delta.AddRemote(request);
                }

                break;
        }
    }

    /// <summary>
    ///     On the keeper: that character writes its line. 0 when it was kept (and told to everyone), else the refusal
    /// </summary>
    internal static int Set(ElinNetHost host, int uid, bool muted, string text)
    {
        if (!On || host.IsZoneSession) {
            return 0;
        }

        if (muted) {
            return Muted;
        }

        text = Clean(text);
        if (text.Length > MaxLength) {
            return TooLong;
        }

        var now = Time.realtimeSinceStartup;
        if (_last.TryGetValue(uid, out var last) && now >= last && now - last < Interval) {
            return TooFast;
        }

        _last[uid] = now;
        if (text.Length == 0) {
            Table.Remove(uid);
        } else {
            Table[uid] = text;
        }

        host.SendDeltaToAllExcept(int.MinValue, new PlayerWishDelta { All = new(Table) });
        return 0;
    }

    /// <summary>A player joins: the whole table</summary>
    internal static void Tell(ElinNetHost host, int peerId)
    {
        if (On && !host.IsZoneSession && Table.Count > 0) {
            host.SendDeltaTo(peerId, new PlayerWishDelta { All = new(Table) });
        }
    }

    /// <summary>One line: trimmed, no line break or control character</summary>
    internal static string Clean(string? text)
    {
        var chars = (text ?? "").ToCharArray();
        for (var i = 0; i < chars.Length; i++) {
            if (char.IsControl(chars[i])) {
                chars[i] = ' ';
            }
        }

        return new string(chars).Trim();
    }

    /// <summary>A refusal, said on this game</summary>
    internal static void Say(int code)
    {
        var text = (code switch {
            TooFast => "emp_ui_wishes_fast",
            TooLong => "emp_ui_wishes_long",
            _ => "emp_ui_wishes_muted",
        }).lang();
        if (WidgetPopText.Instance) {
            WidgetPopText.Say(text);
        } else {
            Msg.Say(text);
        }
    }

    /// <summary>The player's own input, prefilled with the current line</summary>
    internal static void Write()
    {
        if (!On || EClass.pc is not { } pc) {
            return;
        }

        Dialog.InputName("emp_ui_wishes_title", Of(pc.uid) ?? "", (cancel, text) => {
            if (!cancel) {
                Ask(text);
            }
        });
    }
}
