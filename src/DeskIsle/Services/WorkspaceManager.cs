using System.IO;
using System.Windows;
namespace DeskIsle.Services;

public sealed class WorkspaceManager : IDisposable
{
    private readonly WorkspaceStore store;
    private readonly WorkspaceState state;
    private readonly Dictionary<Guid, MainWindow> windows = [];
    public DesktopIcons DesktopIcons { get; } = new();
    public string? Warning { get; private set; }
    public bool Exiting { get; private set; }
    private DesktopReturnMonitor? desktopReturn;
    private readonly System.Windows.Threading.DispatcherTimer startupHide = new() { Interval = TimeSpan.FromSeconds(1) };
    private int startupHideAttempts;
    private bool startupHidePending;
    public void CancelStartupHide() { startupHidePending = false; startupHide.Stop(); }
    private void TryStartupHide()
    {
        if (!startupHidePending || Exiting) return;
        try
        {
            if (DesktopIcons.Hide()) { CancelStartupHide(); return; }
            if (++startupHideAttempts < 30) { startupHide.Start(); return; }
            Warning = "启动时未找到桌面图标，请使用卡片菜单手动隐藏。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Warning = "无法保存桌面图标恢复标记，未自动隐藏；请使用卡片菜单重试。"; }
        CancelStartupHide();
        foreach (var window in windows.Values) window.ShowStartupWarning(Warning!);
    }
    public bool DesktopIntegration { get; }
    public WorkspaceManager(string? dataDirectory = null, bool desktopIntegration = true)
    {
        DesktopIntegration = desktopIntegration;
        store = new WorkspaceStore(dataDirectory);
        state = store.Load();
        Warning = store.Warning;
        if (state.Cards.Count == 0) state.Cards.Add(new());
    }
    public void Start()
    {
        // Recovery is application-wide: creating a second card must never show hidden icons.
        if (DesktopIntegration && DesktopIcons.RecoveryPending)
        {
            try { if (!DesktopIcons.Restore()) Warning = "上次桌面图标状态未能恢复，请使用菜单恢复入口。"; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Warning = "桌面图标恢复标记无法处理，请使用菜单恢复入口。"; }
        }
        if (DesktopIntegration) desktopReturn = new DesktopReturnMonitor(Application.Current.Dispatcher, RecoverDesktopCards);
        foreach (var card in state.Cards.ToArray()) Open(card);
        if (DesktopIntegration)
        {
            startupHidePending = true;
            startupHide.Tick += (_, _) => TryStartupHide();
            TryStartupHide();
        }
    }
    private void RecoverDesktopCards()
    {
        if (Exiting) return;
        var desktop = DesktopReturnMonitor.ForegroundDesktop();
        // If another application owns the foreground, cards only refresh their existing surfaces.
        foreach (var window in windows.Values.ToArray()) window.RecoverOnDesktop(desktop);
    }
    private void Open(CardState card)
    {
        var window = new MainWindow(this, card);
        windows.Add(card.Id, window);
        window.Closed += (_, _) =>
        {
            windows.Remove(card.Id);
            if (windows.Count == 0 && !Exiting) Exit();
        };
        window.Show();
    }
    public void Add(MainWindow source)
    {
        if (store.ReadOnly) { MessageBox.Show(source, "配置处于保护模式，不能保存新卡片。", "DeskIsle"); return; }
        var area = SystemParameters.WorkArea;
        var left = source.Left + source.ActualWidth + 16;
        var top = source.Top;
        if (left + 360 > area.Right)
        {
            left = area.Left + 24 + state.Cards.Count % 8 * 24;
            top = area.Top + 48 + state.Cards.Count % 8 * 24;
        }
        var card = new CardState { Title = "新卡片", Left = left, Top = top };
        state.Cards.Add(card);
        Open(card);
        Save();
    }
    public void Remove(MainWindow window, CardState card)
    {
        if (window.HasPendingCreation) { MessageBox.Show(window, "正在处理文件，请完成后再移除卡片。", "DeskIsle"); return; }
        if (store.ReadOnly) { MessageBox.Show(window, "配置处于保护模式，不能移除卡片。", "DeskIsle"); return; }
        if (MessageBox.Show(window, "移除此卡片及其收藏记录？真实文件不会删除。移除最后一张卡片后程序退出。", "移除卡片", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var index = state.Cards.IndexOf(card);
        state.Cards.Remove(card);
        try { Save(); }
        catch { state.Cards.Insert(index, card); throw; }
        window.Close();
    }
    public void Save()
    {
        if (store.ReadOnly) throw new IOException("配置处于只读保护模式，未覆盖原文件。");
        store.Save(state);
    }
    public void Exit()
    {
        if (Exiting) return;
        if (windows.Values.Any(w => w.HasPendingCreation)) { MessageBox.Show("正在处理文件，请完成后再退出。", "DeskIsle"); return; }
        Exiting = true;
        CancelStartupHide();
        desktopReturn?.Dispose();
        foreach (var window in windows.Values.ToArray()) window.Close();
        try { Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { MessageBox.Show("保存布局失败，旧配置保留。", "DeskIsle"); }
        if (DesktopIntegration) DesktopIcons.Dispose();
        Application.Current.Shutdown();
    }
    public void Dispose()
    {
        CancelStartupHide();
        desktopReturn?.Dispose();
        if (DesktopIntegration) DesktopIcons.Dispose();
    }
}
