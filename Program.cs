using System.Runtime.CompilerServices;

namespace GotifyDesktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--copy-native-check") return NotificationCopyProbe.Run(args[1]);
        if (args.Length == 5 && args[0] == "--copy-action-probe") return NotificationCopyProbe.Child(args[1], args[2], args[3], args[4]);
        // This path runs before Application.InitializeComponent, the main mutex, tray,
        // and WebSocket startup. It can never activate an already-running main window.
        if (NotificationCopyInvocation.IsCopyRequest(args)) return NotificationCopyRuntime.Handle(args);
        return RunApplication();
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunApplication()
    {
        var app = new App(); app.InitializeComponent(); return app.Run();
    }
}
