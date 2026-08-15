using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ReScene.Manager.Interop;

/// <summary>
/// Managed declaration of the Win32 <c>ITaskbarList3</c> COM interface used to drive the taskbar
/// button's progress indicator (the Avalonia equivalent of WPF's <c>TaskbarItemInfo</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>CRITICAL — vtable order.</b> This is a flattened <see cref="ComImportAttribute"/> interface, so
/// the CLR maps declared methods to native vtable slots strictly in source order. ITaskbarList3
/// inherits ITaskbarList2 which inherits ITaskbarList, so EVERY inherited method must be declared,
/// in exact vtable order, before the ITaskbarList3 methods we actually call — otherwise a call lands
/// on the wrong native slot. The ordering below is taken from the Windows SDK
/// <c>shobjidl_core.h</c> (interface definitions for ITaskbarList / ITaskbarList2 / ITaskbarList3):
/// </para>
/// <list type="number">
///   <item>ITaskbarList: HrInit, AddTab, DeleteTab, ActivateTab, SetActiveAlt</item>
///   <item>ITaskbarList2: MarkFullscreenWindow</item>
///   <item>ITaskbarList3: SetProgressValue, SetProgressState (trailing ITaskbarList3 methods —
///     RegisterTab, UnregisterTab, SetTabOrder, … — are never called, so their slots are omitted)</item>
/// </list>
/// </remarks>
[SupportedOSPlatform("windows")]
[ComImport]
[Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ITaskbarList3
{
    // ── ITaskbarList (5 methods) ──────────────────────────────────────
    public void HrInit();

    public void AddTab(nint hwnd);

    public void DeleteTab(nint hwnd);

    public void ActivateTab(nint hwnd);

    public void SetActiveAlt(nint hwnd);

    // ── ITaskbarList2 (1 method) ──────────────────────────────────────
    public void MarkFullscreenWindow(nint hwnd, [MarshalAs(UnmanagedType.Bool)] bool fFullscreen);

    // ── ITaskbarList3 (only the two we call) ──────────────────────────
    public void SetProgressValue(nint hwnd, ulong ullCompleted, ulong ullTotal);

    public void SetProgressState(nint hwnd, TaskbarProgressFlags tbpFlags);
}
