#if DEBUG
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;

namespace ElinTogether.Components;

/// <summary>
///     The mapping of the settings table checked without writing anything (bench: settings_suite.py, through the
///     eval bridge): every option written reads back the same, "Custom" writes nothing, and for every combination of
///     the boxes of a choice, picking the option it reads as changes no box (opening a page and choosing what it
///     shows never rewrites a hand-made file)
/// </summary>
internal static class SettingsTableCheck
{
    internal static string Run()
    {
        var failures = new List<string>();

        foreach (var choice in SettingsTable.Choices()) {
            for (var i = 0; i < choice.Options.Length; i++) {
                var written = Overlay(new(), choice.WriteFor(i));
                if (choice.Read(Source(written)) != i) {
                    failures.Add($"{choice.Id}: option {i} reads back {choice.Read(Source(written))}");
                }
            }

            if (choice.WriteFor(choice.Options.Length).Length != 0 || choice.WriteFor(-1).Length != 0) {
                failures.Add($"{choice.Id}: Custom writes");
            }

            foreach (var combo in Combos(choice.Entries)) {
                var read = choice.Read(Source(combo));
                // (a joining check written again may be spelt otherwise, "none" as "": what it means stays)
                var after = Overlay(new(combo), choice.WriteFor(read));
                if (read >= 0 && (choice.WriteFor(read).Any(w => w.Entry.SettingType == typeof(bool) && !Equals(Value(combo, w.Entry), w.Value)) ||
                        choice.Read(Source(after)) != read)) {
                    failures.Add($"{choice.Id}: {Show(combo)} reads {read} but writing {read} changes it");
                }
            }
        }

        // presets computed, never stored: each preset's values read as that preset, the old Classic values (no card
        // since 2026-11-01) as Custom, one box off the defaults as Custom with that one row listed
        for (var i = 0; i < SettingsTable.Presets.Length; i++) {
            if (i != SettingsTable.CustomPreset && SettingsTable.Preset(SettingsTable.PresetSource(i)) != i) {
                failures.Add($"presets: {SettingsTable.Presets[i]} reads {SettingsTable.Preset(SettingsTable.PresetSource(i))}");
            }
        }
        if (SettingsTable.Preset(RuleSource.Defaults) != 0 || SettingsTable.Preset(SettingsTable.Classic) != SettingsTable.CustomPreset) {
            failures.Add($"presets: defaults {SettingsTable.Preset(RuleSource.Defaults)}, classic {SettingsTable.Preset(SettingsTable.Classic)}");
        }
        var one = Source(new() { [EmpConfig.Server.PlayerTrade] = !(bool)EmpConfig.Server.PlayerTrade.DefaultValue });
        if (SettingsTable.Preset(one) != SettingsTable.CustomPreset ||string.Join(",", SettingsTable.Differences(one, RuleSource.Defaults)) != "player_trade") {
            failures.Add($"presets: one box off reads {SettingsTable.Preset(one)}: {string.Join(",", SettingsTable.Differences(one, RuleSource.Defaults))}");
        }

        // the joining check boxes: None and All stand alone
        foreach (var (raw, option, on, expected) in new[] {
                     ("all", "none", true, "none"), ("none", "all", true, "all"), ("none", "sources", true, "sources"),
                     ("all", "plugins", true, "plugins"), ("sources", "plugins", true, "plugins,sources"),
                     ("plugins,sources", "plugins", false, "sources"), ("", "none", true, "none"),
                 }) {
            var got = SettingsTable.ToggleCheck(raw, option, on);
            if (got != expected) {
                failures.Add($"joining check {raw} + {option}={on}: {got}, expected {expected}");
            }
        }

        return failures.Count == 0 ? "ok" : string.Join("\n", failures);
    }

    /// <summary>
    ///     The same through the real entries and file (the bench keeps a copy of the file and puts it back): every
    ///     option written reads back, "Custom" writes nothing, each preset applied reads as itself. The rules are
    ///     put back as they were at the end
    /// </summary>
    internal static string RunLive()
    {
        var failures = new List<string>();
        var before = SettingsTable.HostRules().Select(e => (e, e.BoxedValue)).ToArray();
        try {
            foreach (var choice in SettingsTable.Choices()) {
                for (var i = 0; i < choice.Options.Length; i++) {
                    SettingsTable.Write(choice.WriteFor(i));
                    if (choice.Read(RuleSource.Current) != i) {
                        failures.Add($"{choice.Id}: option {i} written reads back {choice.Read(RuleSource.Current)}");
                    }
                }
                if (SettingsTable.Write(choice.WriteFor(choice.Options.Length))) {
                    failures.Add($"{choice.Id}: Custom wrote");
                }
            }

            // (the public server preset last but one: Recommended puts everything back to the defaults)
            foreach (var preset in new[] { "duo", "hardcore", "community", "official", "recommended" }.Select(SettingsTable.PresetIndex)) {
                SettingsTable.ApplyPreset(preset);
                if (SettingsTable.Preset(RuleSource.Current) != preset) {
                    failures.Add($"preset {preset} applied reads {SettingsTable.Preset(RuleSource.Current)}");
                }
            }
        } finally {
            SettingsTable.Write(before);
        }

        return failures.Count == 0 ? "ok" : string.Join("\n", failures);
    }

    internal static int CombatCustomCount()
    {
        var choice = SettingsTable.CombatPace;
        return Combos(choice.Entries).Count(c => choice.Read(Source(c)) < 0);
    }

    private static readonly string[] Validations = ["", "none", "sources", "plugins", "all", "sources,plugins", "plugins,sources,",
        "none,sources", "all,none", "files", "sources,files:Data/x.json", "Sources"];

    private static IEnumerable<Dictionary<ConfigEntryBase, object>> Combos(ConfigEntryBase[] entries)
    {
        if (entries.All(e => e.SettingType == typeof(bool))) {
            for (var bits = 0; bits < 1 << entries.Length; bits++) {
                yield return entries.Select((e, n) => (e, (object)((bits >> n & 1) == 1))).ToDictionary(p => p.e, p => p.Item2);
            }
        } else if (entries is [{ } box, { } clock] && box.SettingType == typeof(bool) && clock.SettingType.IsEnum) {
            // (the travel choice: independent travel and the world map clock)
            foreach (var on in new[] { false, true }) {
                foreach (var value in System.Enum.GetValues(clock.SettingType)) {
                    yield return new() { [box] = on, [clock] = value };
                }
            }
        } else if (entries[0].SettingType == typeof(int)) {
            foreach (var hours in new[] { 0, 12, 24, 48, 72, 30 }) {
                yield return new() { [entries[0]] = hours };
            }
        } else if (entries[0] == EmpConfig.Server.SourceValidationSet) {
            foreach (var raw in Validations) {
                yield return new() { [entries[0]] = raw };
            }
        } else if (entries[0] == EmpConfig.Server.SpawnZone) {
            foreach (var raw in new[] { "start", "host", "", "startVillage2" }) {
                yield return new() { [entries[0]] = raw };
            }
        } else {
            foreach (var mode in System.Enum.GetValues(entries[0].SettingType)) {
                yield return new() { [entries[0]] = mode };
            }
        }
    }

    private static Dictionary<ConfigEntryBase, object> Overlay(Dictionary<ConfigEntryBase, object> values,
        IEnumerable<(ConfigEntryBase Entry, object Value)> writes)
    {
        foreach (var (entry, value) in writes) {
            values[entry] = value;
        }
        return values;
    }

    private static object Value(Dictionary<ConfigEntryBase, object> values, ConfigEntryBase entry) =>
        values.TryGetValue(entry, out var v) ? v : entry.DefaultValue;

    internal static RuleSource Source(Dictionary<ConfigEntryBase, object> values) => new(e => Value(values, e));

    private static string Show(Dictionary<ConfigEntryBase, object> values) =>
        string.Join(",", values.Select(p => $"{p.Key.Definition.Key}={p.Value}"));
}
#endif
