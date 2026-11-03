using System.Linq;
using System.Runtime.CompilerServices;
using EModding.Helper;
using Serilog.Core;
using Serilog.Events;
using UnityEngine;

#pragma warning disable CA2254

namespace ElinTogether;

internal static partial class EmpLogger
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Debug(string messageTemplate)
    {
        Popup(LogEventLevel.Debug, messageTemplate);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    [MessageTemplateFormatMethod("messageTemplate")]
    internal static void Debug<T0>(string messageTemplate, T0 property0)
    {
        Popup(LogEventLevel.Debug, messageTemplate, property0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    [MessageTemplateFormatMethod("messageTemplate")]
    internal static void Debug<T0, T1>(string messageTemplate, T0 property0, T1 property1)
    {
        Popup(LogEventLevel.Debug, messageTemplate, property0, property1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    [MessageTemplateFormatMethod("messageTemplate")]
    internal static void Debug(string messageTemplate, params object?[]? propertyValues)
    {
        Popup(LogEventLevel.Debug, messageTemplate, propertyValues);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Information(string messageTemplate)
    {
        Popup(LogEventLevel.Information, messageTemplate);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    [MessageTemplateFormatMethod("messageTemplate")]
    internal static void Information<T0>(string messageTemplate, T0 property0)
    {
        Popup(LogEventLevel.Information, messageTemplate, property0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    [MessageTemplateFormatMethod("messageTemplate")]
    internal static void Information<T0, T1>(string messageTemplate, T0 property0, T1 property1)
    {
        Popup(LogEventLevel.Information, messageTemplate, property0, property1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    [MessageTemplateFormatMethod("messageTemplate")]
    internal static void Information(string messageTemplate, params object?[]? propertyValues)
    {
        Popup(LogEventLevel.Information, messageTemplate, propertyValues);
    }

    // Notice kinds (council 2026-10-26): Log = Information (the game's message log), File = Debug (mod file log only
    // in Release, and there only with the player's box "Detailed logs"), Pop = a short text over the map, Block = a dialog the player must close.

    /// <summary>
    ///     A refusal tied to a click: a short text over the map (message log line where the map is not up)
    /// </summary>
    [MessageTemplateFormatMethod("messageTemplate")]
    internal static void Pop(string messageTemplate, params object?[]? propertyValues)
    {
        EmpLog.Write(LogEventLevel.Information, messageTemplate, propertyValues);
        var rendered = Render(messageTemplate, propertyValues);
        if (rendered is null) {
            return;
        }
        if (InGame) {
            WidgetPopText.Say(rendered);
            return;
        }
        PopupInternal(messageTemplate, propertyValues);
    }

    private static Dialog? _block;

    /// <summary>
    ///     The player must act and the session ended or cannot start: one dialog at a time (a second notice while it is
    ///     open stays in the file log), main thread only, never while loading (then the message log / box).
    ///     Never call it from a retry loop: only from the final give-up.
    /// </summary>
    [MessageTemplateFormatMethod("messageTemplate")]
    internal static void Block(string messageTemplate, params object?[]? propertyValues)
    {
        EmpLog.Write(LogEventLevel.Information, messageTemplate, propertyValues);
        var rendered = Render(messageTemplate, propertyValues);
        if (rendered is null) {
            return;
        }
        // (a Unity-destroyed dialog compares equal to null: a dialog killed without our callback frees the flag too)
        if (_block != null) {
            EmpLog.Debug("A notice dialog is already open, not stacking: {Text}", rendered);
            return;
        }
        if (CanShowDialog) {
            _block = Dialog.Ok(rendered, () => _block = null);
            return;
        }
        PopupInternal(messageTemplate, propertyValues);
    }

    private static bool OnMainThread => System.Threading.Thread.CurrentThread.ManagedThreadId == MainThread;

    // the map is up: a text over it and the message log work
    private static bool InGame => OnMainThread &&
                                  EClass.core?.IsGameStarted == true && EClass._zone is not null &&
                                  !EClass.core.game.isLoading;

    // the title screen has a UI too, a dialog works there; not while a game loads
    private static bool CanShowDialog => OnMainThread && EClass.core?.ui != null &&
                                         !(EClass.core.IsGameStarted && EClass.core.game.isLoading);

    private static string? Render(string messageTemplate, params object?[]? propertyValues)
    {
        if (!EmpLog.BindMessageTemplate(messageTemplate, propertyValues, out var template, out var captured)) {
            return null;
        }
        return template.Render(captured.ToDictionary(p => p.Name, p => p.Value)).Replace("\"", "");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Popup(LogEventLevel logLevel, string messageTemplate)
    {
        EmpLog.Write(logLevel, messageTemplate);
        if (Shown(logLevel)) {
            PopupInternal(messageTemplate);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    [MessageTemplateFormatMethod("messageTemplate")]
    internal static void Popup<T0>(LogEventLevel logLevel, string messageTemplate, T0 property0)
    {
        EmpLog.Write(logLevel, messageTemplate, property0);
        if (Shown(logLevel)) {
            PopupInternal(messageTemplate, property0);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    [MessageTemplateFormatMethod("messageTemplate")]
    internal static void Popup<T0, T1>(LogEventLevel logLevel, string messageTemplate, T0 property0, T1 property1)
    {
        EmpLog.Write(logLevel, messageTemplate, property0, property1);
        if (Shown(logLevel)) {
            PopupInternal(messageTemplate, property0, property1);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    [MessageTemplateFormatMethod("messageTemplate")]
    internal static void Popup(LogEventLevel logLevel, string messageTemplate, params object?[]? propertyValues)
    {
        EmpLog.Write(logLevel, messageTemplate, propertyValues);
        if (Shown(logLevel)) {
            PopupInternal(messageTemplate, propertyValues);
        }
    }

    /// <summary>
    ///     What a player sees: debug lines only in a debug build (they are in the log either way)
    /// </summary>
    private static bool Shown(LogEventLevel level)
    {
#if DEBUG
        return true;
#else
        return level > LogEventLevel.Debug;
#endif
    }

    [MessageTemplateFormatMethod("messageTemplate")]
    internal static void PopupInternal(string messageTemplate, params object?[]? propertyValues)
    {
        var rendered = Render(messageTemplate, propertyValues);
        if (rendered is null) {
            return;
        }

        // in game: a line of the game's message log, as the game tells things (user, 2026-10-22: the mod should look
        // like the game). On the title screen and while loading there is no log: the mod's own box
        // (a notice from another thread, e.g. the settings file watcher, keeps the box: the log crashed the game)
        if (InGame) {
            Msg.SayRaw(rendered.RemoveTagColor());
            return;
        }
        var truncation = rendered.Length > 150;
        var header = rendered;
        if (truncation) {
            var truncated = rendered.ToTruncateString(150);
            if (!ReferenceEquals(truncated, header)) {
                header = truncated;
            } else {
                truncation = false;
            }
        }

        using var progress = EGui.CreatePopupScoped(() => new(header));

        if (truncation) {
            var footer = rendered.RemoveTagColor()[150..];
            progress.Object.OnHover(p => GUILayout.Label(footer, p.GUIStyle));
        }
    }
}