using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DeskIsle;
using DeskIsle.Core;
using DeskIsle.Services;

internal static class Program
{
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window,uint command);
    private static readonly List<object> results=[];
    private const BindingFlags Private=BindingFlags.NonPublic|BindingFlags.Instance;
    private static object? Invoke(object o,string name,params object[] args)=>o.GetType().GetMethod(name,Private)!.Invoke(o,args);
    private static T Field<T>(object o,string name)=>(T)o.GetType().GetField(name,Private)!.GetValue(o)!;
    private static void Set(object o,string name,object value)=>o.GetType().GetField(name,Private)!.SetValue(o,value);
    private static void Assert(bool condition,string message){if(!condition)throw new Exception(message);}
    private static void PumpUntil(Func<bool> predicate,int timeout=10000)
    {
        var watch=Stopwatch.StartNew();
        while(!predicate())
        {
            if(watch.ElapsedMilliseconds>timeout)throw new TimeoutException("WPF state did not settle");
            var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);
            Thread.Sleep(10);
        }
    }
    private static IEnumerable<Button> Buttons(DependencyObject root)
    {
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)
        {
            var child=VisualTreeHelper.GetChild(root,i);
            if(child is Button {DataContext:FileTile} b)yield return b;
            else foreach(var found in Buttons(child))yield return found;
        }
    }
    [STAThread]
    public static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var originalForeground=GetForegroundWindow();
        var root=Path.Combine(Path.GetTempPath(),"DeskIsle-wpf-checks-"+Guid.NewGuid());Directory.CreateDirectory(root);
        var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
        app.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("pack://application:,,,/DeskIsle;component/Themes/Dark.xaml")});
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        WorkspaceManager? manager=null;var failures=0;
        void Check(string name,Action action)
        {
            var timer=Stopwatch.StartNew();
            try {action();results.Add(new{name,passed=true,milliseconds=timer.ElapsedMilliseconds});Console.WriteLine("PASS "+name);}
            catch(Exception ex){failures++;results.Add(new{name,passed=false,error=ex.ToString()});Console.WriteLine("FAIL "+name+": "+ex.GetBaseException().Message);}
        }
        try
        {
            var files=Path.Combine(root,"files");Directory.CreateDirectory(files);
            var paths=Enumerable.Range(0,1000).Select(i=>Path.Combine(files,$"文件 {i:D4}.txt")).ToList();foreach(var p in paths)File.WriteAllText(p,"preserve");
            var cards=new WorkspaceState{Cards=[new(){References=paths,Width=360,Height=430,Title="第一张"}]};
            new WorkspaceStore(root).Save(cards);manager=new WorkspaceManager(root,false);manager.Start();
            var window=Field<Dictionary<Guid,MainWindow>>(manager,"windows").Values.First();
            window.Left=-10000;window.Top=-10000;
            PumpUntil(()=>Field<IReadOnlyList<FileTile>>(window,"tiles").Count==1000);
            var list=(ListBox)window.FindName("Files");window.UpdateLayout();
            Check("1000项列表实际启用虚拟化",()=>{var count=Buttons(list).Count();Assert(count>0&&count<100,$"Realized {count} buttons");});
            Check("网格左右留白对称",()=>
            {
                var buttons=Buttons(list).ToArray();var top=buttons.Min(b=>b.TranslatePoint(new Point(),list).Y);var row=buttons.Where(b=>Math.Abs(b.TranslatePoint(new Point(),list).Y-top)<1).ToArray();
                var left=row.Min(b=>b.TranslatePoint(new Point(),list).X);var right=row.Max(b=>b.TranslatePoint(new Point(),list).X+b.ActualWidth);
                Assert(Math.Abs(left-(list.ActualWidth-right))<=1.1,$"Left={left},right={list.ActualWidth-right}");
            });
            Check("重命名Enter保存",()=>
            {
                Invoke(window,"RenameClick",window,new RoutedEventArgs(Button.ClickEvent));var editor=(TextBox)window.FindName("TitleEditor");editor.Text="已重命名";
                editor.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(window)!,0,Key.Enter){RoutedEvent=Keyboard.KeyDownEvent});
                Assert(editor.Visibility==Visibility.Collapsed&&cards.Cards[0].Title=="第一张","Editing closed and fixture input unchanged");
                Assert(new WorkspaceStore(root).Load().Cards[0].Title=="已重命名","Title was not persisted");
            });
            Check("重命名Esc取消和空白保留原名",()=>
            {
                Invoke(window,"RenameClick",window,new RoutedEventArgs(Button.ClickEvent));var editor=(TextBox)window.FindName("TitleEditor");editor.Text="取消";Invoke(window,"FinishRename",false);
                Invoke(window,"RenameClick",window,new RoutedEventArgs(Button.ClickEvent));editor.Text="   ";Invoke(window,"FinishRename",true);
                Assert(Field<CardState>(window,"state").Title=="已重命名","Title changed unexpectedly");
            });
            Check("点击卡片内部空白退出重命名",()=>
            {
                Invoke(window,"RenameClick",window,new RoutedEventArgs(Button.ClickEvent));var editor=(TextBox)window.FindName("TitleEditor");editor.Text="内部点击保存";
                window.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,0,MouseButton.Left){RoutedEvent=Mouse.PreviewMouseDownEvent,Source=list});
                Assert(editor.Visibility==Visibility.Collapsed&&Field<CardState>(window,"state").Title=="内部点击保存","Internal click did not commit");
            });
            Check("拖动高亮跟随命中项且绿线按半区移动",()=>
            {
                var b=Buttons(list).Take(3).ToArray();Assert(b.Length==3,"Need three visible tiles");var source=(FileTile)b[0].DataContext;Set(window,"reorderSource",source);
                var origin=b[1].TranslatePoint(new Point(),list);
                Set(window,"reorderPointer",new Point(origin.X+10,origin.Y+20));Invoke(window,"UpdateReorderPreview");var adorner=Field<ReorderAdorner>(window,"reorderAdorner");var target=adorner.Target;var first=adorner.InsertionLine;
                Set(window,"reorderPointer",new Point(origin.X+b[1].ActualWidth-10,origin.Y+20));Invoke(window,"UpdateReorderPreview");
                Assert(adorner.Target==target&&adorner.InsertionLine!=first,"Hover or half-slot behavior incorrect");
                var line=adorner.InsertionLine;var next=b[2].TranslatePoint(new Point(),list);
                Set(window,"reorderPointer",new Point(next.X+10,next.Y+20));Invoke(window,"UpdateReorderPreview");
                Assert(adorner.InsertionLine==line&&adorner.Target!=target,"Adjacent gap geometry or hover incorrect");Invoke(window,"ClearReorderPreview");Set(window,"reorderSource",null!);
            });
            Check("预览更新复用覆盖层并可清理",()=>
            {
                var b=Buttons(list).First();var point=b.TranslatePoint(new Point(10,20),list);Set(window,"reorderSource",b.DataContext);Set(window,"reorderPointer",point);Invoke(window,"UpdateReorderPreview");var preview=Field<ReorderAdorner>(window,"reorderAdorner");
                for(var i=0;i<20;i++){Set(window,"reorderPointer",new Point(point.X+i,point.Y));Invoke(window,"UpdateReorderPreview");Assert(ReferenceEquals(preview,Field<ReorderAdorner>(window,"reorderAdorner")),"Adorner recreated");}
                Invoke(window,"HideReorderPreview");Assert(preview.Visibility==Visibility.Hidden,"Preview not hidden");Invoke(window,"UpdateReorderPreview");Assert(preview.Visibility==Visibility.Visible,"Preview not restored");Invoke(window,"ClearReorderPreview");Assert(Field<ReorderAdorner?>(window,"reorderAdorner")==null,"Preview not removed");Set(window,"reorderSource",null!);
            });
            Check("多卡片独立命名与透明度保存",()=>
            {
                manager.Add(window);var windows=Field<Dictionary<Guid,MainWindow>>(manager,"windows").Values.ToArray();Assert(windows.Length==2,"Second card missing");var second=windows.Single(w=>w!=window);second.Left=-10000;second.Top=-10000;
                Invoke(second,"RenameClick",second,new RoutedEventArgs(Button.ClickEvent));((TextBox)second.FindName("TitleEditor")).Text="第二张独立名称";Invoke(second,"FinishRename",true);((Slider)second.FindName("OpacitySlider")).Value=.55;Invoke(second,"SaveCard");
                var saved=new WorkspaceStore(root).Load();Assert(saved.Cards[0].Title=="内部点击保存"&&saved.Cards[1].Title=="第二张独立名称"&&Math.Abs(saved.Cards[1].BackgroundOpacity-.55)<.001,"State leaked between cards");
            });
            Check("普通应用前台时重绘不抢焦点或改变层级",()=>
            {
                var host=Field<DesktopHost>(window,"host");var handle=new WindowInteropHelper(window).Handle;var foreground=GetForegroundWindow();var previous=GetWindow(handle,3);
                host.RecoverOnDesktop(window,0,true);window.UpdateLayout();
                Assert(GetForegroundWindow()==foreground&&GetWindow(handle,3)==previous,"Redraw affected focus or Z-order");
            });
            Check("真实WinEvent挂钩可注册且合并后释放",()=>
            {
                var count=0;var monitor=new DesktopReturnMonitor(Dispatcher.CurrentDispatcher,()=>count++);
                try
                {
                    Assert(monitor.Available,"Native hook registration failed");
                    Invoke(monitor,"OnEvent",(nint)0,(uint)3,(nint)0,0,0,(uint)0,(uint)0);
                    PumpUntil(()=>count>=2);monitor.Dispose();var stopped=count;
                    Invoke(monitor,"OnEvent",(nint)0,(uint)3,(nint)0,0,0,(uint)0,(uint)0);
                    var elapsed=Stopwatch.StartNew();PumpUntil(()=>elapsed.ElapsedMilliseconds>400);
                    Assert(count==stopped&&!monitor.Available,"Callback continued after disposal");
                }
                finally {monitor.Dispose();}
            });
            Check("移至默认卡片只更改唯一归属", () =>
            {
                var destination = Field<Dictionary<Guid, MainWindow>>(manager, "windows").Single(pair => pair.Value != window);
                manager.SetDefaultCard(destination.Key);
                var tile = Field<IReadOnlyList<FileTile>>(window, "tiles")[0];
                var before = File.ReadAllBytes(tile.FullPath);
                Invoke(window, "MoveToDefaultCardClick", new Button { DataContext = tile }, new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => Field<IReadOnlyList<FileTile>>(destination.Value, "tiles").Any(item => item.FullPath == tile.FullPath));
                Assert(Field<IReadOnlyList<FileTile>>(window, "tiles").All(item => item.FullPath != tile.FullPath), "Source tile still visible");
                var owners = new WorkspaceStore(root).Load().Cards.Where(card => card.References.Contains(tile.FullPath)).ToArray();
                Assert(owners.Length == 1 && owners[0].Id == destination.Key, "Expected one owner, the default card");
                Assert(before.SequenceEqual(File.ReadAllBytes(tile.FullPath)), "Real file changed");
            });
        }
        catch(Exception ex){failures++;results.Add(new{name="harness setup",passed=false,error=ex.ToString()});Console.WriteLine(ex);}
        finally
        {
            // Disable application shutdown in this harness while closing only its own windows.
            if(manager!=null)
            {
                typeof(WorkspaceManager).GetProperty("Exiting")!.SetValue(manager,true);
                foreach(var w in Field<Dictionary<Guid,MainWindow>>(manager,"windows").Values.ToArray())w.Close();manager.Dispose();
            }
            var path=args.Length>0?args[0]:Path.Combine(root,"results.json");Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);File.WriteAllText(path,JsonSerializer.Serialize(results,new JsonSerializerOptions{WriteIndented=true}));
            if(GetForegroundWindow()!=originalForeground)SetForegroundWindow(originalForeground);
            app.Shutdown();
            Directory.Delete(root,true);
        }
        return failures==0?0:1;
    }
}
