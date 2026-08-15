using System.Runtime.Versioning;
using Avalonia.Controls;
using Avalonia.Platform;
using ReScene.App.Core.Services;
using ReScene.App.Core.ViewModels;
using ReScene.Manager.Interop;

namespace ReScene.Manager.Services;

/// <summary>
/// Windows-only consumer of <see cref="MainWindowViewModel"/>'s framework-neutral taskbar-progress
/// signals, restoring the WPF head's <c>TaskbarItemInfo</c> behavior via the <see cref="ITaskbarList3"/>
/// COM interface. Created through <see cref="TryCreate"/>, which returns <c>null</c> off Windows, on a
/// non-Win32 (headless) window, or if COM activation fails — so nothing here is reachable in unit tests
/// or on other platforms. Every COM call is failure-swallowed and latches the wrapper disabled on the
/// first error: a taskbar-progress hiccup must never crash the app or surface to the user.
/// </summary>
internal sealed class WindowsTaskbarProgress
{
    // CLSID_TaskbarList (shobjidl_core.h) — the coclass that implements ITaskbarList3.
    private static readonly Guid _taskbarListClsid = new("56FDF344-FD6D-11d0-958A-006097C9A090");

    private readonly nint _hwnd;
    private readonly ITaskbarList3 _taskbar;
    private bool _disabled;

    [SupportedOSPlatform("windows")]
    private WindowsTaskbarProgress(nint hwnd, ITaskbarList3 taskbar)
    {
        _hwnd = hwnd;
        _taskbar = taskbar;
    }

    /// <summary>
    /// Attempts to create a wrapper bound to <paramref name="window"/>'s native HWND. Returns
    /// <c>null</c> unless the process is on Windows, the window exposes a real Win32 handle
    /// (<see cref="IPlatformHandle.HandleDescriptor"/> == <c>"HWND"</c> — the headless platform reports
    /// <c>"STUB"</c>, so headless windows are excluded), and COM activation + <c>HrInit</c> succeed.
    /// </summary>
    internal static WindowsTaskbarProgress? TryCreate(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        IPlatformHandle? handle = window.TryGetPlatformHandle();
        if (handle is null || handle.HandleDescriptor != "HWND" || handle.Handle == nint.Zero)
        {
            return null;
        }

        try
        {
            var comType = Type.GetTypeFromCLSID(_taskbarListClsid);
            if (comType is null || Activator.CreateInstance(comType) is not ITaskbarList3 taskbar)
            {
                return null;
            }

            taskbar.HrInit();
            return new WindowsTaskbarProgress(handle.Handle, taskbar);
        }
        catch
        {
            // Any COM/activation failure: silently disable the feature.
            return null;
        }
    }

    /// <summary>
    /// Maps App.Core's framework-neutral <see cref="TaskbarProgressState"/> onto the native
    /// <see cref="TaskbarProgressFlags"/>. Pure; unknown values map to
    /// <see cref="TaskbarProgressFlags.NoProgress"/>.
    /// </summary>
    internal static TaskbarProgressFlags ToFlags(TaskbarProgressState state) => state switch
    {
        TaskbarProgressState.None => TaskbarProgressFlags.NoProgress,
        TaskbarProgressState.Normal => TaskbarProgressFlags.Normal,
        TaskbarProgressState.Indeterminate => TaskbarProgressFlags.Indeterminate,
        TaskbarProgressState.Error => TaskbarProgressFlags.Error,
        TaskbarProgressState.Paused => TaskbarProgressFlags.Paused,
        _ => TaskbarProgressFlags.NoProgress,
    };

    /// <summary>
    /// Pushes <paramref name="state"/> and, for the value-bearing states (Normal/Error/Paused),
    /// <paramref name="value"/> (clamped to [0,1]) to the taskbar button.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal void Update(TaskbarProgressState state, double value)
    {
        if (_disabled)
        {
            return;
        }

        TaskbarProgressFlags flags = ToFlags(state);

        try
        {
            _taskbar.SetProgressState(_hwnd, flags);

            // Indeterminate/NoProgress ignore the value; the determinate states drive the bar.
            if (flags is TaskbarProgressFlags.Normal or TaskbarProgressFlags.Error or TaskbarProgressFlags.Paused)
            {
                double clamped = Math.Clamp(value, 0.0, 1.0);
                _taskbar.SetProgressValue(_hwnd, (ulong)(clamped * 1000), 1000);
            }
        }
        catch
        {
            // Latch off on first failure so we stop poking a broken COM object.
            _disabled = true;
        }
    }

    /// <summary>Clears the progress indicator (used on window close).</summary>
    [SupportedOSPlatform("windows")]
    internal void Clear()
    {
        if (_disabled)
        {
            return;
        }

        try
        {
            _taskbar.SetProgressState(_hwnd, TaskbarProgressFlags.NoProgress);
        }
        catch
        {
            _disabled = true;
        }
    }
}
