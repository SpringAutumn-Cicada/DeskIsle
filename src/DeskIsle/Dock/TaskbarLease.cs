using System.Diagnostics;
using System.IO;
using System.Text.Json;
using DeskIsle.Services;
namespace DeskIsle.Dock;

internal sealed class TaskbarLease : IDisposable
{
    internal sealed record Recovery(int Pid, long Started, bool Visible);
    internal static string Marker => Path.Combine(CardStore.DataDirectory, "taskbar-recovery.json");
    private bool active, takeoverReady;
    private bool visible;
    private readonly HashSet<nint> hidden = [];
    private Process? guard;
    private nint showHook;
    private DockNative.WinEventProc? showCallback;
    internal bool NativeMode { get; private set; }
    internal void Acquire()
    {
        if (active) return;
        var bars = DockNative.Taskbars();
        if (bars.Length == 0) throw new IOException("尚未找到主屏任务栏，请在 Explorer 启动完成后重试。");
        if (File.Exists(Marker)) throw new IOException("存在待恢复的任务栏记录，请先恢复原生任务栏。");
        visible = bars.Any(DockNative.IsWindowVisible);
        Directory.CreateDirectory(CardStore.DataDirectory);
        using var process = Process.GetCurrentProcess();
        File.WriteAllText(Marker, JsonSerializer.Serialize(new Recovery(Environment.ProcessId, process.StartTime.ToUniversalTime().Ticks, visible)));
        try
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--taskbar-watch");
            guard = Process.Start(start) ?? throw new IOException("任务栏恢复进程未能启动。");
            active = true;
            showCallback = (_, _, window, objectId, childId, _, _) =>
            {
                if (active && takeoverReady && !NativeMode && objectId == 0 && childId == 0 && DockNative.Class(window) == "Shell_TrayWnd")
                    DockNative.ShowWindowAsync(window, 0);
            };
            showHook = DockNative.SetWinEventHook(0x8002, 0x8002, 0, showCallback, 0, 0, 2);
        }
        catch { Restore(false); throw; }
    }
    internal void BeginTakeover()
    {
        if (!active || takeoverReady) return;
        takeoverReady = true;
        DeskIsle.Services.StartupDiagnostics.Mark("taskbar-takeover-ready");
        Reconcile();
    }
    internal void Native(bool show)
    {
        if (!active) return;
        NativeMode = show;
        hidden.Clear();
        if (show)
        {
            foreach (var bar in DockNative.Taskbars())
                if (!DockNative.IsWindowVisible(bar)) DockNative.ShowWindowAsync(bar, 4);
        }
        else Reconcile(); // one hide request per taskbar, not hide + reconciliation twice
    }
    internal void Reconcile()
    {
        if (!active || !takeoverReady || NativeMode) return;
        // Called by the low-frequency Shell reconciliation, not every pointer tick.
        foreach (var bar in DockNative.Taskbars())
            if (hidden.Add(bar) || DockNative.IsWindowVisible(bar)) DockNative.ShowWindowAsync(bar, 0);
    }
    internal static bool Restore(bool onlyStale)
    {
        if (!File.Exists(Marker)) return true;
        var record = JsonSerializer.Deserialize<Recovery>(File.ReadAllText(Marker)) ?? throw new IOException("任务栏恢复记录无法读取。");
        if (onlyStale && Alive(record)) return false;
        var bars = DockNative.Taskbars();
        if (bars.Length == 0) return false;
        foreach (var bar in bars) DockNative.ShowWindow(bar, record.Visible ? 4 : 0);
        File.Delete(Marker);
        return true;
    }
    private static bool Alive(Recovery record)
    {
        if (record.Pid <= 0) return false;
        try { using var p = Process.GetProcessById(record.Pid); return !p.HasExited && p.StartTime.ToUniversalTime().Ticks == record.Started; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }
    internal static void Watch()
    {
        try
        {
            if (!File.Exists(Marker)) return;
            var record = JsonSerializer.Deserialize<Recovery>(File.ReadAllText(Marker));
            if (record == null) return;
            while (File.Exists(Marker))
            {
                record = JsonSerializer.Deserialize<Recovery>(File.ReadAllText(Marker));
                if (record == null || !Alive(record)) break;
                Thread.Sleep(1000);
            }
            for (var i = 0; i < 30 && File.Exists(Marker); i++)
            { if (Restore(true)) return; Thread.Sleep(1000); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or JsonException) { }
    }
    public void Dispose()
    {
        if (showHook != 0) { DockNative.UnhookWinEvent(showHook); showHook = 0; }
        if (!active) return;
        // Leave the marker and guard alive if Explorer is not available yet.
        active = false;
        try
        {
            if (!Restore(false) && File.Exists(Marker))
            {
                var record = JsonSerializer.Deserialize<Recovery>(File.ReadAllText(Marker));
                if (record != null) File.WriteAllText(Marker, JsonSerializer.Serialize(record with { Pid = 0, Started = 0 }));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        finally { guard?.Dispose(); }
    }
}
