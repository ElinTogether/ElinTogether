using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ElinTogether.Models;
using ElinTogether.Net;
using HeathenEngineering.SteamworksIntegration;
using UnityEngine;

namespace ElinTogether.Helper;

/// <summary>
///     Host box "Market" (user 2026-10-30): a classified-ads board for a public server. A player posts "Selling" or
///     "Looking for" ads (a thing of the game or a free text, a quantity, a price, a short note); the host keeps them in
///     the world save, checks every post (limits below) and sends the board to a player who asks. At each change every
///     player is told the board's new version only, and an open Market tab asks again. The host's own player asks and
///     is answered by the same functions
/// </summary>
internal static class Market
{
    internal const int Selling = 0;
    internal const int LookingFor = 1;

    internal const int MaxAdsPerPlayer = 5;
    internal const int MaxText = 40;
    internal const int MaxNote = 140;
    internal const int MaxQty = 9999;
    internal const int MaxPrice = 999_999_999;
    internal const long LifeSeconds = 7L * 24 * 3600;

    private const float PostSeconds = 10f;
    private const float AskSeconds = 1.5f;

    internal static bool On => NetSession.Instance is { InGame: true, Rules.Market: true };

    /// <summary>
    ///     Added to the real clock (seconds): the bench moves time forward to test the expiry
    /// </summary>
    internal static long ClockOffset { get; set; }

    internal static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds() + ClockOffset;

    internal sealed record Ad(int Id, int Author, int Kind, int Qty, int Price, long Posted, string Item, string Name, string Text, string Note);

    // ---------------------------------------------------------------- host: storage

    private static bool KeepsBook => NetSession.Instance.Transport is ElinNetHost { IsZoneSession: false };

    private static string Pack(Ad a) =>
        string.Join("|", a.Author, a.Kind, a.Qty, a.Price, a.Posted, a.Item, a.Name, a.Text, a.Note);

    private static Ad? Unpack(int id, string text)
    {
        var p = text.Split('|', 9);
        return p.Length == 9 && int.TryParse(p[0], out var author) && int.TryParse(p[1], out var kind) && int.TryParse(p[2], out var qty) &&
               int.TryParse(p[3], out var price) && long.TryParse(p[4], out var posted)
            ? new Ad(id, author, kind, qty, price, posted, p[5], p[6], p[7], p[8])
            : null;
    }

    /// <summary>
    ///     The ads still running, newest first; the expired ones are dropped from the save
    /// </summary>
    internal static List<Ad> Ads()
    {
        var book = ElinNetHost.MarketAds;
        var now = Now();
        var ads = new List<Ad>();
        foreach (var (id, text) in book.ToArray()) {
            if (Unpack(id, text) is not { } ad || now - ad.Posted >= LifeSeconds) {
                book.Remove(id);
                continue;
            }

            ads.Add(ad);
        }

        ads.Sort((a, b) => b.Posted != a.Posted ? b.Posted.CompareTo(a.Posted) : b.Id.CompareTo(a.Id));
        return ads;
    }

    /// <summary>
    ///     A player's text made safe to show and to store: no control characters, no rich-text tags, no separator
    /// </summary>
    internal static string Clean(string? text, int max)
    {
        if (string.IsNullOrEmpty(text)) {
            return "";
        }

        var sb = new StringBuilder(text!.Length);
        foreach (var c in text) {
            if (char.IsControl(c)) {
                continue;
            }

            sb.Append(c switch {
                '<' => '‹',
                '>' => '›',
                '|' => '/',
                _ => c,
            });
        }

        var clean = sb.ToString().Trim();
        return clean.Length > max ? clean.Substring(0, max) : clean;
    }

    /// <summary>A thing id of this game (an optional "t:" of the picker taken off), or null</summary>
    internal static string? ThingId(string? item)
    {
        if (string.IsNullOrEmpty(item)) {
            return null;
        }

        var id = item!.StartsWith("t:") ? item.Substring(2) : item;
        return EClass.sources.things.map.ContainsKey(id) ? id : null;
    }

    private static int _version;
    private static readonly Dictionary<int, float> _posted = [];
    private static readonly Dictionary<int, float> _asked = [];

    private static bool Moderates(ulong user, bool hostPlayer) =>
        hostPlayer || ElinNetHost.Admins.ContainsKey(user) || ElinNetHost.AdminExtrasOn && ElinNetHost.Moderators.ContainsKey(user);

    /// <summary>
    ///     Checks a post; null when it is taken, else the lang id of the refusal
    /// </summary>
    internal static string? Refusal(MarketAskDelta ask, Chara from, List<Ad> ads, out Ad? ad)
    {
        ad = null;
        if (ask.Kind is not (Selling or LookingFor)) {
            return "emp_ui_market_err_kind";
        }

        var item = "";
        var text = "";
        if (!string.IsNullOrEmpty(ask.Item)) {
            if (ThingId(ask.Item) is not { } id) {
                return "emp_ui_market_err_item";
            }

            item = id;
        } else {
            text = Clean(ask.Text, int.MaxValue);
            if (text.Length == 0 || text.Length > MaxText) {
                return "emp_ui_market_err_text";
            }
        }

        if (ask.Qty is < 1 or > MaxQty) {
            return "emp_ui_market_err_qty";
        }

        if (ask.Price is < 0 or > MaxPrice) {
            return "emp_ui_market_err_price";
        }

        var note = Clean(ask.Note, int.MaxValue);
        if (note.Length > MaxNote) {
            return "emp_ui_market_err_note";
        }

        if (ads.Count(a => a.Author == from.uid) >= MaxAdsPerPlayer) {
            return "emp_ui_market_err_limit";
        }

        var now = Time.realtimeSinceStartup;
        if (_posted.TryGetValue(from.uid, out var last) && now - last < PostSeconds) {
            return "emp_ui_market_err_rate";
        }

        var state = ElinNetHost.MarketState;
        var next = state.GetValueOrDefault(0) + 1;
        state[0] = next;
        _posted[from.uid] = now;
        ad = new(next, from.uid, ask.Kind, ask.Qty, ask.Price, Now(), item, Clean(Teams.PlayerName(from.uid), 40), text, note);
        return null;
    }

    /// <summary>
    ///     A player's ask (the host's own player too: <paramref name="hostPlayer" />), answered to that player alone
    /// </summary>
    internal static void Handle(MarketAskDelta ask, Chara from, ulong user, bool hostPlayer, ElinNetHost host, bool muted = false)
    {
        if (!KeepsBook) {
            return;
        }

        var result = "";
        var changed = false;
        if (!EmpConfig.Server.Market.Value) {
            result = "emp_ui_market_off";
        } else {
            var now = Time.realtimeSinceStartup;
            // (every ask, refused ones too: each answer carries the whole board, review 2026-10-30)
            if (_asked.TryGetValue(from.uid, out var last) && now - last < (ask.Verb == MarketAskDelta.List ? AskSeconds : 0.5f)) {
                return;
            }

            _asked[from.uid] = now;
            var ads = Ads();
            switch (ask.Verb) {
                case MarketAskDelta.Post:
                    // (a muted player posts nothing, as it says nothing, review 2026-10-30)
                    if (muted) {
                        result = "emp_ui_muted";
                    } else if (Refusal(ask, from, ads, out var ad) is { } why) {
                        result = why;
                    } else {
                        ElinNetHost.MarketAds[ad!.Id] = Pack(ad);
                        result = "emp_ui_market_posted";
                        changed = true;
                        EmpLog.Information("Market ad {Id} by {Author}: kind {Kind}, {Item}{Text} x{Qty} for {Price}",
                            ad.Id, ad.Author, ad.Kind, ad.Item, ad.Text, ad.Qty, ad.Price);
                    }

                    break;
                case MarketAskDelta.Remove:
                    var target = ads.FirstOrDefault(a => a.Id == ask.AdId);
                    if (target is null) {
                        result = "emp_ui_market_err_gone";
                    } else if (target.Author != from.uid && !Moderates(user, hostPlayer)) {
                        result = "emp_ui_market_err_not_yours";
                    } else {
                        ElinNetHost.MarketAds.Remove(target.Id);
                        result = "emp_ui_market_removed";
                        changed = true;
                        EmpLog.Information("Market ad {Id} removed by {Uid}", target.Id, from.uid);
                    }

                    break;
            }
        }

        if (changed) {
            _version++;
        }

        var answer = Board(result);
        if (hostPlayer) {
            Receive(answer);
        } else {
            foreach (var peerId in host.PeerIds.ToArray()) {
                if (host.PlayerChara(peerId)?.uid == from.uid) {
                    host.SendDeltaTo(peerId, answer);
                    break;
                }
            }
        }

        if (changed) {
            // every other game learns the new version (a few bytes): an open Market tab asks again
            host.Delta.AddRemote(new MarketBoardDelta { Version = _version });
            // (and the host's own open tab)
            Receive(new MarketBoardDelta { Version = _version });
        }
    }

    private static MarketBoardDelta Board(string result)
    {
        var ads = Ads();
        var now = Now();
        return new() {
            Version = _version,
            Full = true,
            Ids = ads.Select(a => a.Id).ToArray(),
            Kinds = ads.Select(a => a.Kind).ToArray(),
            Qtys = ads.Select(a => a.Qty).ToArray(),
            Prices = ads.Select(a => a.Price).ToArray(),
            Ages = ads.Select(a => Math.Max(0L, now - a.Posted)).ToArray(),
            Authors = ads.Select(a => a.Author).ToArray(),
            Items = ads.Select(a => a.Item).ToArray(),
            Names = ads.Select(a => a.Name).ToArray(),
            Texts = ads.Select(a => a.Text).ToArray(),
            Notes = ads.Select(a => a.Note).ToArray(),
            Result = result,
        };
    }

    // ---------------------------------------------------------------- player side

    /// <summary>The last board this game was sent (null before the first answer)</summary>
    internal static MarketBoardDelta? Latest { get; private set; }

    /// <summary>The newest board version this game heard of</summary>
    internal static int KnownVersion { get; private set; } = -1;

    /// <summary>The host's word on this player's last post or removal (a lang id), shown once by the tab</summary>
    internal static string LastResult { get; set; } = "";

    internal static float LastAsk { get; private set; } = float.MinValue;

    /// <summary>The board shown is older than the host's: the open tab asks again</summary>
    internal static bool Stale => Latest is null || Latest.Version < KnownVersion;

    /// <summary>Told when a board lands (the open tab redraws)</summary>
    internal static Action? Changed { get; set; }

    internal static void Receive(MarketBoardDelta board)
    {
        // (a full board says how things are: a host started again counts from 0, a notice only moves the number up)
        KnownVersion = board.Full ? board.Version : Math.Max(KnownVersion, board.Version);
        if (!board.Full) {
            return;
        }

        Latest = board;
        if (board.Result.Length > 0) {
            LastResult = board.Result;
        }

        Changed?.Invoke();
    }

    /// <summary>Back to the title: nothing of the last world's board is kept</summary>
    [ElinPreSceneInit]
    private static void OnTitle(Scene.Mode mode)
    {
        if (mode == Scene.Mode.Title) {
            Forget();
        }
    }

    internal static void Forget()
    {
        Latest = null;
        KnownVersion = -1;
        LastResult = "";
        _posted.Clear();
        _asked.Clear();
    }

    internal static void Ask(MarketAskDelta ask)
    {
        LastAsk = Time.realtimeSinceStartup;
        switch (NetSession.Instance.Transport) {
            case ElinNetHost { IsZoneSession: false } host:
                if (EClass.pc is { } pc) {
                    Handle(ask, pc, ServerAvatar.HostUser, true, host);
                }

                break;
            case ElinNetClient client:
                client.Delta.AddRemote(ask);
                break;
        }
    }

    internal static void AskBoard() => Ask(new() { Verb = MarketAskDelta.List });

    internal static void Post(int kind, string item, string text, int qty, int price, string note) =>
        Ask(new() { Verb = MarketAskDelta.Post, Kind = kind, Item = item, Text = text, Qty = qty, Price = price, Note = note });

    internal static void Remove(int adId) => Ask(new() { Verb = MarketAskDelta.Remove, AdId = adId });

    /// <summary>
    ///     This player may remove any ad: the host, an admin, or a moderator of this world
    /// </summary>
    internal static bool IModerate()
    {
        var me = (ulong)(NetSession.Instance.Self?.User ?? UserData.Me);
        return PeerHosting.IAmAdmin() || ModerationDelta.ModeratorIds.Contains(me);
    }
}
