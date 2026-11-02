#if DEBUG
using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ElinTogether.Emp;

/// <summary>
///     Test bridge only: mouse clicks and keys given to the game where it reads its input (EInput), so a test plays
///     like a player (the game builds the plan under the pointer, attacks, casts the selected spell, fires) while
///     the bench windows stay minimized and never take the focus. The pointer is put on a map cell, not on the
///     screen: what the OS cursor does is ignored while something is queued
/// </summary>
[HarmonyPatch]
[AlwaysPatched]
public static class InputInject
{
    private enum Kind
    {
        Point,
        Down,
        Up,
        Release,
        Hotkey,
        Action,
    }

    private readonly record struct Step(Kind Kind, int X = 0, int Z = 0, bool Right = false, int Hotkey = -1,
        EAction Action = EAction.None);

    private static readonly Queue<Step> Steps = new();
    private static (int X, int Z)? _pointer;

    // frames the pointer stays on its cell after the last step (the game reads it again to show the plan)
    private static int _linger;

    /// <summary>
    ///     Something is still to be given to the game
    /// </summary>
    public static bool Busy => Steps.Count > 0;

    /// <summary>
    ///     Clicks the game took, for the tests to count
    /// </summary>
    public static int Clicks { get; private set; }

    /// <summary>
    ///     The pointer on that cell, then a click of the left (attack, walk, talk) or right (the selected hotbar
    ///     item: a spell, a thrown or held thing) button
    /// </summary>
    public static void Click(int x, int z, bool right = false)
    {
        Steps.Enqueue(new(Kind.Point, x, z));
        Steps.Enqueue(new(Kind.Point, x, z));
        Steps.Enqueue(new(Kind.Down, x, z, right));
        Steps.Enqueue(new(Kind.Up, x, z, right));
        Steps.Enqueue(new(Kind.Release, x, z, right));
    }

    /// <summary>
    ///     The pointer on that cell and nothing else (the game's keys act on the cell under it: AutoCombat)
    /// </summary>
    public static void Point(int x, int z)
    {
        Steps.Enqueue(new(Kind.Point, x, z));
        Steps.Enqueue(new(Kind.Point, x, z));
    }

    /// <summary>
    ///     A number key of the hotbar, 1 to 9
    /// </summary>
    public static void Hotbar(int number)
    {
        Steps.Enqueue(new(Kind.Hotkey, Hotkey: number - 1));
    }

    /// <summary>
    ///     A key of the game by its action: Fire, Wait, EmptyHand, AutoCombat...
    /// </summary>
    public static void Key(string action)
    {
        Steps.Enqueue(new(Kind.Action, Action: (EAction)Enum.Parse(typeof(EAction), action, true)));
    }

    public static void Clear()
    {
        Steps.Clear();
        _pointer = null;
        _linger = 0;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(EInput), nameof(EInput.Update))]
    internal static void OnInput()
    {
        if (Steps.Count == 0 && _linger <= 0) {
            return;
        }

        if (!Application.isFocused) {
            // (the game does not read keys when it is not in front: what was left from before is not repeated)
            EInput.hotkey = -1;
            EInput.action = EAction.None;
        }

        if (_pointer is { } at) {
            // a test left before its clicks were taken, and the player went to another map: dropped
            if (!new Point(at.X, at.Z).IsValid) {
                Clear();
                return;
            }

            Aim(at.X, at.Z);
        }

        if (Steps.Count == 0) {
            _linger--;
            return;
        }

        if (EInput.skipFrame > 0 || EInput.haltInput) {
            // (a game that is not in front returns before counting its skipped frames down: it would wait forever)
            if (!Application.isFocused && EInput.skipFrame > 0) {
                EInput.skipFrame--;
            }

            return;
        }

        var step = Steps.Peek();
        var button = step.Right ? EInput.rightMouse : EInput.leftMouse;
        switch (step.Kind) {
            case Kind.Point:
                if (!new Point(step.X, step.Z).IsValid) {
                    Clear();
                    return;
                }

                _pointer = (step.X, step.Z);
                Aim(step.X, step.Z);
                if (EClass.scene.actionMode is AM_Adv adv) {
                    adv.UpdatePlans();
                }

                break;
            case Kind.Down:
                // a click is taken when the player can act (the game ignores it during a turn being played)
                if (!EClass.pc.HasNoGoal) {
                    return;
                }

                button.down = button.pressing = button.usedMouse = true;
                button.up = button.clicked = button.consumed = button.wasConsumed = false;
                button.pressedTimer = 0f;
                EInput.dragStartPos = EInput.mpos;
                break;
            case Kind.Up:
                button.down = button.pressing = false;
                button.up = button.clicked = true;
                button.timeSinceLastClick = 0f;
                Clicks++;
                break;
            case Kind.Release:
                button.down = button.up = button.pressing = button.clicked = false;
                break;
            case Kind.Hotkey:
                EInput.hotkey = step.Hotkey;
                break;
            case Kind.Action:
                // a key is taken when the player can act, like a click (AM_Adv ignores AutoCombat during a turn being played)
                if (!EClass.player.CanAcceptInput()) {
                    return;
                }

                EInput.action = step.Action;
                break;
        }

        Steps.Dequeue();
        _linger = 30;
    }

    /// <summary>
    ///     The game asks the OS cursor whether it is over a window: not while a test points at the map
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(UI), nameof(UI.RefreshActiveState))]
    internal static void OnUiState(UI __instance)
    {
        if (Steps.Count > 0 || _linger > 0) {
            __instance.isPointerOverUI = false;
        }
    }

    private static void Aim(int x, int z)
    {
        var world = new Point(x, z).PositionCenter();
        EInput.mpos = Camera.main.WorldToScreenPoint(world);
        var mw = Camera.main.ScreenToWorldPoint(EInput.mpos);
        mw.z = 0f;
        EInput.mposWorld = mw;
        Scene.HitPoint.Set(x, z);
    }
}
#endif
