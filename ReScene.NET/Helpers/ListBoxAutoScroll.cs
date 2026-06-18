using System.Collections.Specialized;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace ReScene.NET.Helpers;

/// <summary>
/// Attached behavior that keeps an <see cref="ItemsControl"/> (e.g. a log ListBox bound to an
/// <see cref="System.Collections.ObjectModel.ObservableCollection{T}"/>) scrolled to its newest
/// item as entries are appended — but only while the user is already at the bottom, so scrolling
/// up to read earlier entries is not yanked back down. Applied to the shared CompactLogListBox
/// style so every operation log (SRR/SRS creation, reconstruction, restore) auto-scrolls.
/// </summary>
public static class ListBoxAutoScroll
{
    /// <summary>
    /// When <see langword="true"/>, the target auto-scrolls to the last item whenever items are
    /// added (or the collection resets), provided the view is currently at the bottom.
    /// </summary>
    public static readonly DependencyProperty AutoScrollToEndProperty =
        DependencyProperty.RegisterAttached(
            "AutoScrollToEnd",
            typeof(bool),
            typeof(ListBoxAutoScroll),
            new PropertyMetadata(false, OnAutoScrollToEndChanged));

    public static bool GetAutoScrollToEnd(DependencyObject obj) =>
        (bool)obj.GetValue(AutoScrollToEndProperty);

    public static void SetAutoScrollToEnd(DependencyObject obj, bool value) =>
        obj.SetValue(AutoScrollToEndProperty, value);

    // Holds each control's subscription so it can be removed if the property is toggled off.
    // The weak key means the entry is collected with the control — no leak, no explicit unhook.
    private static readonly ConditionalWeakTable<ItemsControl, NotifyCollectionChangedEventHandler> _handlers = new();

    private static void OnAutoScrollToEndChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ItemsControl itemsControl || itemsControl.Items is not INotifyCollectionChanged incc)
        {
            return;
        }

        // Drop any prior subscription first so toggling the property never double-subscribes.
        if (_handlers.TryGetValue(itemsControl, out NotifyCollectionChangedEventHandler? previous))
        {
            incc.CollectionChanged -= previous;
            _handlers.Remove(itemsControl);
        }

        if (e.NewValue is true)
        {
            void Handler(object? _, NotifyCollectionChangedEventArgs args) => OnItemsChanged(itemsControl, args);
            _handlers.Add(itemsControl, Handler);
            incc.CollectionChanged += Handler;
        }
    }

    private static void OnItemsChanged(ItemsControl itemsControl, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action is not (NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Reset))
        {
            return;
        }

        // Collection mutations are marshalled onto the UI thread by the view-models; if that ever
        // changes, bail rather than touch the visual tree off-thread.
        if (!itemsControl.CheckAccess() || itemsControl.Items.Count == 0)
        {
            return;
        }

        // Only stick to the bottom if the user is already there. The scroll metrics here still
        // reflect the pre-layout state (the new item isn't measured yet), which is exactly what
        // lets us detect "was at the bottom" before the content grew.
        ScrollViewer? scrollViewer = FindScrollViewer(itemsControl);
        bool atBottom = scrollViewer is null
            || scrollViewer.ScrollableHeight <= 0
            || scrollViewer.VerticalOffset >= scrollViewer.ScrollableHeight - 1.0;

        if (!atBottom)
        {
            return;
        }

        // Defer until after the new item is laid out — scrolling before layout is a no-op.
        itemsControl.Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            () => FindScrollViewer(itemsControl)?.ScrollToBottom());
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer scrollViewer)
        {
            return scrollViewer;
        }

        int childCount = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < childCount; i++)
        {
            ScrollViewer? found = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }
}
