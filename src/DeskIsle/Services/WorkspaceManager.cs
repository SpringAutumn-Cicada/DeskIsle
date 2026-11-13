using System.IO;
using System.Windows;
namespace DeskIsle.Services;

public sealed partial class WorkspaceManager : IDisposable
{
    private readonly WorkspaceStore store;
    private readonly WorkspaceState state;
    private readonly Dictionary<Guid, MainWindow> windows = [];
    private DeskIsle.Dock.DockController? dock;
    public void ConfigureDock() { dock?.Configure(); }
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
        defaultSettle.Tick += async (_, _) => { defaultSettle.Stop(); await SyncDefaultDesktop(); };
        store = new WorkspaceStore(dataDirectory);
        state = store.Load();
        Warning = store.Warning;
        if (state.Cards.Count == 0) state.Cards.Add(new());
        if (!state.Cards.Any(c => c.Id == state.DefaultCardId)) state.DefaultCardId = state.Cards[0].Id;
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
        if (DesktopIntegration && PendingDesktopPaths.Length > 0)
        {
            try { EnsureMigrationBackup(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Warning = "旧配置备份未完成，请先修复权限再导入旧记录。"; }
        }
        foreach (var card in state.Cards.ToArray()) Open(card);
        if (DesktopIntegration) _ = SyncDefaultDesktop();
        if (DesktopIntegration) { dock = new DeskIsle.Dock.DockController(); dock.Start(); }
        if (DesktopIntegration && PendingDesktopPaths.Length > 0 && !state.DesktopMigrationPrompted)
            Application.Current.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() => windows.Values.FirstOrDefault()?.OfferDesktopMigration()));
        if (DesktopIntegration)
        {
            startupHidePending = true;
            startupHide.Tick += (_, _) => TryStartupHide();
            TryStartupHide();
        }
    }
    private void RecoverDesktopCards(bool repaint)
    {
        if (Exiting) return;
        var desktop = DesktopReturnMonitor.ForegroundDesktop();
        // Repair desktop placement only; ordinary foreground/dialog changes do not repaint cards.
        foreach (var window in windows.Values.ToArray()) window.RecoverOnDesktop(desktop, repaint);
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
        var previousDefault = state.DefaultCardId;
        state.Cards.Remove(card);
        if (state.DefaultCardId == card.Id) state.DefaultCardId = state.Cards.FirstOrDefault()?.Id;
        try { Save(); }
        catch { state.Cards.Insert(index, card); state.DefaultCardId = previousDefault; throw; }
        window.Close();
    }
    public bool FileMoveInProgress { get; private set; }
    public bool BeginFileMove()
    {
        if (Exiting || FileMoveInProgress || windows.Values.Any(w => w.HasPendingCreation || w.IsReordering || w.HasOpenFileDialog)) return false;
        if (store.ReadOnly) throw new IOException("配置处于保护模式，不能执行文件导入或移动。");
        FileMoveInProgress = true;
        return true;
    }
    public void EndFileMove() { FileMoveInProgress = false; if (DesktopIntegration) NotifyDesktopMutation(); }
    private string? migrationBackup;
    public string[] PendingDesktopPaths => state.Cards.SelectMany(card => card.References).Where(path => !DesktopFiles.IsCardPath(path)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(path => path.Length).ToArray();
    public IReadOnlyDictionary<string, string> ImportedPaths => new Dictionary<string, string>(state.ImportedPaths, StringComparer.OrdinalIgnoreCase);
    public void EnsureMigrationBackup()
    {
        if (store.ReadOnly) throw new IOException("配置处于保护模式，不能导入或迁移。");
        migrationBackup ??= store.BackupForDesktopMigration(state);
    }
    public void MarkDesktopMigrationPrompted()
    {
        EnsureMigrationBackup();
        state.DesktopMigrationPrompted = true;
        Save();
    }
    public void PublishMigrationRoots(TransferReport report)
    {
        foreach (var card in state.Cards)
            foreach (var root in report.Roots.Where(root => root.Published && card.References.Contains(root.Source, StringComparer.OrdinalIgnoreCase)))
                if (!card.References.Contains(root.Target, StringComparer.OrdinalIgnoreCase)) card.References.Add(root.Target);
        Save();
    }
    public void RemovePendingDesktopPaths()
    {
        EnsureMigrationBackup();
        foreach (var card in state.Cards) card.References.RemoveAll(path => !DesktopFiles.IsCardPath(path));
        Save();
    }
    public void RecordTransfer(Guid sourceCard, TransferCompletion completion)
    {
        DesktopFiles.Require(completion.Target);
        if (!completion.Moved)
        {
            if (!DesktopFiles.Private(completion.Source)) state.ImportedPaths[completion.Source] = completion.Target;
            foreach (var card in state.Cards)
            {
                card.References = card.References.Select(path => FileMoveService.Same(path, completion.Source) ? completion.Target : path).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                card.CreationDirectory = null;
            }
        }
        else
        {
            foreach (var card in state.Cards)
            {
                card.References = card.References.Where(path => card.Id != sourceCard || !FileMoveService.Same(path, completion.Source))
                    .Select(path => FileMoveService.Remap(path, completion.Source, completion.Target)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                card.CreationDirectory = null;
                card.FolderOrders = card.FolderOrders.GroupBy(pair => FileMoveService.Remap(pair.Key, completion.Source, completion.Target), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.SelectMany(pair => pair.Value).Select(path => FileMoveService.Remap(path, completion.Source, completion.Target)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), StringComparer.OrdinalIgnoreCase);
            }
            foreach (var key in state.ImportedPaths.Keys.ToArray()) state.ImportedPaths[key] = FileMoveService.Remap(state.ImportedPaths[key], completion.Source, completion.Target);
        }
        Save();
    }
    public void RecordDesktopRename(string source, string destination)
    {
        if (!DesktopFiles.IsCardPath(source) || !DesktopFiles.IsCardPath(destination)) return;
        foreach (var card in state.Cards)
        {
            card.References = card.References.Select(path => FileMoveService.Remap(path, source, destination)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            card.FolderOrders = card.FolderOrders.GroupBy(pair => FileMoveService.Remap(pair.Key, source, destination), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.SelectMany(pair => pair.Value).Select(path => FileMoveService.Remap(path, source, destination)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), StringComparer.OrdinalIgnoreCase);
        }
        foreach (var key in state.ImportedPaths.Keys.ToArray()) state.ImportedPaths[key] = FileMoveService.Remap(state.ImportedPaths[key], source, destination);
        Save();
    }
    public async Task RefreshAfterFileMove(string source, string destination)
    {
        foreach (var window in windows.Values.ToArray()) await window.RefreshAfterFileMove(source, destination);
    }
    public void Save()
    {
        if (store.ReadOnly) throw new IOException("配置处于只读保护模式，未覆盖原文件。");
        // Drop grouping references only, never the files. External pending migration records stay intact.
        foreach (var card in state.Cards)
            card.References.RemoveAll(path => DesktopFiles.IsCardPath(path) && !DesktopFiles.IsDesktopItem(path));
        store.Save(state);
    }
    public void Exit()
    {
        if (Exiting) return;
        if (windows.Values.Any(w => w.HasPendingCreation)) { MessageBox.Show("正在处理文件，请完成后再退出。", "DeskIsle"); return; }
        Exiting = true;
        CancelStartupHide();
        defaultSettle.Stop();
        dock?.Dispose();
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
        defaultSettle.Stop();
        dock?.Dispose();
        desktopReturn?.Dispose();
        if (DesktopIntegration) DesktopIcons.Dispose();
    }
}
