using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using SystemColors = System.Windows.SystemColors;

namespace GotifyDesktop;

internal static class ThemeManager
{
    private static readonly List<WeakReference<FrameworkElement>> PopupScopes = new();
    private static readonly string[] BrushKeys = { "PageBrush", "SurfaceBrush", "RaisedBrush", "LineBrush", "TextBrush", "MutedBrush", "AccentBrush", "AccentSoftBrush", "OnAccentBrush", "SuccessBrush", "SuccessSoftBrush", "WarningBrush", "WarningSoftBrush", "DangerBrush", "DangerSoftBrush", "PrimaryBrush" };
    internal static bool IsDark { get; private set; }
    internal static void TrackMenu(System.Windows.Controls.ContextMenu menu)
    {
        if (TrackScope(menu)) menu.Opened += (_, _) => RefreshPalette(menu);
    }
    internal static void TrackComboBox(System.Windows.Controls.ComboBox box)
    {
        if (TrackScope(box)) box.DropDownOpened += (_, _) => RefreshPalette(box);
    }
    private static bool TrackScope(FrameworkElement scope)
    {
        bool added = !PopupScopes.Any(reference => reference.TryGetTarget(out var value) && ReferenceEquals(value, scope));
        if (added) PopupScopes.Add(new(scope));
        RefreshPalette(scope); return added;
    }
    private static void RefreshPalette(FrameworkElement scope)
    {
        // Popups have a separate tree and may cache application resources while closed.
        // A local palette invalidates their template, item and separator resources together.
        foreach (string key in BrushKeys) scope.Resources[key] = System.Windows.Application.Current.Resources[key];
    }
    internal static void Apply(string theme)
    {
        IsDark = theme == "Dark";
        var values = IsDark
            ? new[] { "#101722", "#192332", "#223044", "#33445B", "#EDF3FC", "#A6B5CB", "#77A8FF", "#243D65", "#FFFFFF", "#78DCB0", "#183E35", "#FFBE75", "#493725", "#FF9DAB", "#482C38" }
            : new[] { "#F3F6FB", "#FFFFFF", "#F7F9FD", "#DAE2EE", "#1D2D46", "#667891", "#2467D6", "#EAF1FE", "#FFFFFF", "#18794E", "#E6F6ED", "#9C5600", "#FFF2DD", "#BC304A", "#FDECF0" };
        string[] keys = { "PageBrush", "SurfaceBrush", "RaisedBrush", "LineBrush", "TextBrush", "MutedBrush", "AccentBrush", "AccentSoftBrush", "OnAccentBrush", "SuccessBrush", "SuccessSoftBrush", "WarningBrush", "WarningSoftBrush", "DangerBrush", "DangerSoftBrush" };
        var app = System.Windows.Application.Current;
        for (int i = 0; i < keys.Length; i++) { var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(values[i])); brush.Freeze(); app.Resources[keys[i]] = brush; }
        app.Resources["PrimaryBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(IsDark ? "#2867C7" : "#2467D6"));
        app.Resources[SystemColors.WindowBrushKey] = app.Resources["SurfaceBrush"];
        app.Resources[SystemColors.WindowTextBrushKey] = app.Resources["TextBrush"];
        app.Resources[SystemColors.ControlBrushKey] = app.Resources["SurfaceBrush"];
        app.Resources[SystemColors.ControlTextBrushKey] = app.Resources["TextBrush"];
        app.Resources[SystemColors.HighlightBrushKey] = app.Resources["AccentSoftBrush"];
        app.Resources[SystemColors.HighlightTextBrushKey] = app.Resources["TextBrush"];
        for (int i = PopupScopes.Count - 1; i >= 0; i--)
        {
            if (PopupScopes[i].TryGetTarget(out var scope)) RefreshPalette(scope);
            else PopupScopes.RemoveAt(i);
        }
        // Native frame updates may dispatch window messages. Work from a stable
        // snapshot rather than enumerating a collection that callbacks can change.
        foreach (Window window in app.Windows.Cast<Window>().ToArray()) TitleBar(window);
    }
    internal static void TitleBar(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        int enabled = IsDark ? 1 : 0;
        try
        {
            // Windows 10 1809 uses attribute 19; later builds use attribute 20.
            if (DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int)) < 0)
                DwmSetWindowAttribute(handle, 19, ref enabled, sizeof(int));
            // Explicit caption colors are supported on Windows 11. Windows 10
            // rejects these attributes and continues using the dark-mode flag.
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            {
                int caption = CaptionColor("SurfaceBrush"), text = CaptionColor("TextBrush");
                DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int));
                DwmSetWindowAttribute(handle, 36, ref text, sizeof(int));
            }
            // Finish updating WPF resources before native painting dispatches
            // callbacks; this also lets newly created window handles finish loading.
            window.Dispatcher.BeginInvoke(DispatcherPriority.Render, () => RefreshTitleBarFrame(window, handle));
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }
    private static void RefreshTitleBarFrame(Window window, IntPtr handle)
    {
        if (!window.IsVisible || new WindowInteropHelper(window).Handle != handle) return;
        // Windows 10 can keep its cached light frame until activation changes.
        // Repaint both activation states without moving focus or the window.
        const uint wmNcActivate = 0x0086;
        const uint frameChanged = 0x0020, noSize = 0x0001, noMove = 0x0002, noZOrder = 0x0004, noActivate = 0x0010;
        const uint invalidate = 0x0001, updateNow = 0x0100, frame = 0x0400;
        bool active = window.IsActive;
        SendMessage(handle, wmNcActivate, active ? IntPtr.Zero : new IntPtr(1), IntPtr.Zero);
        SendMessage(handle, wmNcActivate, active ? new IntPtr(1) : IntPtr.Zero, IntPtr.Zero);
        SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, noSize | noMove | noZOrder | noActivate | frameChanged);
        RedrawWindow(handle, IntPtr.Zero, IntPtr.Zero, invalidate | updateNow | frame);
    }
    private static int CaptionColor(string key)
    {
        var color = ((SolidColorBrush)System.Windows.Application.Current.Resources[key]).Color;
        return color.R | (color.G << 8) | (color.B << 16);
    }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RedrawWindow(IntPtr window, IntPtr update, IntPtr region, uint flags);
}
