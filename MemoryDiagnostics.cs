using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ListView = System.Windows.Controls.ListView;

namespace GotifyDesktop;

internal static partial class Diagnostics
{
    // Identical workload for before/after comparisons. No real profile or network access.
    internal static int RunMemory(string reportPath)
    {
        reportPath = Path.GetFullPath(reportPath);
        var samples = new List<object>(); MainWindow? window = null; string? error = null;
        var profile = Path.Combine(Path.GetDirectoryName(reportPath)!, ".memory-profile-" + Path.GetFileNameWithoutExtension(reportPath));
        try
        {
            PrepareMemoryProfile(profile);
            window = new MainWindow(profile, false) { ShowInTaskbar = false };
            window.Show(); Settle(1200); Sample("visible-startup");
            var list = (ListView)window.FindName("MessagesList");
            var messages = (IList)typeof(MainWindow).GetField("_messages", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            long allocations = GC.GetTotalAllocatedBytes();
            for (int i = 0; i < 160; i++)
            {
                var message = (GotifyMessage)messages[i * 7]!; list.SelectedItem = message; list.ScrollIntoView(message); window.UpdateLayout();
                if (i % 20 == 0) Settle(20);
            }
            Settle(1200); Sample("after-browsing", GC.GetTotalAllocatedBytes() - allocations);
            allocations = GC.GetTotalAllocatedBytes();
            for (int i = 0; i < 50; i++)
            {
                window.MarkAllRead();
                var message = (GotifyMessage)messages[i]!; message.IsUnread = true; window.SelectMessage(message);
            }
            Settle(1200); Sample("after-history-writes", GC.GetTotalAllocatedBytes() - allocations);
            window.Hide(); Settle(4500); Sample("tray-idle");
            for (int i = 0; i < 3; i++) { window.OpenFromActivation(null); Settle(300); window.Hide(); Settle(700); }
            Settle(2500); Sample("tray-after-reopen-cycles");
            window.OpenFromActivation(null); Settle(1000); Sample("restored");
        }
        catch (Exception ex) { error = ex.ToString(); }
        finally { window?.DisposeResources(); window?.Close(); }
        File.WriteAllText(reportPath, JsonSerializer.Serialize(new { passed = error == null, error, samples, os = Environment.OSVersion.ToString(), runtime = Environment.Version.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
        return error == null ? 0 : 1;

        void Sample(string stage, long? allocated = null)
        {
            using var process = Process.GetCurrentProcess(); process.Refresh();
            var list = (ListView)window!.FindName("MessagesList");
            int Realized(DependencyObject node)
            {
                int count = node is System.Windows.Controls.ListViewItem ? 1 : 0;
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) count += Realized(VisualTreeHelper.GetChild(node, i));
                return count;
            }
            samples.Add(new { stage, workingSetMB = Math.Round(process.WorkingSet64 / 1048576d, 2), privateMB = Math.Round(process.PrivateMemorySize64 / 1048576d, 2), managedMB = Math.Round(GC.GetTotalMemory(false) / 1048576d, 2), allocatedMB = allocated.HasValue ? Math.Round(allocated.Value / 1048576d, 2) : (double?)null, realizedRows = Realized(list), loadedAssemblies = AppDomain.CurrentDomain.GetAssemblies().Select(x => x.GetName().Name).Order().ToArray() });
        }
        static void Settle(int ms)
        {
            var until = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < until)
            {
                var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame);
                Thread.Sleep(15);
            }
        }
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void PrepareMemoryProfile(string profile)
    {
        Directory.CreateDirectory(profile); new AppConfig { NotificationsEnabled = false }.Save(Path.Combine(profile, "config.json"));
        var messages = Enumerable.Range(1, 1250).Select(i => new GotifyMessage { Id = 20000 - i, AppId = i % 4 + 1, AppName = "测试应用 " + (i % 4 + 1), Title = "短信转发 " + i, Message = $"【测试服务】验证码：{100000 + i}，5 分钟内有效。\nSIM2 中国电信\n2026-11-08 08:30:00\n测试设备", Priority = 9, IsUnread = i < 40 }).ToList();
        using var stream = File.Create(Path.Combine(profile, "messages.json")); JsonSerializer.Serialize(stream, messages);
    }
    private static void CheckMemoryBehavior(MainWindow window, List<string> checks, string profile)
    {
        void Check(bool ok, string label) { if (!ok) throw new InvalidOperationException(label); checks.Add(label); }
        var model = new GotifyMessage { Title = "SMS", Message = "验证码：123456" };
        string first = model.VerificationCode;
        Check(first == "123456" && ReferenceEquals(first, model.VerificationCode), "Repeated code bindings reuse their extraction result");
        model.Message = "验证码：654321";
        Check(model.VerificationCode == "654321", "Verification-code cache invalidates when the body changes");
        model.Message = new string('字', 319) + "😀" + new string('字', 10000);
        Check(model.Preview.Length <= 321 && !char.IsHighSurrogate(model.Preview[^2]) && model.Message.Length > 10000, "Long list previews are bounded without splitting emoji or truncating stored content");
        window.ResetFilters(); window.ClearLocalHistory(); window.Show();
        window.ManagementConfig.NotificationsEnabled = true; window.ManagementConfig.NotificationOptions = new() { AggregateBursts = false };
        var messages = (IList)typeof(MainWindow).GetField("_messages", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var add = typeof(MainWindow).GetMethod("AddMessage", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var source = (System.Windows.Controls.CheckBox)window.FindName("ShowSourceBox"); source.IsChecked = false;
        var list = (ListView)window.FindName("MessagesList");
        add.Invoke(window, new object[] { new WireMessage { id = 90001, appid = 1, title = "Reusable SMS", message = "验证码：123456", priority = 0 } });
        list.SelectedItem = messages[0]; window.UpdateLayout();
        var reader = (System.Windows.Controls.RichTextBox)window.FindName("DetailRichBody"); var document = reader.Document;
        PumpUntil(() => false, TimeSpan.FromMilliseconds(200)); window.UpdateLayout();
        Check(reader.Height <= 120, "Short-message reading height settles without a repeating extent/viewport growth loop");
        window.SelectMessage((GotifyMessage)messages[0]!);
        Check(ReferenceEquals(document, reader.Document), "Selecting the same unchanged message reuses its existing rich document");
        source.IsChecked = true; source.IsChecked = false;
        Check(ReferenceEquals(document, reader.Document) && !reader.IsUndoEnabled && !((System.Windows.Controls.TextBox)window.FindName("DetailBody")).IsUndoEnabled, "Source toggling reuses the reader and read-only controls do not retain undo histories");
        var notifications = new List<GotifyMessage>(); window.NotificationSink = notifications.Add;
        window.Hide(); Check(window.ReaderSuspended, "Hiding the window suspends reader work");
        add.Invoke(window, new object[] { new WireMessage { id = 90002, appid = 2, title = "While hidden", message = "Still received", priority = 9 } });
        Check(notifications.Count == 1 && messages.Cast<GotifyMessage>().Single(x => x.Id == 90002).IsUnread && File.ReadAllText(Path.Combine(profile, "messages.json")).Contains("Still received"), "Background receive, notifications, unread state and disk persistence continue while hidden");
        window.OpenFromActivation(null); window.UpdateLayout();
        Check(!window.ReaderSuspended && list.Items.Count == 2 && ReferenceEquals(document, reader.Document), "Restoring the window preserves the short-message reader and includes background arrivals");
        add.Invoke(window, new object[] { new WireMessage { id = 90003, appid = 1, title = "Large body", message = "验证码：482619\n" + new string('字', 9000), priority = 0 } });
        var large = messages.Cast<GotifyMessage>().Single(x => x.Id == 90003); list.SelectedItem = large;
        window.Hide();
        Check(reader.Document.Blocks.Count == 0 && messages.Contains(large), "Hiding releases a large rich document while retaining the complete message model");
        window.OpenFromActivation(null); window.UpdateLayout();
        Check(reader.Document.Blocks.Count > 0 && ((System.Windows.Controls.TextBox)window.FindName("DetailBody")).Text == large.Message && ((System.Windows.Controls.TextBox)window.FindName("VerificationCodeBox")).Text == "482619", "Restoring a large reader recovers the full body and its exact verification code");
        window.NotificationSink = null; window.ManagementConfig.NotificationsEnabled = false;
        var tray = (NativeTray)typeof(MainWindow).GetField("_tray", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        tray.Commands[2]!.Invoke(); Check(tray.Commands[2]!.Checked!() && tray.Commands[2]!.Text() == "恢复通知", "Native tray pause action updates its checked state and label");
        tray.Commands[2]!.Invoke(); Check(!tray.Commands[2]!.Checked!() && tray.Commands[2]!.Text() == "暂停通知", "Native tray resume action restores notification state");
        Check(!AppDomain.CurrentDomain.GetAssemblies().Any(x => x.GetName().Name == "System.Windows.Forms"), "App workflows do not load the Windows Forms framework for the tray");
        var icons = (SmallIconConverter)window.FindResource("SmallIcon");
        var smallIcon = (BitmapSource)icons.Convert("pack://application:,,,/Assets/logo.png", typeof(BitmapSource), null!, System.Globalization.CultureInfo.InvariantCulture)!;
        Check(smallIcon.IsFrozen && smallIcon.PixelWidth == 64 && ReferenceEquals(smallIcon, icons.Convert("pack://application:,,,/Assets/logo.png", typeof(BitmapSource), null!, System.Globalization.CultureInfo.InvariantCulture)), "Repeated icons share a frozen 64-pixel decoded bitmap");
        Check(ReferenceEquals(smallIcon, icons.Convert("https://example.invalid/private-icon.png", typeof(BitmapSource), null!, System.Globalization.CultureInfo.InvariantCulture)), "Icon bindings reject remote URLs and use the local fallback without a network request");
    }
}
