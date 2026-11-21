using System.Linq;
using BepInEx.Configuration;
using ElinTogether.Components;
using ElinTogether.Helper;
using ElinTogether.Net;

namespace ElinTogether;

/// <summary>
///     The settings as the game's own mod config page (mod list, "Config" button): the host's rules and the
///     client's simple options. What needs a dialog (depot, keymap) stays in the mod's own window
/// </summary>
internal static class ModConfigPage
{
    internal static void Register()
    {
        SettingsTable.Watch();

        var package = EClass.core.mods.packages.OfType<ModPackage>()
            .FirstOrDefault(p => BaseModPackage.NormalizeId(p.id) == BaseModPackage.NormalizeId(ModInfo.Guid));
        if (package == null) {
            return;
        }

        _package = package;
        package.onBuildConfig = Build;
        package.onResetConfig = EmpConfig.Reset;
        package.configPath = EmpMod.Instance.Config.ConfigFilePath;
    }

    private static ModPackage? _package;

    /// <summary>
    ///     Drawn again (greyed boxes, choices) when a rule changes while the page is open
    /// </summary>
    internal static void Refresh()
    {
        if (EClass.ui?.GetLayer<LayerModConfig>() is { } layer && layer.current == _package && _package is not null) {
            layer.Show(_package);
        }
    }

    /// <summary>
    ///     The same table as the mod's World rules tab, by the same rules: the preset line, the visible rows (all
    ///     of them with "Show advanced options"), Reset, Undo. The joining check boxes and the tax bills stay in the
    ///     mod's own window
    /// </summary>
    private static void Build(UINote note)
    {
        // the host's rules: not while playing in someone else's game, where the host's apply (the mod's own window
        // shows them only to the host too)
        if (NetSession.Instance.Transport is not ElinNetClient) {
            note.AddText("emp_ui_sv_note_live".lang());

            var preset = SettingsTable.Preset(RuleSource.Current);
            Dropdown(note, "emp_ui_sv_cfg_preset", [..SettingsTable.Presets.Select(p => $"emp_ui_sv_opt_preset_{p}".lang())], preset,
                SettingsTable.ApplyPreset);
            note.AddText(TabServerConfiguration.PresetText(preset));
            if (preset != 0 || SettingsTable.CanUndo) {
                var buttons = UIHelper.NoteButtonRow(note);
                if (preset != 0) {
                    note.AddButton("emp_ui_sv_reset".lang(), () => SettingsTable.ApplyPreset(0)).InRow(buttons);
                }
                if (SettingsTable.CanUndo) {
                    note.AddButton("emp_ui_sv_undo".lang(), SettingsTable.Undo).InRow(buttons);
                }
            }
            note.AddToggle("emp_ui_sv_advanced", TabServerConfiguration.ShowAdvanced, value => {
                TabServerConfiguration.ShowAdvanced = value;
                SettingsTable.MarkDirty();
            });

            var rows = SettingsTable.Rows().Where(r => (TabServerConfiguration.ShowAdvanced || !r.Advanced) && r.Id is not ("checks" or "bills" or "client_mods" or "lobby_password"))
                .ToList();
            foreach (var card in SettingsTable.Cards) {
                var inCard = rows.Where(r => r.Card == card).ToList();
                if (inCard.Count == 0) {
                    continue;
                }

                note.AddHeader($"emp_ui_sv_sec_{card}".lang());
                foreach (var row in inCard) {
                    if (row.Choice is { } choice) {
                        var current = choice.Read(RuleSource.Current);
                        var options = Enumerable.Range(0, choice.Options.Length).Select(choice.OptionLabel).ToList();
                        if (current < 0) {
                            options.Add(choice.OptionLabel(-1));
                        }
                        var dropdown = Dropdown(note, $"emp_ui_sv_cfg_{row.Id}", options, current < 0 ? options.Count - 1 : current,
                            i => SettingsTable.Write(choice.WriteFor(i)));
                        if (row.Requires is { } needed) {
                            dropdown.interactable = needed();
                        }
                    } else if (row.Entry is { } entry) {
                        var toggle = note.AddToggle($"emp_ui_sv_cfg_{row.Id}", entry.Value, value => SettingsTable.Write([(entry, value)]));
                        toggle.SetTooltipLang($"emp_ui_sv_desc_{row.Id}");
                        if (row.Requires is { } requires) {
                            toggle.interactable = requires();
                        }
                    }
                    // (no tooltip on a controller: the text under the row)
                    foreach (var line in row.Description.Split('\n')) {
                        note.AddText(line);
                    }
                }
            }

            note.AddText("emp_ui_sv_more");
        }

        note.AddHeader("emp_ui_tab_client".lang());
        Toggle(note, "emp_ui_cl_fetch_mods", "emp_ui_cl_desc_fetch_mods", EmpConfig.Client.FetchMods);
        Toggle(note, "emp_ui_cl_keep_mods", "emp_ui_cl_desc_keep_mods", EmpConfig.Client.KeepMods).interactable =
            EmpConfig.Client.FetchMods.Value;
        Toggle(note, "emp_ui_cl_code_mods", "emp_ui_cl_desc_code_mods", EmpConfig.Client.FetchCodeMods).interactable =
            EmpConfig.Client.FetchMods.Value;
    }

    /// <summary>
    ///     A labelled dropdown whose value is set before it listens: drawing the page writes nothing
    /// </summary>
    private static UIDropdown Dropdown(UINote note, string label, System.Collections.Generic.List<string> options, int value,
        System.Action<int> onPick)
    {
        var dropdown = note.AddDropdown(label);
        dropdown.ClearOptions();
        dropdown.AddOptions(options);
        dropdown.SetValueWithoutNotify(value);
        dropdown.onValueChanged.AddListener(i => onPick(i));
        return dropdown;
    }

    private static UIButton Toggle(UINote note, string label, string desc, ConfigEntry<bool> entry)
    {
        var toggle = note.AddToggle(label, entry.Value, value => entry.Value = value);
        toggle.SetTooltipLang(desc);
        // (no tooltip on a controller: the text under the box)
        note.AddText(desc);
        return toggle;
    }
}
