using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace GotifyDesktop;

// Shell notification icon without loading the Windows Forms UI framework.
internal sealed class NativeTray : IDisposable
{
    internal sealed record Command(Func<string> Text, Action Invoke, Func<bool>? Checked = null);
    internal List<Command?> Commands { get; } = new();
    internal Action? Open { get; set; }
    internal Action? BalloonClicked { get; set; }
    private const int Callback = 0x8000 + 17;
    private readonly HwndSource _source;
    private readonly int _taskbarCreated;
    private nint _icon;
    private bool _visible, _disposed;
    internal bool Visible => _visible;
    internal bool HasIcon => _icon != 0;
    internal static int DataSize => Marshal.SizeOf<IconData>();
    internal NativeTray()
    {
        _source = new HwndSource(new HwndSourceParameters("Gotify Desktop Tray") { WindowStyle = unchecked((int)0x80000000), Width = 0, Height = 0 });
        _source.AddHook(WindowProc); _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        ExtractIconEx(Environment.ProcessPath!, 0, out var large, out var small, 1);
        _icon = small != 0 ? small : large;
        if (large != 0 && large != _icon) DestroyIcon(large);
        if (_icon == 0) { _source.Dispose(); throw new Win32Exception("无法加载 Gotify 托盘图标。"); }
    }
    internal void Show()
    {
        if (_disposed || _visible) return;
        var data = Data(); data.Flags = 1 | 2 | 4 | 0x80;
        _visible = ShellNotifyIcon(0, ref data);
        if (_visible) { data.Version = 4; ShellNotifyIcon(4, ref data); }
    }
    internal void ShowBalloonTip(int milliseconds, string title, string body)
    {
        if (_disposed) return;
        if (!_visible) Show();
        var data = Data(); data.Flags = 0x10; data.Title = Shorten(title, 63); data.Info = Shorten(body, 255); data.InfoFlags = 1; data.Version = (uint)milliseconds;
        ShellNotifyIcon(1, ref data);
    }
    private static string Shorten(string value, int maximum)
    {
        if (value.Length <= maximum) return value;
        int length = maximum; if (char.IsHighSurrogate(value[length - 1])) length--;
        return value[..length];
    }
    private IconData Data() => new() { Size = (uint)DataSize, Window = _source.Handle, Id = 1, CallbackMessage = Callback, Icon = _icon, Tip = "Gotify Desktop", Info = "", Title = "" };
    private nint WindowProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (_disposed) return 0;
        if (message == _taskbarCreated) { _visible = false; Show(); return 0; }
        if (message != Callback) return 0;
        handled = true;
        int notification = (int)(lParam.ToInt64() & 0xffff);
        if (notification is 0x7b or 0x205) ShowMenu();
        else if (notification is 0x400 or 0x401 or 0x203) _source.Dispatcher.BeginInvoke(() => Open?.Invoke());
        else if (notification == 0x405) _source.Dispatcher.BeginInvoke(() => BalloonClicked?.Invoke());
        return 0;
    }
    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == 0) return;
        try
        {
            for (int i = 0; i < Commands.Count; i++)
            {
                var command = Commands[i];
                if (command == null) AppendMenu(menu, 0x800, 0, null);
                else AppendMenu(menu, command.Checked?.Invoke() == true ? 8u : 0u, (nuint)(i + 1), command.Text());
            }
            GetCursorPos(out var point); SetForegroundWindow(_source.Handle);
            uint id = TrackPopupMenu(menu, 0x100 | 2, point.X, point.Y, 0, _source.Handle, 0);
            PostMessage(_source.Handle, 0, 0, 0);
            if (id > 0 && id <= Commands.Count && Commands[(int)id - 1] is Command selected) _source.Dispatcher.BeginInvoke(selected.Invoke);
        }
        finally { DestroyMenu(menu); }
    }
    public void Dispose()
    {
        if (_disposed) return;
        if (_visible) { var data = Data(); ShellNotifyIcon(2, ref data); }
        _visible = false; _disposed = true; _source.RemoveHook(WindowProc); _source.Dispose();
        if (_icon != 0) { DestroyIcon(_icon); _icon = 0; }
        Commands.Clear(); Open = BalloonClicked = null;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct IconData
    {
        internal uint Size; internal nint Window; internal uint Id, Flags, CallbackMessage; internal nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string Tip;
        internal uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] internal string Info;
        internal uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] internal string Title;
        internal uint InfoFlags; internal Guid GuidItem; internal nint BalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point { internal int X, Y; }
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShellNotifyIcon(uint message, ref IconData data);
    [DllImport("shell32.dll", EntryPoint = "ExtractIconExW", CharSet = CharSet.Unicode)] private static extern uint ExtractIconEx(string file, int index, out nint large, out nint small, uint count);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode)] private static extern int RegisterWindowMessage(string message);
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AppendMenu(nint menu, uint flags, nuint id, string? title);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint window, nint rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
}
