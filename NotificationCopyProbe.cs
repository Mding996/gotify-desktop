using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using DataObject = System.Windows.DataObject;
using Clipboard = System.Windows.Clipboard;
namespace GotifyDesktop;
// Explicit diagnostics only. Preserve every clipboard format in memory; abort if any format cannot be cloned.
internal static class NotificationCopyProbe
{
    internal static int Run(string reportPath)
    {
        reportPath = Path.GetFullPath(reportPath); Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        var checks = new List<string>(); string? error = null; DataObject? backup = null; bool wrote = false;
        string directory = Path.Combine(Path.GetDirectoryName(reportPath)!, ".copy-native-profile-" + Guid.NewGuid().ToString("N"));
        try
        {
            uint sequence = GetClipboardSequenceNumber(); backup = BackupClipboard();
            Check(sequence == GetClipboardSequenceNumber(), "All clipboard formats backed up in memory before changes");
            var actions = new NotificationCopyActions(directory);
            string uriA = actions.Create(new GotifyMessage { Id = 17, Message = "验证码：001234" })!;
            string uriB = actions.Create(new GotifyMessage { Id = 19, Message = "验证码：87654321" })!;
            var first = Launch(uriA, "first"); wrote = first.result == NotificationCopyResult.Success;
            Check(wrote && ReadText() == "001234", "Cold helper preserves leading zeros and clipboard data persists after exit");
            Check(!first.windowsFrameworkLoaded && !first.windowsFormsLoaded && first.mainWindow == 0, "Copy-only startup loads no WPF/Forms and creates no main window");
            var second = Launch(uriB, "second");
            Check(second.result == NotificationCopyResult.Success && ReadText() == "87654321", "Each notification copies its own immutable snapshot");
            Check(OpenClipboard(0), "Clipboard opened for contention test"); ProbeReport blocked;
            try { blocked = Launch(uriA, "blocked"); } finally { CloseClipboard(); }
            Check(blocked.result == NotificationCopyResult.ClipboardUnavailable && ReadText() == "87654321", "Busy clipboard fails without overwriting data");
            var missing = Launch(NotificationCopyInvocation.Prefix + "?key=" + Guid.NewGuid().ToString("N"), "missing");
            Check(missing.result == NotificationCopyResult.MissingRecord && ReadText() == "87654321", "Missing snapshots never overwrite the clipboard");
            string publicUri = new NotificationCopyActions(NotificationCopyActions.DefaultDirectory).Create(new GotifyMessage { Message = "验证码：001234" })!;
            NotificationCopyInvocation.TryKey(publicUri, out var key);
            try
            {
                using var command = Registry.CurrentUser.OpenSubKey(@"Software\Classes\gotify-desktop\shell\open\command", true) ?? throw new InvalidOperationException("Application protocol is not registered.");
                object? saved = command.GetValue(""); var kind = command.GetValueKind("");
                string testCommand = $"\"{Environment.ProcessPath}\" --uri \"%1\""; command.SetValue("", testCommand);
                try
                {
                    if (File.Exists(reportPath + ".preview"))
                    {
                        NativeToast.SetProcessIdentity();
                        var message = new GotifyMessage { Title = "验证码复制测试", Message = "【模拟短信】验证码：001234。仅用于测试复制按钮。", AppName = "Gotify 功能测试" };
                        using var toast = NativeToast.ShowTemporary(NativeToast.Payload(message, publicUri).Replace("<toast ", "<toast duration=\"long\" "));
                        File.WriteAllText(reportPath + ".ready", "ready");
                        var watch = Stopwatch.StartNew(); while (ReadText() != "001234" && watch.Elapsed < TimeSpan.FromSeconds(90)) Thread.Sleep(100);
                    }
                    else
                    {
                        using var helper = Process.Start(new ProcessStartInfo(publicUri) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden });
                        var watch = Stopwatch.StartNew(); while (ReadText() != "001234" && watch.Elapsed < TimeSpan.FromSeconds(15)) Thread.Sleep(100);
                        helper?.WaitForExit(5000);
                    }
                    Check(ReadText() == "001234", "Windows protocol reaches the public copy-only entry without starting the main application");
                }
                finally { if (Equals(command.GetValue(""), testCommand)) { if (saved == null) command.DeleteValue("", false); else command.SetValue("", saved, kind); } }
            }
            finally { File.Delete(Path.Combine(NotificationCopyActions.DefaultDirectory, key + ".action")); }
            ProbeReport Launch(string uri, string label)
            {
                string output = Path.ChangeExtension(reportPath, "." + label + ".json");
                var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
                foreach (string arg in new[] { "--copy-action-probe", directory, uri, output, "interactive" }) start.ArgumentList.Add(arg);
                using var child = Process.Start(start) ?? throw new InvalidOperationException("Failed to start helper.");
                if (!child.WaitForExit(10000)) { child.Kill(); throw new TimeoutException("Helper did not exit."); }
                return JsonSerializer.Deserialize<ProbeReport>(File.ReadAllText(output))!;
            }
        }
        catch (Exception ex) { error = ex.ToString(); }
        finally
        {
            try
            {
                if (backup != null && wrote && ReadText() is "001234" or "87654321")
                {
                    for (int attempt = 0; ; attempt++)
                        try { if (backup.GetFormats(false).Length == 0) Clipboard.Clear(); else Clipboard.SetDataObject(backup, true); break; }
                        catch (ExternalException) when (attempt < 7) { Thread.Sleep(60); }
                    checks.Add("Previous clipboard restored; no clipboard contents included in reports");
                }
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
            catch (Exception ex) { error = (error ?? "") + " Cleanup: " + ex; }
        }
        File.WriteAllText(reportPath, JsonSerializer.Serialize(new { passed = error == null, checks, error }, new JsonSerializerOptions { WriteIndented = true }));
        return error == null ? 0 : 1;
        void Check(bool ok, string label) { if (!ok) throw new InvalidOperationException(label); checks.Add(label); }
    }
    private static DataObject BackupClipboard()
    {
        var result = new DataObject(); var source = Clipboard.GetDataObject();
        foreach (var format in source?.GetFormats(false) ?? [])
        {
            object? value = source!.GetData(format, false);
            object copy = value switch { string text => text, string[] paths => paths.ToArray(), byte[] bytes => bytes.ToArray(), MemoryStream stream => new MemoryStream(stream.ToArray()), BitmapSource bitmap => bitmap.Clone(), int or uint or long or bool => value, _ => throw new NotSupportedException("Clipboard format cannot be safely backed up; no changes made.") };
            result.SetData(format, copy, false);
        }
        return result;
    }
    private sealed class ProbeReport
    {
        public NotificationCopyResult result { get; set; }
        public bool windowsFrameworkLoaded { get; set; }
        public bool windowsFormsLoaded { get; set; }
        public long mainWindow { get; set; }
        public double elapsedMilliseconds { get; set; }
    }
    internal static int Child(string directory, string uri, string output, string mode)
    {
        if (mode != "interactive") return 2;
        try
        {
            var watch = Stopwatch.StartNew(); var result = NotificationCopyRuntime.Execute(uri, new NotificationCopyActions(directory), NativeClipboard.WriteCode);
            var assemblies = AppDomain.CurrentDomain.GetAssemblies().Select(x => x.GetName().Name).ToArray(); using var process = Process.GetCurrentProcess();
            File.WriteAllText(output, JsonSerializer.Serialize(new ProbeReport { result = result, windowsFrameworkLoaded = assemblies.Contains("PresentationFramework") || assemblies.Contains("PresentationCore"), windowsFormsLoaded = assemblies.Contains("System.Windows.Forms"), mainWindow = process.MainWindowHandle.ToInt64(), elapsedMilliseconds = watch.Elapsed.TotalMilliseconds })); return 0;
        }
        catch (Exception ex) { File.WriteAllText(output + ".error", ex.ToString()); return 1; }
    }
    private static string ReadText()
    {
        if (!OpenClipboard(0)) return "";
        try { nint handle = GetClipboardData(13); if (handle == 0) return ""; nint value = GlobalLock(handle); if (value == 0) return ""; try { return Marshal.PtrToStringUni(value) ?? ""; } finally { GlobalUnlock(handle); } } finally { CloseClipboard(); }
    }
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenClipboard(nint owner);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern nint GetClipboardData(uint format);
    [DllImport("kernel32.dll")] private static extern nint GlobalLock(nint memory);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GlobalUnlock(nint memory);
}
