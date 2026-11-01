using System.Collections.Generic;
using System.IO;
using System.Linq;
using ElinTogether.Helper;
using Newtonsoft.Json;
using UnityEngine;
using YKF;

namespace ElinTogether.Components;

/// <summary>
///     What changed in each version (user 2026-11-01), offline: LangMod/EN/patch_notes.json next to the DLL (CN for
///     Chinese, JP for Japanese, else English), made from the release notes by dev/_tools/make_patch_notes.py. The
///     versions on the left page, newest first; the picked one's short lines on the right page
/// </summary>
internal class TabPatchNotes : TabEmpBase
{
    public const string TabName = "emp_tab_patchnotes";

    internal sealed class Entry
    {
        [JsonProperty("version")] public string Version = "";
        [JsonProperty("date")] public string Date = "";
        [JsonProperty("title")] public string Title = "";
        [JsonProperty("bullets")] public List<string> Bullets = [];
    }

    private static List<Entry>? _notes;
    private static string _notesLang = "";

    /// <summary>The notes in the player's language (Chinese, Japanese, else English), read once per language</summary>
    internal static List<Entry> Notes
    {
        get {
            var lang = Lang.langCode is "CN" or "JP" ? Lang.langCode : "EN";
            if (_notes is null || _notesLang != lang) {
                _notesLang = lang;
                _notes = Read(lang) ?? (lang != "EN" ? Read("EN") : null) ?? [];
            }

            return _notes;
        }
    }

    private static List<Entry>? Read(string lang)
    {
        try {
            var dir = Path.GetDirectoryName(EmpMod.Assembly.Location) ?? "";
            var path = Path.Combine(dir, "LangMod", lang, "patch_notes.json");
            if (!File.Exists(path)) {
                return null;
            }

            var list = JsonConvert.DeserializeObject<List<Entry>>(File.ReadAllText(path));
            return list is { Count: > 0 } ? list : null;
        }
        catch (System.Exception e) {
            EmpLog.Debug(e, "The patch notes could not be read");
            return null;
        }
    }

    /// <summary>The running mod's version as the notes write it (the assembly's has a fourth part)</summary>
    internal static string Installed
    {
        get {
            var parts = ModInfo.BuildVersion.Split('.');
            return string.Join(".", parts.Take(3));
        }
    }

    /// <summary>The mod was updated and its notes not seen yet: a badge on the tab</summary>
    internal static bool Unseen => EmpConfig.Client.PatchNotesSeen.Value != Installed;

    // the version open on the right page (an index into Notes), kept while the game runs
    private static int _picked;

    public override void OnLayout()
    {
        if (Waits()) {
            return;
        }

        // (built only when shown: the notes are seen)
        EmpConfig.Client.PatchNotesSeen.Value = Installed;

        var page = Vertical();
        page.Fitter.verticalFit = UnityEngine.UI.ContentSizeFitter.FitMode.PreferredSize;
        page.LayoutElement().flexibleWidth = 1f;

        var notes = Notes;
        var (left, right) = page.Book();
        var list = left.Card("emp_ui_tab_patchnotes".lang());
        if (notes.Count == 0) {
            list.Text("emp_ui_patchnotes_none".lang());
            return;
        }

        var text = right.Vertical();
        text.Fitter.verticalFit = UnityEngine.UI.ContentSizeFitter.FitMode.PreferredSize;
        text.Layout.spacing = UIHelper.RowSpacing;
        var rows = new List<YKHorizontal>();
        var installed = Installed;

        string Detail(Entry entry) =>
            entry.Version == installed ? $"{entry.Date}  ({"emp_ui_patchnotes_installed".lang()})" : entry.Date;

        void Show(int index, bool relayout = true)
        {
            _picked = index;
            foreach (Transform child in text.transform) {
                Destroy(child.gameObject);
            }

            var entry = notes[index];
            var card = text.Card(entry.Version);
            if (!string.IsNullOrEmpty(entry.Title)) {
                card.Text(entry.Title, FontColor.Gray);
            }

            foreach (var bullet in entry.Bullets) {
                card.Text("• " + bullet);
            }

            for (var i = 0; i < rows.Count; i++) {
                if (rows[i].GetComponent<UnityEngine.UI.Image>() is { } shade) {
                    shade.color = i == index ? new(0.28f, 0.22f, 0.13f, 0.14f) : new(0f, 0f, 0f, 0.001f);
                }
            }

            UIHelper.WrapTexts(text);
            if (relayout) {
                Relayout();
            }
        }

        for (var i = 0; i < notes.Count; i++) {
            var index = i;
            rows.Add(list.PickLine(notes[i].Version, Detail(notes[i]), i == _picked, () => Show(index)));
        }

        Show(Mathf.Clamp(_picked, 0, notes.Count - 1), false);
    }
}
