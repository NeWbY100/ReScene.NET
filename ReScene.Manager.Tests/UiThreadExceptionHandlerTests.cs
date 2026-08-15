using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using ReScene.Manager.Helpers;

namespace ReScene.Manager.Tests;

/// <summary>
/// Tests for <see cref="UiThreadExceptionHandler"/> (F1 — the restored WPF
/// <c>DispatcherUnhandledException</c> equivalent). Two layers: pure unit tests of the latch / handled
/// flag / dialog-fault swallowing, and one headless integration test proving the real handler,
/// subscribed to <see cref="Dispatcher.UIThread"/>'s <c>UnhandledException</c>, both observes a
/// UI-thread exception and suppresses it (the app survives) — the live desktop path in miniature.
/// </summary>
public class UiThreadExceptionHandlerTests
{
    [Fact]
    public void Handle_LogsShowsDialog_AndReturnsHandled()
    {
        var calls = new List<(string Title, string Message)>();
        var handler = new UiThreadExceptionHandler((t, m) => calls.Add((t, m)));

        bool handled = handler.Handle(new InvalidOperationException("boom"));

        Assert.True(handled);
        (string Title, string Message) = Assert.Single(calls);
        Assert.Equal("Unexpected error", Title);
        Assert.Contains("boom", Message, StringComparison.Ordinal);
        Assert.Contains("try to continue", Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Handle_WhenDialogThrows_SwallowsAndResetsLatch()
    {
        int shown = 0;
        var handler = new UiThreadExceptionHandler((_, _) =>
        {
            shown++;
            throw new InvalidOperationException("dialog itself faulted");
        });

        // A faulting dialog must not propagate, and the latch must reset so a LATER exception still
        // gets its dialog.
        Assert.True(handler.Handle(new Exception("first")));
        Assert.True(handler.Handle(new Exception("second")));
        Assert.Equal(2, shown);
    }

    [Fact]
    public void Handle_Reentrant_SkipsSecondDialog()
    {
        int shown = 0;
        UiThreadExceptionHandler? handler = null;
        handler = new UiThreadExceptionHandler((_, _) =>
        {
            shown++;
            if (shown == 1)
            {
                // Simulate the dialog (running on the faulted UI thread) re-entering the handler.
                Assert.True(handler!.Handle(new Exception("reentrant")));
            }
        });

        Assert.True(handler.Handle(new Exception("outer")));
        Assert.Equal(1, shown); // the reentrant call was latched out — no second dialog
    }

    [AvaloniaFact]
    public void RealHandler_OnDispatcher_ObservesAndSuppressesUiThreadException()
    {
        var errors = new List<(string Title, string Message)>();
        var handler = new UiThreadExceptionHandler((t, m) => errors.Add((t, m)));

        void Subscription(object? _, DispatcherUnhandledExceptionEventArgs e) =>
            e.Handled = handler.Handle(e.Exception);

        Dispatcher.UIThread.UnhandledException += Subscription;
        try
        {
            // Post uses throwOnUiThread: true, so an escaping exception is routed through the
            // dispatcher's UnhandledException event. Our handler marks it Handled, so RunJobs must NOT
            // rethrow (the app process survives).
            Dispatcher.UIThread.Post(() => throw new InvalidOperationException("kaboom"));
            Dispatcher.UIThread.RunJobs();
        }
        finally
        {
            Dispatcher.UIThread.UnhandledException -= Subscription;
        }

        (string Title, string Message) = Assert.Single(errors);
        Assert.Equal("Unexpected error", Title);
        Assert.Contains("kaboom", Message, StringComparison.Ordinal);
    }
}
