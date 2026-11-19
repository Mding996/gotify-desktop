using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace GotifyDesktop;

// An eagerly populated CF_UNICODETEXT handle belongs to Windows after SetClipboardData.
// It remains available after this short-lived process and its hidden owner window exit.
internal static class NativeClipboard
{
    internal static void WriteCode(string code)
    {
        if (!NotificationCopyActions.IsCode(code)) throw new ArgumentException("Invalid verification code.");
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA) throw new ThreadStateException("Clipboard actions require STA.");
        nint owner = CreateWindowEx(0, "STATIC", "Gotify Desktop Clipboard", 0, 0, 0, 0, 0, new nint(-3), 0, 0, 0);
        if (owner == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        nint memory = 0; bool opened = false;
        try
        {
            byte[] bytes = Encoding.Unicode.GetBytes(code + "\0");
            memory = GlobalAlloc(2, (nuint)bytes.Length);
            if (memory == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            nint buffer = GlobalLock(memory);
            if (buffer == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            try { Marshal.Copy(bytes, 0, buffer, bytes.Length); }
            finally { GlobalUnlock(memory); }
            for (int attempt = 0; attempt < 8; attempt++)
            {
                if (OpenClipboard(owner)) { opened = true; break; }
                Thread.Sleep(60);
            }
            if (!opened) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!EmptyClipboard() || SetClipboardData(13, memory) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            memory = 0;
        }
        finally
        {
            if (opened) CloseClipboard();
            if (memory != 0) GlobalFree(memory);
            DestroyWindow(owner);
        }
    }
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowEx(uint exStyle, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenClipboard(nint window);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetClipboardData(uint format, nint memory);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseClipboard();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint GlobalLock(nint memory);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GlobalUnlock(nint memory);
    [DllImport("kernel32.dll")] private static extern nint GlobalFree(nint memory);
}
