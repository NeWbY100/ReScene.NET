using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ReScene.Manager.Behaviors;

namespace ReScene.Manager.Tests;

/// <summary>
/// Covers <see cref="LogListCopy"/>: the shared <c>ListBox.logList</c> style must wire the behavior
/// up (the same failure mode as F3, where a ported attached property had no style setting it), the
/// menu must carry both copy items with open-time enabled state, and both the menu and the platform
/// copy chord must put the right text on the clipboard. Avalonia's headless clipboard is a real
/// in-memory store, so the copies are asserted end to end rather than mocked.
/// </summary>
public class LogListCopyTests
{
    private static (Window Window, ListBox List) ShowLog(params string[] lines)
    {
        var list = new ListBox
        {
            Classes = { "logList" },
            ItemsSource = new ObservableCollection<string>(lines),
            Width = 220,
            Height = 120,
        };

        var window = new Window { Width = 260, Height = 160, Content = list };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, list);
    }

    private static MenuItem CopyLine(ListBox list) => (MenuItem)list.ContextMenu!.Items[0]!;

    private static MenuItem CopyAll(ListBox list) => (MenuItem)list.ContextMenu!.Items[1]!;

    /// <summary>
    /// Opens the menu the way a keyboard user does — the Apps/Menu key, which
    /// <c>Control.OnKeyUp</c> turns into the same ContextRequested that a right-click raises, so
    /// this exercises the real open path (and therefore the open-time enablement) rather than
    /// calling <c>ContextMenu.Open</c> behind the framework's back. Focus goes on a ListBoxItem
    /// because that is where it lands at runtime: the ListBox itself is not focusable, only its
    /// items are, and focusing one does not select it.
    /// </summary>
    private static void OpenMenuWithAppsKey(Window window, ListBox list, int focusIndex = 0)
    {
        ((ListBoxItem)list.ContainerFromIndex(focusIndex)!).Focus();
        Dispatcher.UIThread.RunJobs();
        window.KeyPressQwerty(PhysicalKey.ContextMenu, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.ContextMenu, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// Right-clicks the blank strip at the bottom of the list, below any lines. Reaches the menu
    /// while leaving selection and focus untouched, and is the only route to it on an empty log
    /// (which has no item to put keyboard focus on).
    /// </summary>
    private static void OpenMenuBelowTheLines(Window window, ListBox list)
    {
        Point spot = list.TranslatePoint(new Point(list.Bounds.Width / 2, list.Bounds.Height - 6), window)!.Value;
        window.MouseDown(spot, MouseButton.Right);
        window.MouseUp(spot, MouseButton.Right);
        Dispatcher.UIThread.RunJobs();
    }

    private static string? ReadClipboard(Window window)
    {
        Task<string?> read = window.Clipboard!.TryGetTextAsync();
        Dispatcher.UIThread.RunJobs();
        Assert.True(read.IsCompleted, "the headless clipboard completes synchronously");
        return read.Result;
    }

    private static void Click(MenuItem item)
    {
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// Raises the platform copy chord on the list, as it arrives at runtime when the key bubbles up
    /// from the focused ListBoxItem. Returns the args so callers can assert on Handled.
    /// </summary>
    private static KeyEventArgs PressCopyChord(ListBox list)
    {
        var args = new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.C,
            KeyModifiers = KeyModifiers.Control,
            Source = list,
        };
        list.RaiseEvent(args);
        Dispatcher.UIThread.RunJobs();
        return args;
    }

    /// <summary>Two log panes sharing one window, so both are in the same focus scope.</summary>
    private static (Window Window, ListBox First, ListBox Second) ShowTwoLogs()
    {
        ListBox Pane(params string[] lines) => new()
        {
            Classes = { "logList" },
            ItemsSource = new ObservableCollection<string>(lines),
            Width = 220,
            Height = 100,
        };

        ListBox first = Pane("pane A line 1", "pane A line 2");
        ListBox second = Pane("pane B line 1", "pane B line 2");

        var panel = new StackPanel();
        panel.Children.Add(first);
        panel.Children.Add(second);

        var window = new Window { Width = 260, Height = 260, Content = panel };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, first, second);
    }

    // ── Style wire-up ────────────────────────────────────────────────

    [AvaloniaFact]
    public void LogListStyle_AttachesCopyMenu_WithBothItems_NoBindingErrors()
    {
        using var sink = new BindingErrorSink();
        (Window _, ListBox list) = ShowLog("line 1", "line 2");

        Assert.True(LogListCopy.GetEnable(list));
        ContextMenu menu = Assert.IsType<ContextMenu>(list.ContextMenu);
        Assert.Collection(
            menu.Items.Cast<MenuItem>(),
            item => Assert.Equal("Copy Line", item.Header),
            item => Assert.Equal("Copy All Lines", item.Header));

        Assert.Empty(sink.Messages);
    }

    /// <summary>
    /// Each log pane owns its menu. A menu shared across panes (e.g. one instance handed out by the
    /// style) would copy from whichever pane opened it last.
    /// </summary>
    [AvaloniaFact]
    public void EachLogList_GetsItsOwnMenu()
    {
        (Window _, ListBox first) = ShowLog("first pane");
        (Window _, ListBox second) = ShowLog("second pane");

        Assert.NotNull(first.ContextMenu);
        Assert.NotNull(second.ContextMenu);
        Assert.NotSame(first.ContextMenu, second.ContextMenu);
    }

    [AvaloniaFact]
    public void DisablingTheBehavior_RemovesTheMenuAndTheChord()
    {
        (Window window, ListBox list) = ShowLog("line 1", "line 2");
        list.SelectedIndex = 0;

        LogListCopy.SetEnable(list, false);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(list.ContextMenu);

        var args = new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.C,
            KeyModifiers = KeyModifiers.Control,
            Source = list,
        };
        list.RaiseEvent(args);
        Dispatcher.UIThread.RunJobs();

        Assert.False(args.Handled);
        Assert.Null(ReadClipboard(window));
    }

    // ── Open-time enabled state ──────────────────────────────────────

    [AvaloniaFact]
    public void MenuOpenedWithSelection_EnablesBothItems_AndShowsTheCopyChord()
    {
        (Window window, ListBox list) = ShowLog("line 1", "line 2");
        list.SelectedIndex = 1;

        OpenMenuWithAppsKey(window, list, focusIndex: 1);

        Assert.True(list.ContextMenu!.IsOpen);
        Assert.True(CopyLine(list).IsEnabled);
        Assert.True(CopyAll(list).IsEnabled);

        // The gesture shown next to Copy Line is the platform's, not a hardcoded Ctrl+C.
        KeyGesture expected = window.GetPlatformSettings()!.HotkeyConfiguration.Copy[0];
        Assert.Equal(expected, CopyLine(list).InputGesture);
    }

    /// <summary>
    /// The state a keyboard user lands in by tabbing into a log: a line has focus but nothing is
    /// selected (Avalonia only selects once an arrow key moves the selection). Copy Line stays
    /// applicable and acts on the focused line — and copying does not select it, because a copy is
    /// a read and a caret jump nobody asked for is a surprise.
    /// </summary>
    [AvaloniaFact]
    public void MenuOpenedOnAFocusedButUnselectedLine_CopiesThatLine_WithoutSelectingIt()
    {
        (Window window, ListBox list) = ShowLog("line 1", "line 2", "line 3");

        OpenMenuWithAppsKey(window, list, focusIndex: 1);

        Assert.Null(list.SelectedItem);
        Assert.True(CopyLine(list).IsEnabled);
        Assert.True(CopyAll(list).IsEnabled);

        // Activate through the REAL keyboard flow (arrow onto the item, Enter) instead of raising
        // ClickEvent on the still-open menu: the capture-at-Opening design rests on Avalonia
        // raising Click BEFORE Closed clears the capture. If a future Avalonia reversed that
        // order, a raised-Click test would stay green while real activations silently copied the
        // wrong thing — the same silent-wrong-data class the right-press test guards.
        window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("line 2", ReadClipboard(window));
        Assert.Null(list.SelectedItem);
    }

    /// <summary>
    /// Nothing selected and nothing focused — reachable by right-clicking the blank area below a
    /// short log, which selects nothing and focuses nothing.
    /// </summary>
    [AvaloniaFact]
    public void MenuOpenedWithNoSelectionAndNoFocus_DisablesCopyLine_ButLeavesCopyAllEnabled()
    {
        (Window window, ListBox list) = ShowLog("line 1", "line 2");

        OpenMenuBelowTheLines(window, list);

        Assert.True(list.ContextMenu!.IsOpen);
        Assert.Null(list.SelectedItem);

        // Disabled rather than hidden: an item that vanishes cannot explain why it is gone.
        Assert.False(CopyLine(list).IsEnabled);
        Assert.True(CopyAll(list).IsEnabled);
    }

    [AvaloniaFact]
    public void MenuOpenedOnAnEmptyLog_DisablesBothItems()
    {
        (Window window, ListBox list) = ShowLog();

        OpenMenuBelowTheLines(window, list);

        Assert.True(list.ContextMenu!.IsOpen);
        Assert.False(CopyLine(list).IsEnabled);
        Assert.False(CopyAll(list).IsEnabled);
    }

    /// <summary>
    /// The focus capture belongs to one opening of the menu. Closing must drop it, and the Opened
    /// refresh must reuse whatever Opening captured rather than re-resolving focus — a programmatic
    /// open skips Opening, so it has no capture and falls back to selected-or-disabled.
    /// </summary>
    [AvaloniaFact]
    public void ProgrammaticOpen_DoesNotResolveFocus_SoAStaleCaptureCannotLeakIntoIt()
    {
        (Window window, ListBox list) = ShowLog("line 1", "line 2");
        var container = (ListBoxItem)list.ContainerFromIndex(1)!;

        OpenMenuWithAppsKey(window, list, focusIndex: 1);
        Assert.True(CopyLine(list).IsEnabled);

        list.ContextMenu!.Close();
        Dispatcher.UIThread.RunJobs();

        // Closing hands focus back to the line, so focus IS resolvable here. That is what makes the
        // assertion below meaningful: Copy Line comes out disabled only because Opened reuses the
        // (now cleared) capture instead of re-resolving the focused line.
        Assert.Same(container, window.FocusManager?.GetFocusedElement());

        list.ContextMenu.Open(list);
        Dispatcher.UIThread.RunJobs();

        Assert.True(list.ContextMenu.IsOpen);
        Assert.False(CopyLine(list).IsEnabled);
    }

    // ── Copying ──────────────────────────────────────────────────────

    [Fact]
    public void BuildCopyAllText_JoinsEveryLineInOrder_SeparatedByTheEnvironmentNewLine()
    {
        string text = LogListCopy.BuildCopyAllText(["first", "second", "third"]);

        Assert.Equal($"first{Environment.NewLine}second{Environment.NewLine}third", text);
    }

    [AvaloniaFact]
    public void CopyLine_PutsTheSelectedLineOnTheClipboard()
    {
        (Window window, ListBox list) = ShowLog("line 1", "line 2", "line 3");
        list.SelectedIndex = 1;

        Click(CopyLine(list));

        Assert.Equal("line 2", ReadClipboard(window));
    }

    /// <summary>
    /// The log lists virtualize, so only a handful of rows ever have containers. Copy All must read
    /// the bound items: seeded with far more lines than fit the 120px viewport, it still has to
    /// produce every one.
    /// </summary>
    [AvaloniaFact]
    public void CopyAllLines_CopiesEveryLine_IncludingUnrealizedOnes()
    {
        string[] lines = [.. Enumerable.Range(1, 200).Select(i => $"line {i}")];
        (Window window, ListBox list) = ShowLog(lines);

        Click(CopyAll(list));

        Assert.Equal(string.Join(Environment.NewLine, lines), ReadClipboard(window));
    }

    /// <summary>
    /// The log can be cleared between the menu opening and the click landing, so Copy All has to
    /// cope with finding nothing. It must leave the clipboard alone rather than set it to the empty
    /// string, which Avalonia documents as equivalent to clearing it.
    /// </summary>
    [AvaloniaFact]
    public void CopyAllLines_OnALogClearedWhileTheMenuWasOpen_LeavesTheClipboardAlone()
    {
        (Window window, ListBox list) = ShowLog("line 1", "line 2");
        list.SelectedIndex = 0;
        Click(CopyLine(list));
        Assert.Equal("line 1", ReadClipboard(window));

        ((ObservableCollection<string>)list.ItemsSource!).Clear();
        Dispatcher.UIThread.RunJobs();

        Click(CopyAll(list));

        Assert.Equal("line 1", ReadClipboard(window));
    }

    // ── The copy chord ───────────────────────────────────────────────

    [AvaloniaFact]
    public void CopyChord_WithASelectedLine_CopiesIt_AndMarksTheKeyHandled()
    {
        (Window window, ListBox list) = ShowLog("line 1", "line 2");
        list.SelectedIndex = 0;

        KeyEventArgs args = PressCopyChord(list);

        Assert.True(args.Handled);
        Assert.Equal("line 1", ReadClipboard(window));
    }

    /// <summary>
    /// The chord resolves focus live, unlike the menu (nothing has opened to take focus away), so a
    /// user who has only tabbed into the log can copy the line they are standing on. Copying must
    /// not select it.
    /// </summary>
    [AvaloniaFact]
    public void CopyChord_OnAFocusedButUnselectedLine_CopiesIt_WithoutSelectingIt()
    {
        (Window window, ListBox list) = ShowLog("line 1", "line 2", "line 3");
        ((ListBoxItem)list.ContainerFromIndex(2)!).Focus();
        Dispatcher.UIThread.RunJobs();
        Assert.Null(list.SelectedItem);

        KeyEventArgs args = PressCopyChord(list);

        Assert.True(args.Handled);
        Assert.Equal("line 3", ReadClipboard(window));
        Assert.Null(list.SelectedItem);
    }

    /// <summary>
    /// Precedence: the focused line is a fallback for having no selection, never an override of one.
    /// Arrowing moves focus and selection together, so the two only diverge transiently — but when
    /// they do, the selection is what the user last committed to.
    /// </summary>
    [AvaloniaFact]
    public void CopyChord_PrefersTheSelectedLineOverTheFocusedOne()
    {
        (Window window, ListBox list) = ShowLog("line 1", "line 2", "line 3");
        list.SelectedIndex = 2;
        ((ListBoxItem)list.ContainerFromIndex(0)!).Focus();
        Dispatcher.UIThread.RunJobs();

        PressCopyChord(list);

        Assert.Equal("line 3", ReadClipboard(window));
    }

    /// <summary>
    /// The focus fallback is scoped to the pane being acted on. Twelve log panes share this
    /// behavior and several are on screen together, so focus parked in one must never leak a line
    /// into another pane's copy.
    /// </summary>
    [AvaloniaFact]
    public void CopyChord_IgnoresFocusSittingInADifferentLogPane()
    {
        (Window window, ListBox first, ListBox second) = ShowTwoLogs();
        ((ListBoxItem)second.ContainerFromIndex(0)!).Focus();
        Dispatcher.UIThread.RunJobs();
        Assert.Null(first.SelectedItem);

        KeyEventArgs args = PressCopyChord(first);

        Assert.False(args.Handled);
        Assert.Null(ReadClipboard(window));
    }

    /// <summary>
    /// Nothing selected and nothing focused means no copy — and crucially no copy-all fallback:
    /// silently replacing the clipboard with the whole log is a destructive surprise. The key is
    /// left unhandled so it stays available to anything else that wants it.
    /// </summary>
    [AvaloniaFact]
    public void CopyChord_WithNoSelectionAndNoFocus_CopiesNothing_AndLeavesTheKeyUnhandled()
    {
        (Window window, ListBox list) = ShowLog("line 1", "line 2");
        Assert.Null(list.SelectedItem);

        KeyEventArgs args = PressCopyChord(list);

        Assert.False(args.Handled);
        Assert.Null(ReadClipboard(window));
    }

    // ── Dismissing the menu ──────────────────────────────────────────

    /// <summary>
    /// Escape must both close the menu and hand focus back to the line it was opened from —
    /// otherwise a keyboard user who backs out of the menu is left with focus nowhere, and has to
    /// tab all the way back into the log.
    /// </summary>
    [AvaloniaFact]
    public void EscapeOnTheOpenMenu_ClosesIt_AndReturnsFocusToTheLine()
    {
        (Window window, ListBox list) = ShowLog("line 1", "line 2", "line 3");
        var container = (ListBoxItem)list.ContainerFromIndex(1)!;

        OpenMenuWithAppsKey(window, list, focusIndex: 1);
        Assert.True(list.ContextMenu!.IsOpen);

        // Opening the menu takes focus off the line — without this the assertion below would hold
        // trivially and prove nothing about focus being handed back.
        Assert.NotSame(container, window.FocusManager?.GetFocusedElement());

        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.False(list.ContextMenu.IsOpen);
        Assert.Same(container, window.FocusManager?.GetFocusedElement());
    }

    // ── Right-click selection parity ─────────────────────────────────

    /// <summary>
    /// Guards the framework assumption <see cref="LogListCopy"/> is built on: Avalonia's own
    /// <c>ListBoxItem</c> selects the item under a right press, so the behavior deliberately does
    /// not hook PointerPressed itself. If a future Avalonia stops doing this, Copy Line would
    /// quietly copy whichever line happened to be selected earlier — a wrong-data bug with no
    /// visible symptom, which is why the assumption is pinned here rather than left implicit.
    /// </summary>
    [AvaloniaFact]
    public void RightPress_SelectsThePressedLine_SoTheMenuActsOnIt()
    {
        (Window window, ListBox list) = ShowLog("line 1", "line 2", "line 3");
        Assert.Null(list.SelectedItem);

        var container = (ListBoxItem)list.ContainerFromIndex(1)!;
        Point center = container.TranslatePoint(
            new Point(container.Bounds.Width / 2, container.Bounds.Height / 2), window)!.Value;

        window.MouseDown(center, MouseButton.Right);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("line 2", list.SelectedItem);
    }
}
