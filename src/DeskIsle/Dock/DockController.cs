using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
namespace DeskIsle.Dock;

internal sealed partial class DockController : IDisposable
{
    private readonly DockStore store = new();
    internal DockSettings Settings { get; }
    private readonly DispatcherTimer pointer = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private readonly DispatcherTimer refresh = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer startupRetry = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
    private int startupAttempts;
    private TaskbarLease? lease;
    private DockBar? bar;
    private Window? returnButton;
    private HwndSource? messages;
    private DockSettingsWindow? settingsWindow;
    private bool scanning, disposed, hotkey, refreshPending;
    private nint windowHook, foregroundHook;
    private DockNative.WinEventProc? changeCallback;
    private readonly DispatcherTimer settle = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(180) };
    private long edgeSince, awaySince, edgeRepairAt, returnRevealUntil;
    private nint lastExternalForeground;
    internal nint ForegroundForClick()
    {
        var foreground = DockNative.GetForegroundWindow();
        DockNative.GetWindowThreadProcessId(foreground, out var pid);
        if (foreground != 0 && pid != Environment.ProcessId) lastExternalForeground = foreground;
        return DockNative.IsWindow(lastExternalForeground) ? lastExternalForeground : 0;
    }
    private DockPin[] pins = [];
    private DockWindow[] windows = [];
    private string[] resolvedPaths = [];
    private long pinsResolvedAt;
    internal DockController()
    {
        Settings = store.Load();
        startupRetry.Tick += (_, _) => TryStartup();
        pointer.Tick += PointerTick;
        refresh.Tick += async (_, _) => await Refresh();
        settle.Tick += async (_, _) => { settle.Stop(); await Refresh(); };
        try { TaskbarLease.Restore(true); }
        catch (Exception ex) when (Recoverable(ex)) { MessageBox.Show("任务栏恢复失败：" + ex.Message, "DeskIsle"); }
    }
    internal void Start()
    {
        if (store.Warning != null) MessageBox.Show(store.Warning, "DeskIsle Dock");
        // Explicit product behavior: every launch starts Dock, even after a previous session disabled it.
        if (!store.ReadOnly) { startupAttempts = 0; TryStartup(); }
    }
    private void TryStartup()
    {
        if (disposed) { startupRetry.Stop(); return; }
        ++startupAttempts;
        // Both the taskbar and stale recovery must be ready before attempting takeover.
        try
        {
            if (DockNative.Taskbars().Length > 0 && TaskbarLease.Restore(true))
            {
                startupRetry.Stop();
                DeskIsle.Services.StartupDiagnostics.Mark("explorer-ready");
                Apply(true); return;
            }
        }
        catch (Exception ex) when (Recoverable(ex))
        { DeskIsle.Services.StartupDiagnostics.Mark("explorer-recovery-error=" + ex.GetType().Name); }
        if (startupAttempts < 60) { startupRetry.Start(); return; }
        startupRetry.Stop();
        DeskIsle.Services.StartupDiagnostics.Mark("explorer-wait-timeout");
        MessageBox.Show("等待桌面就绪超时，已保留原生任务栏。可在卡片菜单的 Dock 设置中重新启用。", "DeskIsle Dock");
    }
    private bool configureQueued;
    internal void Configure()
    {
        if (configureQueued || disposed) return;
        configureQueued = true;
        // Let the invoking ContextMenu/Popup release mouse capture and restore focus first.
        Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            configureQueued = false;
            if (disposed) return;
            if (settingsWindow == null)
            {
                settingsWindow = new(this);
                settingsWindow.Closed += (_, _) => settingsWindow = null;
            }
            if (settingsWindow.WindowState == WindowState.Minimized) settingsWindow.WindowState = WindowState.Normal;
            settingsWindow.Show();
            settingsWindow.Topmost = true;
            settingsWindow.Activate();
            settingsWindow.Topmost = false;
            settingsWindow.Focus();
        }));
    }
    internal bool Apply(bool enabled)
    {
        startupRetry.Stop();
        try
        {
            if (store.ReadOnly) throw new IOException("Dock 配置处于保护模式，不能启用接管。");
            if (enabled)
            {
                if (lease == null)
                {
                    lease = new();
                    try { lease.Acquire(); CreateWindows(); }
                    catch { Stop(); throw; }
                }
                Settings.Enabled = true;
                bar!.Surface.Opacity = Settings.Opacity;
                RegisterKey(); pointer.Start(); refresh.Start();
                _ = Refresh();
                Reveal();
            }
            else { Stop(); Settings.Enabled = false; }
            store.Save(Settings);
            return true;
        }
        catch (Exception ex) when (Recoverable(ex))
        {
            Stop(); Settings.Enabled = false;
            MessageBox.Show("Dock 未启用，已尝试恢复任务栏：" + ex.Message, "DeskIsle"); return false;
        }
    }
    private void CreateWindows()
    {
        lastExternalForeground = DockNative.GetForegroundWindow();
        bar = new(this) { Opacity = 0 };
        bar.RevealReady += () => lease?.BeginTakeover();
        bar.Show(); bar.Place(); bar.HideAnimated(true);
        var button = ReturnDockHead.Create();
        returnButton = new Window { Content = button, SizeToContent = SizeToContent.WidthAndHeight, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, AllowsTransparency = true, Background = Brushes.Transparent, ShowInTaskbar = false, Topmost = true, ShowActivated = false };
        var menu = new ContextMenu(); DockBar.Add(menu, "关闭 Dock 并恢复任务栏", () => Apply(false)); button.ContextMenu = menu;
        bool pressed = false, moved = false;
        DockNative.Point origin = default;
        Point initial = default;
        button.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (!DockNative.GetCursorPos(out origin)) return;
            pressed = true; moved = false;
            initial = new Point(returnButton.Left, returnButton.Top);
            button.CaptureMouse(); e.Handled = true;
        };
        button.PreviewMouseMove += (_, e) =>
        {
            if (!pressed || e.LeftButton != MouseButtonState.Pressed || !DockNative.GetCursorPos(out var current)) return;
            var scale = PresentationSource.FromVisual(returnButton)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
            var delta = scale.Transform(new Vector(current.X - origin.X, current.Y - origin.Y));
            if (!moved && Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            moved = true;
            PlaceReturn(initial.X + delta.X, initial.Y + delta.Y); e.Handled = true;
        };
        button.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (!pressed) return;
            var dragged = moved; pressed = false;
            button.ReleaseMouseCapture(); e.Handled = true;
            if (dragged) { Settings.ReturnLeft = returnButton.Left; Settings.ReturnTop = returnButton.Top; Save(); }
            else ToggleNative();
        };
        button.LostMouseCapture += (_, _) => { pressed = false; };
        // Keyboard/accessibility invocation still uses Button.Click; captured mouse gestures do not.
        button.Click += (_, _) => { if (!pressed) ToggleNative(); };
        // Initialize the native tool window before its first show. ShowActivated=false
        // alone does not prevent a later mouse click from activating this window.
        var returnHandle = new WindowInteropHelper(returnButton).EnsureHandle();
        DockNative.SetWindowLong(returnHandle, -20, DockNative.GetWindowLong(returnHandle, -20) | 0x08000000 | 0x80);
        messages = HwndSource.FromHwnd(returnHandle);
        messages.AddHook(WindowMessage);
        returnButton.Show(); PlaceReturn(); returnButton.Hide();
        RegisterAttention();
        changeCallback = (_, eventId, window, objectId, childId, _, _) =>
        {
            if (eventId == 3 && window != 0) lastExternalForeground = window;
            if (eventId != 3 && (objectId != 0 || childId != 0 || eventId is not (0x8000 or 0x8001 or 0x8002 or 0x8003 or 0x800C))) return;
            if (disposed || lease == null || settle.IsEnabled) return;
            settle.Start();
        };
        windowHook = DockNative.SetWinEventHook(0x8000, 0x800C, 0, changeCallback, 0, 0, 2);
        foregroundHook = DockNative.SetWinEventHook(3, 3, 0, changeCallback, 0, 0, 2);
    }
    private void RegisterKey()
    {
        if (messages == null) return;
        if (hotkey) DockNative.UnregisterHotKey(messages.Handle, 1);
        hotkey = DockNative.RegisterHotKey(messages.Handle, 1, 0x4003, Settings.HotKey);
        if (!hotkey) MessageBox.Show("Dock 快捷键已被占用，请在设置中换一个字母。返回按钮仍可使用。", "DeskIsle Dock");
    }
    private nint WindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x21) { handled = true; return 3; } // MA_NOACTIVATE, same as Dock
        HandleAttentionMessage(message, wParam, lParam);
        if (message == 0x0312 && wParam == 1) { ToggleNative(); handled = true; }
        if (message is 0x007E or 0x02E0) { bar?.Place(); PlaceReturn(); }
        return 0;
    }
    private void PlaceReturn(double? requestedLeft = null, double? requestedTop = null)
    {
        if (returnButton == null) return;
        var transform = PresentationSource.FromVisual(returnButton)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var monitor = DockNative.Primary().Monitor;
        var start = transform.Transform(new Point(monitor.Left, monitor.Top));
        var end = transform.Transform(new Point(monitor.Right, monitor.Bottom));
        double leftEdge = start.X + 12, topEdge = start.Y + 12, rightEdge = end.X - 12, bottomEdge = end.Y - 12;
        // WorkArea can still reflect the hidden taskbar. Use its HWND bounds even before
        // Explorer refreshes WorkArea, and convert physical pixels into WPF DIP.
        foreach (var taskbar in DockNative.Taskbars())
        {
            if (!DockNative.GetWindowRect(taskbar, out var bounds) || bounds.Right <= bounds.Left || bounds.Bottom <= bounds.Top) continue;
            var a = transform.Transform(new Point(bounds.Left, bounds.Top));
            var z = transform.Transform(new Point(bounds.Right, bounds.Bottom));
            if (z.X <= start.X || a.X >= end.X || z.Y <= start.Y || a.Y >= end.Y) continue;
            if (z.X - a.X >= z.Y - a.Y)
            {
                if (a.Y > (start.Y + end.Y) / 2) bottomEdge = Math.Min(bottomEdge, a.Y - 12);
                else topEdge = Math.Max(topEdge, z.Y + 12);
            }
            else if (a.X > (start.X + end.X) / 2) rightEdge = Math.Min(rightEdge, a.X - 12);
            else leftEdge = Math.Max(leftEdge, z.X + 12);
        }
        var width = returnButton.ActualWidth > 0 ? returnButton.ActualWidth : 40;
        var height = returnButton.ActualHeight > 0 ? returnButton.ActualHeight : 40;
        var left = requestedLeft ?? Settings.ReturnLeft ?? rightEdge - width;
        var top = requestedTop ?? Settings.ReturnTop ?? bottomEdge - height;
        returnButton.Left = Math.Clamp(double.IsFinite(left) ? left : rightEdge - width, leftEdge, Math.Max(leftEdge, rightEdge - width));
        returnButton.Top = Math.Clamp(double.IsFinite(top) ? top : bottomEdge - height, topEdge, Math.Max(topEdge, bottomEdge - height));
    }
    internal void ToggleNative()
    {
        if (lease == null) return;
        var native = !lease.NativeMode;
        lease.Native(native);
        if (native) { bar?.HideAnimated(true); PlaceReturn(); returnButton?.Show(); }
        else { returnButton?.Hide(); edgeSince = 0; returnRevealUntil = Environment.TickCount64 + 1400; Reveal(); }
    }
    private void Reveal()
    {
        if (bar == null || lease == null || lease.NativeMode) return;
        if (bar.WindowState == WindowState.Minimized) bar.WindowState = WindowState.Normal;
        bar.Place(); bar.RevealAnimated();
        DockNative.SetWindowPos(new WindowInteropHelper(bar).Handle, -1, 0, 0, 0, 0, 0x1 | 0x2 | 0x10);
        awaySince = Environment.TickCount64;
    }
    private void PointerTick(object? sender, EventArgs e)
    {
        if (lease == null || bar == null) return;
        UpdateAttention();
        if (!lease.NativeMode && Environment.TickCount64 < returnRevealUntil)
        {
            if (!bar.IsVisible || bar.IsHiding) Reveal();
            awaySince = Environment.TickCount64;
            return;
        }
        if (attentionWindows.Count > 0 && !lease.NativeMode)
        {
            // Pending attention holds the Dock open, including over a full-screen application.
            if (!bar.IsVisible || bar.IsHiding || DockNative.IsIconic(new WindowInteropHelper(bar).Handle) || !DockNative.IsWindowVisible(new WindowInteropHelper(bar).Handle)) Reveal();
            awaySince = Environment.TickCount64;
            return;
        }
        if (lease.NativeMode) { if (returnButton != null && (!returnButton.IsVisible || DockNative.IsIconic(new WindowInteropHelper(returnButton).Handle) || !DockNative.IsWindowVisible(new WindowInteropHelper(returnButton).Handle)))
            { PlaceReturn(); returnButton.WindowState = WindowState.Normal; returnButton.Show(); DockNative.ShowWindowAsync(new WindowInteropHelper(returnButton).Handle, 4); } return; }
        // A desktop with no unminimized application windows keeps its launcher visible.
        if (!DockNative.FullScreen() && windows.All(w => !DockNative.IsWindowVisible(w.Handle) || DockNative.IsIconic(w.Handle)))
        {
            if (!bar.IsVisible || bar.IsHiding || DockNative.IsIconic(new WindowInteropHelper(bar).Handle) || !DockNative.IsWindowVisible(new WindowInteropHelper(bar).Handle)) Reveal();
            awaySince = Environment.TickCount64;
            edgeSince = 0;
            return;
        }
        if (!DockNative.GetCursorPos(out var mouse)) return;
        var monitor = DockNative.Primary().Monitor;
        var now = Environment.TickCount64;
        var edgeHeight = Math.Max(4, (int)Math.Ceiling(6 * VisualTreeHelper.GetDpi(bar).DpiScaleY));
        var atEdge = mouse.Y >= monitor.Bottom - edgeHeight && mouse.Y < monitor.Bottom && mouse.X >= monitor.Left && mouse.X < monitor.Right;
        if (atEdge)
        {
            if (edgeSince == 0) edgeSince = now;
            if (edgeSince > 0 && now - edgeSince >= Settings.RevealMs)
            { Reveal(); edgeSince = -1; edgeRepairAt = now + 500; }
            else if (edgeSince == -1 && now >= edgeRepairAt)
            {
                // Repair an externally minimized/covered Dock without requiring the pointer to leave the edge.
                Reveal(); edgeRepairAt = now + 500;
            }
            awaySince = now;
        }
        else edgeSince = 0;
        DockNative.GetWindowRect(new WindowInteropHelper(bar).Handle, out var rect);
        var within = bar.IsVisible && mouse.X >= rect.Left && mouse.X < rect.Right && mouse.Y >= rect.Top && mouse.Y < monitor.Bottom;
        if (DockNative.FullScreen() && !atEdge && !within && !bar.Interacting)
        { bar.HideAnimated(); returnButton?.Hide(); return; }
        if (!bar.IsVisible) return;
        if (within || bar.Interacting || settingsWindow?.IsVisible == true)
        {
            if (bar.IsHiding) Reveal();
            awaySince = now;
        }
        else if (now - awaySince >= Settings.HideMs) bar.HideAnimated();
    }
    private async Task Refresh()
    {
        if (disposed || lease == null) return;
        if (scanning) { refreshPending = true; return; }
        scanning = true;
        try
        {
            lease.Reconcile();
            var requested = Settings.Pins.ToArray();
            var resolve = !resolvedPaths.SequenceEqual(requested, StringComparer.OrdinalIgnoreCase) ||
                Environment.TickCount64 - pinsResolvedAt >= 30000 || pins.Any(p => !p.Available || p.Executable == null);
            // Shortcut COM runs on an STA worker; no Shell icon extraction on the UI thread.
            var snapshot = await Task.Run(DockApps.Windows);
            var nextPins = resolve ? await ResolvePins(requested) : pins;
            if (disposed || lease == null) return;
            if (!requested.SequenceEqual(Settings.Pins, StringComparer.OrdinalIgnoreCase)) return;
            windows = snapshot; pins = nextPins; resolvedPaths = requested;
            if (resolve) pinsResolvedAt = Environment.TickCount64;
            bar?.SetApps(pins, windows);
            ReconcileAttention();
        }
        catch (Exception ex) when (Recoverable(ex)) { /* Preserve last snapshot; try again on the next event/tick. */ }
        finally
        {
            scanning = false;
            if (refreshPending && !disposed && lease != null) { refreshPending = false; settle.Start(); }
        }
    }
    private static Task<DockPin[]> ResolvePins(string[] paths)
    {
        var result = new TaskCompletionSource<DockPin[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { result.SetResult(paths.Select(DockApps.Resolve).ToArray()); } catch (Exception ex) { result.SetException(ex); } }) { IsBackground = true, Name = "DeskIsle Dock shortcuts" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return result.Task;
    }
    internal void Pin(string[] paths)
    {
        foreach (var path in paths.Where(p => Path.IsPathFullyQualified(p) && DockApps.Supported(p)))
            if (!Settings.Pins.Contains(path, StringComparer.OrdinalIgnoreCase) && Settings.Pins.Count < 100) Settings.Pins.Add(path);
        Save(); _ = Refresh();
    }
    internal void Unpin(string path) { Settings.Pins.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)); Save(); _ = Refresh(); }
    internal void Reorder(string source, string target)
    {
        if (source == target || !Settings.Pins.Contains(source) || !Settings.Pins.Contains(target)) return;
        Settings.Pins.Remove(source); Settings.Pins.Insert(Settings.Pins.IndexOf(target), source); Save(); _ = Refresh();
    }
    internal void Relocate(string old)
    {
        var picker = new OpenFileDialog { Filter = "应用|*.lnk;*.exe;*.appref-ms" };
        if (picker.ShowDialog() != true) return;
        var index = Settings.Pins.IndexOf(old); if (index < 0) return;
        Settings.Pins[index] = picker.FileName; Settings.Pins = Settings.Pins.Distinct(StringComparer.OrdinalIgnoreCase).ToList(); Save(); _ = Refresh();
    }
    internal void Launch(string path)
    {
        try { Process.Start(DockApps.LaunchInfo(path))?.Dispose(); }
        catch (Exception ex) when (Recoverable(ex)) { MessageBox.Show("无法打开应用：" + ex.Message, "DeskIsle Dock"); }
    }
    internal void Reveal(string path)
    {
        try { var info = new ProcessStartInfo("explorer.exe") { UseShellExecute = false }; info.ArgumentList.Add("/select,"); info.ArgumentList.Add(path); Process.Start(info)?.Dispose(); }
        catch (Exception ex) when (Recoverable(ex)) { MessageBox.Show(ex.Message, "DeskIsle Dock"); }
    }
    private void Save() { try { store.Save(Settings); } catch (Exception ex) when (Recoverable(ex)) { MessageBox.Show("Dock 配置保存失败：" + ex.Message, "DeskIsle"); } }
    private static bool Recoverable(Exception ex) => ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException or System.Text.Json.JsonException;
    private void Stop()
    {
        startupRetry.Stop(); pointer.Stop(); refresh.Stop(); settle.Stop();
        StopAttention();
        if (windowHook != 0) { DockNative.UnhookWinEvent(windowHook); windowHook = 0; }
        if (foregroundHook != 0) { DockNative.UnhookWinEvent(foregroundHook); foregroundHook = 0; }
        if (messages != null) { if (hotkey) DockNative.UnregisterHotKey(messages.Handle, 1); hotkey = false; messages.RemoveHook(WindowMessage); messages = null; }
        bar?.Close(); bar = null; returnButton?.Close(); returnButton = null;
        lease?.Dispose(); lease = null;
    }
    public void Dispose() { if (disposed) return; disposed = true; settingsWindow?.Close(); Stop(); }
}
