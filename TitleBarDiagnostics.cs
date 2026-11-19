using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace GotifyDesktop;

internal static partial class Diagnostics
{
    private static void CheckNativeTitleBar(MainWindow window, List<string> checks, string reportPath)
    {
        var handle = new WindowInteropHelper(window).EnsureHandle();
        foreach (string theme in new[] { "Dark", "Light", "Dark", "Light" })
        {
            ThemeManager.Apply(theme);
            PumpUntil(() => false, TimeSpan.FromMilliseconds(100));
            int result = DwmGetWindowAttribute(handle, 20, out int actual, sizeof(int));
            if (result < 0) result = DwmGetWindowAttribute(handle, 19, out actual, sizeof(int));
            if (result < 0 || actual != (theme == "Dark" ? 1 : 0))
                throw new InvalidOperationException($"Native title-bar theme did not switch to {theme}: HRESULT={result:X8}, value={actual}");
            checks.Add($"Native Windows title bar changes to {theme} on the same window handle");
        }
        // Capture the non-client frame, which WPF RenderTargetBitmap does not include.
        var preview = new Window { Title = "Gotify Desktop — 标题栏检查", Width = 640, Height = 240,
            Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false,
            Content = new System.Windows.Controls.TextBlock { Text = "标题栏随深浅色模式切换", Margin = new Thickness(24) } };
        preview.SetResourceReference(Window.BackgroundProperty, "SurfaceBrush");
        preview.SetResourceReference(Window.ForegroundProperty, "TextBrush");
        try
        {
            preview.Show();
            var previewHandle = new WindowInteropHelper(preview).Handle;
            foreach (string theme in new[] { "Dark", "Light" })
            {
                ThemeManager.Apply(theme); ThemeManager.TitleBar(preview);
                PumpUntil(() => false, TimeSpan.FromMilliseconds(100));
                GetWindowRect(previewHandle, out var bounds);
                using var bitmap = new System.Drawing.Bitmap(bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
                using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
                {
                    var dc = graphics.GetHdc();
                    try { if (!PrintWindow(previewHandle, dc, 2)) throw new InvalidOperationException("Native frame capture failed"); }
                    finally { graphics.ReleaseHdc(dc); }
                }
                bitmap.Save(Path.ChangeExtension(reportPath, $".titlebar-{theme.ToLowerInvariant()}.png"), System.Drawing.Imaging.ImageFormat.Png);
            }
        }
        finally { preview.Close(); ThemeManager.Apply("Light"); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
}
