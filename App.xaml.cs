using System.Threading;
using System.Windows;

namespace GotifyDesktop;

public partial class App : System.Windows.Application
{
    internal static bool ShouldOpenWindow(string[] args) => !NotificationCopyInvocation.IsCopyRequest(args) && (!args.Contains("--background") || args.Contains("--uri"));
    private Mutex? _mutex;
    private bool _ownsMutex;
    private InstanceChannel? _channel;
    protected override void OnStartup(StartupEventArgs e)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        if (e.Args.Length == 2 && e.Args[0] == "--diagnostics") { Environment.Exit(Diagnostics.Run(e.Args[1])); return; }
        if (e.Args.Length == 2 && e.Args[0] == "--memory-check") { Environment.Exit(Diagnostics.RunMemory(e.Args[1])); return; }
        if (e.Args.Length == 2 && e.Args[0] == "--appearance-check") { Environment.Exit(Diagnostics.RunAppearance(e.Args[1])); return; }
        if (e.Args.Length == 2 && e.Args[0] == "--notification-check")
        {
            try
            {
                var toast = new NativeToast(); toast.Register();
                toast.Show(new GotifyMessage { Id = 1, Title = "Gotify Desktop 通知测试", Message = "本机功能测试。可使用下方按钮打开 Gotify 官网。", AppName = "Gotify Desktop", Extras = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>("""{"client::notification":{"click":{"url":"https://gotify.net"}}}""") });
                System.IO.File.WriteAllText(e.Args[1], "Windows toast registration and Show succeeded."); Shutdown(0);
            }
            catch (Exception ex) { System.IO.File.WriteAllText(e.Args[1], ex.ToString()); Shutdown(1); }
            return;
        }
        long? id = InstanceChannel.ParseActivation(e.Args);
        _mutex = new Mutex(true, "Local\\" + InstanceChannel.DefaultName, out _ownsMutex);
        if (!_ownsMutex)
        {
            try { if (ShouldOpenWindow(e.Args)) InstanceChannel.SendAsync(InstanceChannel.DefaultName, id).GetAwaiter().GetResult(); }
            catch (Exception) { System.Windows.MessageBox.Show("已有 Gotify Desktop 正在启动，请稍后从托盘打开。", "Gotify Desktop"); }
            Shutdown(); return;
        }
        _channel = new InstanceChannel(InstanceChannel.DefaultName, messageId => Dispatcher.BeginInvoke(() => (MainWindow as GotifyDesktop.MainWindow)?.OpenFromActivation(messageId)));
        base.OnStartup(e);
        bool background = e.Args.Contains("--background");
        var window = new MainWindow(background);
        MainWindow = window;
        if (ShouldOpenWindow(e.Args)) window.OpenFromActivation(id);
    }
    protected override void OnExit(ExitEventArgs e)
    {
        _channel?.Dispose();
        if (_ownsMutex) _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
