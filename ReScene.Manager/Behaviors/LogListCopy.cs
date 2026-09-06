using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.VisualTree;

namespace ReScene.Manager.Behaviors;

/// <summary>
/// Attached behavior giving a log <see cref="ListBox"/> a way to get its text out that does not
/// require a mouse drag-select: a context menu (Copy Line / Copy All Lines) plus the platform copy
/// chord. Applied to the shared <c>ListBox.logList</c> style, so every operation log pane in the app
/// gets it.
/// </summary>
/// <remarks>
/// <para>
/// "The line" is the selected item's text, falling back to the <b>focused</b> line when nothing is
/// selected. The fallback exists because tabbing into a log focuses a line without selecting it
/// (Avalonia only selects once an arrow key moves the selection), so without it a keyboard user
/// standing on a line would find Copy Line disabled and the copy chord dead. Copying never selects
/// the focused line: copying is a read, and a caret jump the user did not ask for is a surprise.
/// </para>
/// <para>
/// Right-click selection needs no code here: Avalonia's <c>ListBoxItem.OnPointerPressed</c> already
/// treats a right press like a left press for selection purposes (it calls
/// <c>ListBox.UpdateSelectionFromPointerEvent</c>, which selects the pressed item unless it is
/// already selected), so the item under the pointer is current by the time the menu opens.
/// <c>LogListCopyTests.RightPress_SelectsThePressedLine_SoTheMenuActsOnIt</c> guards that
/// assumption — if a future Avalonia drops it, Copy Line would quietly copy a stale line.
/// </para>
/// <para>
/// The menu is built per ListBox rather than declared once in the style: a <see cref="ContextMenu"/>
/// instance attaches to the control it is set on, so a single shared instance across the eleven log
/// panes would copy from whichever pane opened it last.
/// </para>
/// </remarks>
public static class LogListCopy
{
    /// <summary>
    /// When <see langword="true"/>, the target gets the copy context menu and honours the platform
    /// copy chord on its current line. Setting it back to <see langword="false"/> removes both.
    /// </summary>
    public static readonly AttachedProperty<bool> EnableProperty =
        AvaloniaProperty.RegisterAttached<ListBox, bool>("Enable", typeof(LogListCopy));

    public static bool GetEnable(ListBox obj) => obj.GetValue(EnableProperty);

    public static void SetEnable(ListBox obj, bool value) => obj.SetValue(EnableProperty, value);

    // Each list's own menu and its open-time state, held weakly so it dies with the ListBox — no
    // leak, no explicit unhook (same rationale as ListBoxAutoScroll's handler table). Tracking it
    // also means detaching only ever clears OUR menu, never one some other code put on the list.
    private static readonly ConditionalWeakTable<ListBox, MenuState> _menus = [];

    static LogListCopy()
    {
        EnableProperty.Changed.AddClassHandler<ListBox>(OnEnableChanged);
    }

    private static void OnEnableChanged(ListBox listBox, AvaloniaPropertyChangedEventArgs e)
    {
        // Always detach first so toggling the property never double-attaches.
        Detach(listBox);

        if (e.NewValue is true)
        {
            Attach(listBox);
        }
    }

    private static void Attach(ListBox listBox)
    {
        var copyLine = new MenuItem { Header = "Copy Line" };
        var copyAll = new MenuItem { Header = "Copy All Lines" };
        var menu = new ContextMenu();
        menu.Items.Add(copyLine);
        menu.Items.Add(copyAll);

        var state = new MenuState(menu, copyLine, copyAll);

        copyLine.Click += (_, _) => CopyToClipboard(listBox, LineToCopy(listBox, state.FocusedLine));
        copyAll.Click += (_, _) => CopyAllLines(listBox);

        // Enabled state is recomputed every time the menu opens: an inapplicable item is shown
        // disabled rather than hidden, so its absence never has to be explained.
        //
        // Opening is also where the focused line is captured, and it is the only place that can be:
        // opening the menu moves focus into the menu itself, so by the time Click fires the focused
        // element is a MenuItem. Opened deliberately reuses whatever Opening captured instead of
        // re-resolving, which would read that MenuItem and find no line.
        menu.Opening += (_, _) =>
        {
            state.FocusedLine = FocusedLine(listBox);
            RefreshMenuState(listBox, state);
        };

        // Opened covers the one path Opening does not: a programmatic ContextMenu.Open, which skips
        // Opening entirely. That path leaves FocusedLine null, so Copy Line falls back to the
        // selected line or is disabled.
        menu.Opened += (_, _) => RefreshMenuState(listBox, state);

        // Drop the capture with the menu so a later open can never act on a line the user has since
        // scrolled or arrowed away from.
        menu.Closed += (_, _) => state.FocusedLine = null;

        listBox.ContextMenu = menu;
        _menus.Add(listBox, state);

        // A handler rather than a XAML KeyBinding: the chord is whatever the platform calls Copy
        // (Ctrl+C, plus Ctrl+Insert on Windows/X11; Cmd+C on macOS), which is only knowable at
        // runtime from the TopLevel's platform settings.
        listBox.AddHandler(InputElement.KeyDownEvent, OnKeyDown);
    }

    private static void Detach(ListBox listBox)
    {
        listBox.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);

        if (!_menus.TryGetValue(listBox, out MenuState? state))
        {
            return;
        }

        state.Menu.Close();
        if (ReferenceEquals(listBox.ContextMenu, state.Menu))
        {
            listBox.ContextMenu = null;
        }

        _menus.Remove(listBox);
    }

    private static void RefreshMenuState(ListBox listBox, MenuState state)
    {
        state.CopyLine.IsEnabled = LineToCopy(listBox, state.FocusedLine) is not null;
        state.CopyAll.IsEnabled = listBox.ItemCount > 0;

        // Show the chord next to Copy Line. Resolved here rather than at attach time because the
        // list is not in the visual tree yet when the style setter runs, so there is no TopLevel
        // to ask.
        IReadOnlyList<KeyGesture> gestures = CopyGestures(listBox);
        state.CopyLine.InputGesture ??= gestures.Count > 0 ? gestures[0] : null;
    }

    private static void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || sender is not ListBox listBox)
        {
            return;
        }

        if (!CopyGestures(listBox).Any(gesture => gesture.Matches(e)))
        {
            return;
        }

        // Resolved live, unlike the menu path: at keypress time focus is still on the ListBoxItem,
        // because nothing has opened to take it away.
        if (LineToCopy(listBox, FocusedLine(listBox)) is not { } line)
        {
            // Nothing selected and nothing focused. Deliberately no copy-all fallback, and
            // deliberately left unhandled: silently replacing the clipboard with a
            // multi-thousand-line log is a destructive surprise, and the clipboard holds the
            // user's data, not ours. Copy All Lines is an explicit choice on the menu.
            return;
        }

        CopyToClipboard(listBox, line);
        e.Handled = true;
    }

    /// <summary>
    /// The line a copy should act on: the selected one, or — only when nothing is selected — the
    /// line passed in as the focus fallback. Returns <see langword="null"/> when there is neither.
    /// </summary>
    private static string? LineToCopy(ListBox listBox, string? focusedLine) =>
        listBox.SelectedItem is { } selected ? selected.ToString() : focusedLine;

    /// <summary>
    /// The text of the line that currently holds keyboard focus in <paramref name="listBox"/>, or
    /// <see langword="null"/> when focus is elsewhere.
    /// </summary>
    private static string? FocusedLine(ListBox listBox)
    {
        var topLevel = TopLevel.GetTopLevel(listBox);
        if ((topLevel?.FocusManager?.GetFocusedElement() as Visual)
            .FindAncestorOfType<ListBoxItem>(includeSelf: true) is not { } container)
        {
            return null;
        }

        // Focus can sit in a different log pane than the one being acted on — right-clicking pane A
        // while the caret is in pane B must not copy pane B's line.
        if (!ReferenceEquals(ItemsControl.ItemsControlFromItemContainer(container), listBox))
        {
            return null;
        }

        // Resolve to the data item, never the container: containers are recycled as the list
        // virtualizes and auto-scroll appends during a run, so a container held across even a short
        // gap can end up presenting a different line.
        return listBox.ItemFromContainer(container)?.ToString();
    }

    // Resolved off the visual's root rather than TopLevel.PlatformSettings: Avalonia 12 dropped
    // that property from TopLevel's public surface, and no longer guarantees a TopLevel sits at the
    // visual root at all. GetPlatformSettings() answers from whatever root is actually there.
    private static IReadOnlyList<KeyGesture> CopyGestures(ListBox listBox) =>
        listBox.GetPlatformSettings()?.HotkeyConfiguration.Copy ?? [];

    private static void CopyAllLines(ListBox listBox)
    {
        // Snapshot the bound items, never the realized containers: these lists virtualize, so only
        // the visible rows have containers and a container walk would copy a fraction of the log.
        // Taking the snapshot up front also keeps a still-running operation's appends from
        // mutating the collection mid-read — the same reason
        // OperationViewModelBase.SaveLogToFileAsync snapshots before exporting.
        object?[] snapshot = [.. listBox.Items];

        // Not dead code despite the item being disabled on an empty log: a run can clear LogEntries
        // between the menu opening and the click. SetTextAsync("") is documented as equivalent to
        // ClearAsync, so without this a copy that found nothing would wipe the user's clipboard.
        if (snapshot.Length == 0)
        {
            return;
        }

        CopyToClipboard(listBox, BuildCopyAllText(snapshot));
    }

    /// <summary>
    /// Joins the log lines into the single blob that Copy All Lines puts on the clipboard, in list
    /// order and separated by the platform newline.
    /// </summary>
    internal static string BuildCopyAllText(IReadOnlyList<object?> lines) =>
        string.Join(Environment.NewLine, lines.Select(static line => line?.ToString() ?? string.Empty));

    // Avalonia's Clipboard is async and owned by the TopLevel (unlike WPF's static
    // Clipboard.SetText); fire-and-forget it here, guarded against a headless/detached TopLevel —
    // same idiom as BruteForceProgressWindow.CopyToClipboard. A null line means the menu item was
    // clicked with nothing to copy, which leaves the clipboard alone.
    private static void CopyToClipboard(ListBox listBox, string? text)
    {
        if (text is not null)
        {
            _ = TopLevel.GetTopLevel(listBox)?.Clipboard?.SetTextAsync(text);
        }
    }

    /// <summary>
    /// One log list's menu, its two items, and the line captured while the menu was opening.
    /// </summary>
    private sealed class MenuState(ContextMenu menu, MenuItem copyLine, MenuItem copyAll)
    {
        public ContextMenu Menu { get; } = menu;

        public MenuItem CopyLine { get; } = copyLine;

        public MenuItem CopyAll { get; } = copyAll;

        /// <summary>
        /// Text of the line that held focus when the menu began opening, used only when nothing is
        /// selected. Null when there was none, when the menu was opened programmatically (which
        /// skips Opening), or once the menu has closed.
        /// </summary>
        public string? FocusedLine { get; set; }
    }
}
