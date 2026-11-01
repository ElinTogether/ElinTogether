using System;
using System.Collections.Generic;
using System.Linq;
using ElinTogether.Helper;
using ElinTogether.LangMod;
using ElinTogether.Models;
using UnityEngine;
using UnityEngine.UI;
using YKF;

namespace ElinTogether.Components;

/// <summary>
///     The Mail page of the Players tab (host box "Mailbox", <see cref="Mailbox" />): the letters the host keeps for
///     this player on the left, newest first; on the right the picked letter (Take, then Delete once empty) or the
///     letter being written (recipient, text, gold, up to 5 stacks of the bag). The host answers every request with the
///     whole list, and the page is drawn again then
/// </summary>
internal static class TabMail
{
    private static readonly string[] Icons = ["icon_book", "icon_menu_16"];

    // the letter being written, kept over reopenings while the game runs (never stored)
    private static bool _writing;
    private static int _to;
    private static string _text = "";
    private static int _gold;
    private static readonly List<int> _items = [];

    // the letter shown on the right
    private static int _picked;

    private static YKVertical? _left;
    private static YKVertical? _right;
    private static Action? _relayout;

    public static void Build(YKVertical page, Action relayout)
    {
        if (!Mailbox.On) {
            TabTeam.EmptyPage(page.Card("emp_ui_mail_title".Loc(0)), Icons, "emp_ui_mail_off".lang());
            return;
        }

        (_left, _right) = page.Book();
        _relayout = relayout;
        Mailbox.Changed = OnChanged;
        Redraw();
        Mailbox.AskList();
    }

    /// <summary>
    ///     The Players tab goes: the page forgets its holders, unless a newer tab already holds the page
    /// </summary>
    public static void Release(Component owner)
    {
        if (_left == null || _left.transform.IsChildOf(owner.transform)) {
            Mailbox.Changed = null;
            _left = null;
            _right = null;
            _relayout = null;
        }
    }

    /// <summary>
    ///     The page opens on a new letter to that player (the "..." entries of the Players tab)
    /// </summary>
    internal static void WriteTo(int charaUid)
    {
        _writing = true;
        _to = charaUid;
    }

    private static void OnChanged()
    {
        if (Mailbox.Latest is { Done: "emp_mail_sent" }) {
            _writing = false;
            _text = "";
            _gold = 0;
            _items.Clear();
        }

        Redraw();
    }

    private static void Clear(YKVertical layout)
    {
        foreach (Transform child in layout.transform) {
            child.gameObject.SetActive(false);
            UnityEngine.Object.Destroy(child.gameObject);
        }
    }

    private static void Redraw()
    {
        if (_left == null || _right == null) {
            return;
        }

        Clear(_left);
        Clear(_right);

        var letters = Mailbox.Letters();
        var card = _left.Card("emp_ui_mail_title".Loc(letters.Count(Mailbox.Unclaimed)));
        card.ButtonRow().RowButton("emp_ui_mail_write".lang(), () => {
            _writing = true;
            Redraw();
        });

        if (Mailbox.Latest is null) {
            card.EmptyText("emp_ui_mail_asking".lang());
        } else if (letters.Count == 0) {
            card.EmptyText("emp_ui_mail_none".lang());
        } else {
            var rows = card.ListArea(letters.Count, 8, 44f);
            foreach (var letter in Enumerable.Reverse(letters)) {
                var shown = letter;
                rows.PickLine(Title(shown), Detail(shown), !_writing && shown.Id == _picked, () => {
                    _writing = false;
                    _picked = shown.Id;
                    Mailbox.MarkRead(shown);
                    Redraw();
                });
            }
        }

        // (the host and the admins only: the host sends this list to no one else)
        if (Mailbox.Latest is { Broken.Count: > 0 } latest) {
            var broken = _left.Card("emp_ui_mail_broken_title".Loc(latest.Broken.Count));
            foreach (var letter in latest.Broken) {
                var id = letter.Id;
                var row = broken.Row();
                row.Text("emp_ui_mail_broken_row".Loc(id, Plain(letter.ToName), Plain(letter.FromName), letter.Items.Count,
                    letter.Gold.ToString("N0"))).LayoutElement().flexibleWidth = 1f;
                row.RowButton("emp_ui_mail_retry".lang(), () => Mailbox.Retry(id));
            }
        }

        if (_writing) {
            BuildWriter(_right);
        } else if (letters.Find(l => l.Id == _picked) is { } picked) {
            BuildLetter(_right, picked);
        }

        UIHelper.WrapTexts(_left);
        UIHelper.WrapTexts(_right);
        _relayout?.Invoke();
    }

    private static bool Returned(MailLetter letter) => (letter.Flags & Mailbox.FlagReturned) != 0;

    private static string Title(MailLetter letter)
    {
        var name = Returned(letter) ? "emp_ui_mail_returned_from".Loc(Plain(letter.FromName)) : Plain(letter.FromName);
        return (Mailbox.Unclaimed(letter) ? "* " : "") + name;
    }

    private static string Detail(MailLetter letter)
    {
        var detail = DateOf(letter.Sent);
        if (letter.Items.Count > 0 || letter.Gold > 0) {
            detail += "  " + "emp_ui_mail_holds".Loc(letter.Items.Count, letter.Gold.ToString("N0"));
        }

        return detail;
    }

    private static string DateOf(long seconds)
    {
        return DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    }

    private static void BuildLetter(YKVertical page, MailLetter letter)
    {
        var card = page.Card(Returned(letter) ? "emp_ui_mail_returned_from".Loc(Plain(letter.FromName)) : Plain(letter.FromName));
        card.KeyValue("emp_ui_mail_date".lang(), DateOf(letter.Sent));
        if (Returned(letter)) {
            card.TextSmall("emp_ui_mail_returned_note".Loc(Plain(letter.FromName)), FontColor.Gray);
        } else if ((letter.Flags & Mailbox.FlagTaken) == 0 && (letter.Items.Count > 0 || letter.Gold > 0)) {
            var left = (int)Math.Max(0, (letter.Sent + Mailbox.ReturnSeconds - Mailbox.Now()) / 86400);
            card.TextSmall("emp_ui_mail_returns_in".Loc(left), FontColor.Gray);
        }

        if (letter.Text.Length > 0) {
            card.Divider();
            card.Text(letter.Text.Replace("<", "‹"));
        }

        if (letter.Items.Count > 0 || letter.Gold > 0) {
            card.Divider();
            for (var i = 0; i < letter.Items.Count; i++) {
                var thing = Mailbox.ItemOf(letter, i);
                var row = card.Row();
                row.CardIcon(thing, 32f);
                row.Text(thing?.Name ?? "?").LayoutElement().flexibleWidth = 1f;
            }

            if (letter.Gold > 0) {
                card.KeyValue("emp_ui_mail_gold".lang(), letter.Gold.ToString("N0"));
            }
        }

        var id = letter.Id;
        var buttons = card.ButtonRow();
        if (letter.Items.Count > 0 || letter.Gold > 0) {
            buttons.RowButton("emp_ui_mail_take".lang(), () => Mailbox.Take(id));
        } else {
            buttons.RowButton("emp_ui_mail_delete".lang(), () => Dialog.YesNo("emp_ui_mail_delete_confirm".lang(), () => Mailbox.Delete(id)));
        }
    }

    // what can go in a letter: the stacks of the bag's top level a player may hand over (as for a trade), not worn
    private static List<Thing> Sendable()
    {
        return EClass.pc.things.Where(t => !t.isEquipped && t.trait is not TraitAbility && PlayerTrade.Refuse(t) is null).ToList();
    }

    private static void BuildWriter(YKVertical page)
    {
        var card = page.Card("emp_ui_mail_write".lang());
        var state = Mailbox.Latest;
        var directory = state is null ? [] : state.Uids.Zip(state.Names, (uid, name) => (Uid: uid, Name: name)).ToList();
        if (directory.Count == 0) {
            card.EmptyText((state is null ? "emp_ui_mail_asking" : "emp_ui_mail_no_one").lang());
            card.ButtonRow().RowButton("emp_ui_mail_cancel".lang(), () => {
                _writing = false;
                Redraw();
            });
            return;
        }

        var index = Mathf.Max(0, directory.FindIndex(d => d.Uid == _to));
        _to = directory[index].Uid;
        card.DropdownRow("emp_ui_mail_to".lang(), directory.Select(d => Plain(d.Name)).ToList(), i => _to = directory[i].Uid, index);

        var text = card.InputText(_text);
        text.field.characterLimit = Mailbox.MaxText;
        text.AllowAnyText();
        text.StyleAsSearch("emp_ui_mail_hint".Loc(Mailbox.MaxText));
        text.field.lineType = InputField.LineType.MultiLineNewline;
        var box = text.GetComponent<LayoutElement>();
        box.minHeight = box.preferredHeight = 96f;
        text.field.onValueChanged.AddListener(t => _text = t);

        var have = EClass.pc.GetCurrency();
        var goldRow = card.Horizontal();
        goldRow.Layout.childForceExpandWidth = false;
        goldRow.Layout.childAlignment = TextAnchor.MiddleLeft;
        goldRow.Layout.spacing = 10f;
        goldRow.Text("emp_ui_mail_gold".lang()).LayoutElement().flexibleWidth = 1f;
        var gold = goldRow.InputText(_gold > 0 ? _gold.ToString() : "");
        gold.field.contentType = InputField.ContentType.IntegerNumber;
        gold.field.characterLimit = 9;
        gold.StyleAsSearch("0");
        gold.LayoutElement().preferredWidth = 140f;
        gold.field.onValueChanged.AddListener(t => _gold = int.TryParse(t, out var g) ? Mathf.Clamp(g, 0, have) : 0);
        goldRow.Text("emp_ui_mail_have".Loc(have.ToString("N0")), FontColor.Gray);

        var bag = Sendable();
        _items.RemoveAll(uid => !bag.Exists(t => t.uid == uid));
        card.TextSmall("emp_ui_mail_items".Loc(_items.Count, Mailbox.MaxStacks), FontColor.Gray);
        if (bag.Count == 0) {
            card.EmptyText("emp_ui_mail_bag_empty".lang());
        } else {
            var list = card.ListArea(bag.Count, 6, 40f);
            foreach (var thing in bag) {
                var uid = thing.uid;
                var chosen = _items.Contains(uid);
                var row = list.Row();
                row.CardIcon(thing, 32f);
                row.Text(thing.Name, chosen ? FontColor.Good : FontColor.Default).LayoutElement().flexibleWidth = 1f;
                if (!chosen && _items.Count >= Mailbox.MaxStacks) {
                    continue;
                }

                row.RowButton((chosen ? "emp_ui_mail_remove" : "emp_ui_mail_add").lang(), () => {
                    if (!_items.Remove(uid) && _items.Count < Mailbox.MaxStacks) {
                        _items.Add(uid);
                    }

                    Redraw();
                });
            }
        }

        var buttons = card.ButtonRow();
        buttons.RowButton("emp_ui_mail_send".lang(), () => {
            var body = _text.Trim();
            if (body.Length == 0 && _items.Count == 0 && _gold <= 0) {
                EmpPop.Information("emp_mail_empty".lang());
                return;
            }

            Mailbox.Write(_to, body, _gold, _items.ToList());
        });
        buttons.RowButton("emp_ui_mail_cancel".lang(), () => {
            _writing = false;
            Redraw();
        });
    }

    // a name from another player's game: shown as text, never as rich-text tags (review 2026-10-30)
    private static string Plain(string? name) => (name ?? "?").Replace("<", "‹").Replace(">", "›");
}
