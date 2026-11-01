using System;
using System.Collections.Generic;
using System.Linq;
using ElinTogether.Helper;
using ElinTogether.LangMod;
using ElinTogether.Models;
using ElinTogether.Net;
using UnityEngine;
using UnityEngine.UI;
using YKF;

namespace ElinTogether.Components;

/// <summary>
///     The Market tab (host box "Market", user 2026-10-30): the world's classified ads, newest first, with filters and
///     a search, and a page to post one. The host keeps the board (<see cref="Market" />); this page asks for it when
///     shown and again when the host says it changed
/// </summary>
internal class TabMarket : TabEmpBase
{
    public const string TabName = "emp_tab_market";

    private static readonly string[] Icons = ["icon_money", "icon_stash3"];

    private const float RowH = 44f;
    private const int PageSize = 20;
    private const float RefreshSeconds = 30f;

    // kept over reopenings while the game runs, never stored
    private static int _filter;
    private static string _search = "";
    private static int _page;
    private static int _kind;
    private static string _item = "";
    private static string _text = "";
    private static string _qty = "1";
    private static string _price = "0";
    private static string _note = "";

    private YKVertical? _list;
    private UIText? _status;
    private UIText? _picked;
    private UIText? _mine;

    private static bool Enabled => Market.On;

    public override void OnLayout()
    {
        if (Waits()) {
            return;
        }

        if (!NetSession.Instance.InGame) {
            TabTeam.EmptyPage(this.Card("emp_ui_tab_market".lang()), Icons, "emp_ui_market_unplugged".lang());
            return;
        }

        if (!Enabled) {
            var card = this.Card("emp_ui_tab_market".lang());
            if (NetSession.Instance.Transport is ElinNetHost { IsZoneSession: false }) {
                TabTeam.EmptyPage(card, Icons, "emp_ui_market_off_host".lang(), "emp_ui_market_turn_on".lang(), () => {
                    LayerElinTogether.Instance?.Window.SwitchContent("emp_tab_server");
                    TabServerConfiguration.ShowRule("market");
                });
            } else {
                TabTeam.EmptyPage(card, Icons, "emp_ui_market_off".lang());
            }

            return;
        }

        SubTabs("market", [
            ("emp_ui_market_board".lang(), BuildBoard),
            ("emp_ui_market_post".lang(), BuildPost),
        ]);
        Market.Changed = OnChanged;
        Market.AskBoard();
    }

    private void OnDestroy()
    {
        if (Market.Changed == OnChanged) {
            Market.Changed = null;
        }
    }

    // a board landed: the list and the lines under the form are drawn again (the form keeps what is typed)
    private void OnChanged()
    {
        if (this == null) {
            return;
        }

        Refill();
        ShowStatus();
        Relayout();
    }

    // the board is asked again while the tab is shown (Update runs only then): when the host said it changed, or
    // every half minute
    private void Update()
    {
        if (_list == null || !Enabled) {
            return;
        }

        var since = Time.realtimeSinceStartup - Market.LastAsk;
        if (Market.Stale && since > 3f || since > RefreshSeconds) {
            Market.AskBoard();
        }
    }

    // ---------------------------------------------------------------- the board

    private static readonly string[] FilterKeys = ["emp_ui_market_filter_all", "emp_ui_market_selling", "emp_ui_market_looking", "emp_ui_market_mine"];

    private void BuildBoard(YKVertical page)
    {
        var card = page.Card("emp_ui_tab_market".lang());
        card.DropdownRow("emp_ui_market_show".lang(), [..FilterKeys.Select(k => k.lang())], i => {
            _filter = i;
            _page = 0;
            Refill();
            Relayout();
        }, Mathf.Clamp(_filter, 0, FilterKeys.Length - 1));

        var search = card.InputText(_search);
        search.field.characterLimit = 40;
        search.AllowAnyText();
        search.StyleAsSearch("emp_ui_market_search".lang());
        search.field.onValueChanged.AddListener(text => {
            _search = text.Trim();
            _page = 0;
            Refill();
            Relayout();
        });

        _list = card.ScrollList(7 * (RowH + UIHelper.RowSpacing) - UIHelper.RowSpacing);
        Refill();
    }

    private sealed record Row(int Id, int Kind, int Qty, int Price, long Age, int Author, string Item, string Name, string Text, string Note);

    private static List<Row> Rows()
    {
        if (Market.Latest is not { } b) {
            return [];
        }

        var rows = new List<Row>();
        for (var i = 0; i < b.Ids.Length; i++) {
            rows.Add(new(b.Ids[i], At(b.Kinds, i), At(b.Qtys, i), At(b.Prices, i), b.Ages.Length > i ? b.Ages[i] : 0, At(b.Authors, i),
                b.Items.ElementAtOrDefault(i) ?? "", b.Names.ElementAtOrDefault(i) ?? "", b.Texts.ElementAtOrDefault(i) ?? "",
                b.Notes.ElementAtOrDefault(i) ?? ""));
        }

        return rows;

        static int At(int[] a, int i) => a.Length > i ? a[i] : 0;
    }

    private static string ItemName(Row r) =>
        r.Item.Length > 0 && EClass.sources.things.map.TryGetValue(r.Item, out var row) ? row.GetName() ?? r.Item : r.Item.Length > 0 ? r.Item : r.Text;

    private static Sprite? ItemSprite(string item) =>
        item.Length > 0 && EClass.sources.things.map.TryGetValue(item, out var row) ? row.GetSprite() : EmpUi.Icon(Icons);

    private static bool Shows(Row r)
    {
        var me = EClass.pc?.uid ?? 0;
        var ok = _filter switch {
            1 => r.Kind == Market.Selling,
            2 => r.Kind == Market.LookingFor,
            3 => r.Author == me,
            _ => true,
        };
        if (!ok || _search.Length == 0) {
            return ok;
        }

        bool Has(string s) => s.IndexOf(_search, StringComparison.OrdinalIgnoreCase) >= 0;
        return Has(ItemName(r)) || Has(r.Item) || Has(r.Name) || Has(r.Note);
    }

    private static string Age(long seconds) =>
        seconds < 3600 ? "emp_ui_market_age_m".Loc(Math.Max(1L, seconds / 60)) :
        seconds < 86400 ? "emp_ui_market_age_h".Loc(seconds / 3600) : "emp_ui_market_age_d".Loc(seconds / 86400);

    private void Refill()
    {
        if (_list == null) {
            return;
        }

        foreach (Transform child in _list.transform) {
            child.gameObject.SetActive(false);
            Destroy(child.gameObject);
        }

        if (Market.Latest is null) {
            _list.Text("emp_ui_market_loading".lang(), FontColor.Gray);
            return;
        }

        var rows = Rows().Where(Shows).ToList();
        if (rows.Count == 0) {
            _list.Text("emp_ui_market_empty".lang(), FontColor.Gray);
            // (an empty board says what to do: the Post page, one click away)
            _list.ButtonRow().RowButton("emp_ui_market_post_ad".lang(), () => ShowSubTab(1));
            return;
        }

        var pages = Mathf.Max(1, (rows.Count + PageSize - 1) / PageSize);
        _page = Mathf.Clamp(_page, 0, pages - 1);
        foreach (var r in rows.Skip(_page * PageSize).Take(PageSize)) {
            AddRow(r);
        }

        if (pages > 1) {
            var bar = _list.ButtonRow();
            bar.SignButton("<", () => {
                _page--;
                Refill();
                Relayout();
            }, 44f);
            bar.Text($"{_page + 1} / {pages}");
            bar.SignButton(">", () => {
                _page++;
                Refill();
                Relayout();
            }, 44f);
        }
    }

    private void AddRow(Row r)
    {
        var me = EClass.pc?.uid ?? 0;
        var row = _list!.Row();
        row.LayoutElement().minHeight = RowH;
        TeamBanner.Frame(row, new(0.45f, 0.36f, 0.22f, 1f), ItemSprite(r.Item), 36f, false, null);

        var tag = (r.Kind == Market.Selling ? "emp_ui_market_selling" : "emp_ui_market_looking").lang();
        var title = $"[{tag}] {ItemName(r)} x{r.Qty}";
        var price = r.Price > 0 ? "emp_ui_market_price".Loc(r.Price.ToString("N0")) : "emp_ui_market_price_ask".lang();
        var line = $"{price} · {r.Name} · {Age(r.Age)}" + (r.Note.Length > 0 ? $"\n{r.Note}" : "");
        row.TextColumn(title, line);

        if (r.Author == me) {
            row.RowButton("emp_ui_market_remove".lang(), () => Market.Remove(r.Id));
            return;
        }

        var online = NetSession.Instance.CurrentPlayers.Any(p => p.CharaUid == r.Author);
        var chara = online ? Teams.FindChara(r.Author) : null;
        if (PlayerWhisper.Enabled && chara != null) {
            row.RowButton("emp_ui_market_message".lang(), () => PlayerWhisper.Open(chara));
        }

        if (PlayerTrade.Enabled && chara is { IsAliveInCurrentZone: true }) {
            row.RowButton("emp_act_trade".lang(), () => PlayerTrade.Invite(r.Author));
        }

        if (Market.IModerate()) {
            var id = r.Id;
            row.RowButton("emp_ui_market_remove".lang(), () => Dialog.YesNo("emp_ui_market_remove_confirm".Loc(r.Name), () => Market.Remove(id)));
        }
    }

    // ---------------------------------------------------------------- posting

    private void BuildPost(YKVertical page)
    {
        var (left, right) = page.Book();
        var card = left.Card("emp_ui_market_post".lang());
        card.DropdownRow("emp_ui_market_kind".lang(), ["emp_ui_market_selling".lang(), "emp_ui_market_looking".lang()],
            i => _kind = i, Mathf.Clamp(_kind, 0, 1));

        var itemRow = card.Row();
        itemRow.Text("emp_ui_market_item".lang()).SetSize(UIHelper.LabelSize);
        _picked = itemRow.Text("");
        _picked.LayoutElement().flexibleWidth = 1f;
        itemRow.RowButton("emp_ui_market_clear".lang(), () => {
            _item = "";
            ShowPicked();
        });
        ShowPicked();

        var text = card.InputText(_text);
        text.field.characterLimit = Market.MaxText;
        text.AllowAnyText();
        text.StyleAsSearch("emp_ui_market_text_hint".lang());
        text.field.onValueChanged.AddListener(t => _text = t);

        NumberRow(card, "emp_ui_market_qty", _qty, 4, v => _qty = v);
        NumberRow(card, "emp_ui_market_price_label", _price, 9, v => _price = v);

        var note = card.InputText(_note);
        note.field.characterLimit = Market.MaxNote;
        note.AllowAnyText();
        note.StyleAsSearch("emp_ui_market_note_hint".lang());
        note.field.onValueChanged.AddListener(t => _note = t);

        var bar = card.ButtonRow();
        bar.RowButton("emp_ui_market_send".lang(), () => {
            int.TryParse(_qty, out var qty);
            int.TryParse(_price, out var price);
            Market.LastResult = "";
            Market.Post(_kind, _item, _item.Length > 0 ? "" : _text, qty, price, _note);
            ShowStatus();
        });

        _status = card.Text("", FontColor.Gray);
        _mine = card.Text("", FontColor.Gray);
        ShowStatus();

        var pick = right.Card("emp_ui_market_pick".lang());
        TeamBanner.BuildThingPicker(pick, new(0.45f, 0.36f, 0.22f, 1f), value => {
            _item = Market.ThingId(value) ?? "";
            ShowPicked();
        }, true);
    }

    private static void NumberRow(YKLayout card, string labelId, string value, int digits, Action<string> onChange)
    {
        var row = card.Row();
        var label = row.Text(labelId.lang());
        label.SetSize(UIHelper.LabelSize);
        label.LayoutElement().flexibleWidth = 1f;
        var input = row.InputText(value);
        input.field.contentType = InputField.ContentType.IntegerNumber;
        input.field.characterLimit = digits;
        input.StyleAsSearch("0");
        input.LayoutElement().preferredWidth = 160f;
        input.LayoutElement().flexibleWidth = 0f;
        input.field.onValueChanged.AddListener(v => onChange(v));
    }

    private void ShowPicked()
    {
        if (_picked == null) {
            return;
        }

        _picked.text = _item.Length > 0 && EClass.sources.things.map.TryGetValue(_item, out var row)
            ? row.GetName() ?? _item
            : "emp_ui_market_free_text".lang();
    }

    private void ShowStatus()
    {
        if (_status != null) {
            _status.text = Market.LastResult.Length > 0 ? Market.LastResult.lang() : "";
        }

        if (_mine != null) {
            var me = EClass.pc?.uid ?? 0;
            _mine.text = "emp_ui_market_count".Loc(Rows().Count(r => r.Author == me), Market.MaxAdsPerPlayer);
        }
    }
}
