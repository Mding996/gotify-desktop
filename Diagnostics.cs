using System.Collections;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Controls;
using TextBox = System.Windows.Controls.TextBox;
using ListView = System.Windows.Controls.ListView;
using ComboBox = System.Windows.Controls.ComboBox;
using CheckBox = System.Windows.Controls.CheckBox;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Windows.Threading;

namespace GotifyDesktop;

// Offline integration checks using synthetic data; no real server or user profile is touched.
internal static partial class Diagnostics
{
    internal static int RunAppearance(string reportPath)
    {
        var checks = new List<string>();
        MainWindow? window = null;
        string? error = null;
        try
        {
            reportPath = Path.GetFullPath(reportPath);
            var profile = Path.Combine(Path.GetDirectoryName(reportPath)!, ".appearance-profile");
            Directory.CreateDirectory(profile);
            new AppConfig { NotificationsEnabled = false }.Save(Path.Combine(profile, "config.json"));
            window = new MainWindow(profile, false);
            CheckFiltersAndNotifications(window, checks, reportPath);
            CheckRichMessagesAndPolicy(window, checks, reportPath);
            CheckMenuAndStatus(window, checks, reportPath);
            CheckNativeTitleBar(window, checks, reportPath);
        }
        catch (Exception ex) { error = ex.ToString(); }
        finally { window?.DisposeResources(); window?.Close(); }
        File.WriteAllText(reportPath, JsonSerializer.Serialize(new { passed = error == null, checks, error }, new JsonSerializerOptions { WriteIndented = true }));
        return error == null ? 0 : 1;
    }
    internal static int Run(string reportPath)
    {
        File.WriteAllText(reportPath + ".progress", "Starting diagnostics\n");
        var checks = new List<string>();
        MainWindow? window = null;
        string? error = null;
        try
        {
            reportPath = Path.GetFullPath(reportPath);
            var profile = Path.Combine(Path.GetDirectoryName(reportPath)!, ".diagnostics-profile");
            Directory.CreateDirectory(profile);
            var path = Path.Combine(profile, "config.json");
            var config = new AppConfig { ClientToken = "diagnostic-client-secret", ApplicationToken = "diagnostic-app-secret", NotificationsEnabled = false };
            config.Save(path);
            var saved = File.ReadAllText(path);
            Assert(!saved.Contains("diagnostic-client-secret") && !saved.Contains("diagnostic-app-secret"), "Tokens absent from plaintext configuration");
            var loaded = AppConfig.Load(path);
            Assert(loaded.ClientToken == config.ClientToken && loaded.ApplicationToken == config.ApplicationToken, "DPAPI configuration round trip");
            Assert(config.ClientToken == "diagnostic-client-secret", "Saving preserves in-memory token");
            Assert(MainWindow.StartupCommand(@"C:\Apps\Gotify Desktop.exe") == "\"C:\\Apps\\Gotify Desktop.exe\" --background", "Startup command quotes paths and requests silent tray mode");
            Assert(!App.ShouldOpenWindow(new[] { "--background" }) && App.ShouldOpenWindow(Array.Empty<string>()) && App.ShouldOpenWindow(new[] { "--background", "--uri", "gotify-desktop://message?id=42" }), "Silent startup stays hidden while direct launches and notification activations open the window");
            Assert(InstanceChannel.ParseActivation(new[] { "--uri", "gotify-desktop://message?id=9876543210" }) == 9876543210 && InstanceChannel.ParseActivation(new[] { "--uri", "https://evil/message?id=1" }) == null, "Notification activation parses only the app protocol and long message IDs");
            var pipeName = "GotifyDesktop.Tests." + Guid.NewGuid().ToString("N");
            var activated = new TaskCompletionSource<long?>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var channel = new InstanceChannel(pipeName, id => activated.TrySetResult(id)))
            {
                InstanceChannel.SendAsync(pipeName, 42).GetAwaiter().GetResult();
                Assert(activated.Task.Wait(TimeSpan.FromSeconds(4)) && activated.Task.Result == 42, "Single-instance IPC forwards message activation to the existing process");
            }
            string toastXml = NativeToast.Payload(new GotifyMessage { Id = 42, Title = "<Title> & test", Message = "Text & <body> 😀\u0001", AppName = "Mock App" });
            var xml = System.Xml.Linq.XElement.Parse(toastXml);
            Assert(xml.Attribute("activationType")?.Value == "protocol" && xml.Descendants("text").First().Value == "<Title> & test" && !toastXml.Contains('\u0001'), "Action Center toast payload preserves text and escapes XML with protocol activation");
            File.AppendAllText(reportPath + ".progress", "Entering Windows Runtime notification factories\n");
            NativeToast.ValidatePlatform(toastXml);
            Assert(true, "Windows Runtime XmlDocument and ToastNotification factories create the notification successfully");
            Assert(UserError.Describe(new System.Net.Http.HttpRequestException(null, null, HttpStatusCode.Unauthorized), "Client Token").Contains("Client Token") && UserError.Describe(new System.Net.Http.HttpRequestException(null, null, HttpStatusCode.Forbidden), "Application Token").Contains("403") && UserError.Describe(new TimeoutException(), "Client Token").Contains("超时"), "Errors distinguish Client Token, Application Token, permission and timeout failures");
            foreach (var priority in Enumerable.Range(0, 11))
                Assert(MainWindow.ShouldNotify(true, false, priority) == (priority >= 8), $"Notification priority {priority}");
            Assert(!MainWindow.ShouldNotify(true, true, 9) && !MainWindow.ShouldNotify(false, false, 9), "Paused and disabled notification filters");
            window = new MainWindow(profile, false);
            Assert(window.FindName("TitleBox") is TextBox && window.FindName("MessageContentBox") is TextBox, "WPF XAML resources and composer controls load");
            var list = (ListView)window.FindName("MessagesList");
            var messages = (IList)typeof(MainWindow).GetField("_messages", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            messages.Clear();
            var add = typeof(MainWindow).GetMethod("AddMessage", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var wire = new WireMessage { id = 42, appid = 1, title = "Offline check", message = "Synthetic message", priority = 9, date = DateTime.UtcNow };
            add.Invoke(window, new object[] { wire });
            add.Invoke(window, new object[] { wire });
            Assert(messages.Count == 1, "Realtime message insertion and ID deduplication");
            Assert(File.ReadAllText(Path.Combine(profile, "messages.json")).Contains("Synthetic message"), "Message history persistence");
            ((TextBox)window.FindName("SearchBox")).Text = "synthetic";
            Assert(list.Items.Count == 1, "Search matches message content case-insensitively");
            ((TextBox)window.FindName("SearchBox")).Text = "not found";
            Assert(list.Items.Count == 0 && MainWindow.CopyText((GotifyMessage)messages[0]!).Contains("Synthetic message"), "Search excludes unmatched messages and copy formatting contains full content");
            ((TextBox)window.FindName("SearchBox")).Clear();
            for (int i = messages.Count; i < MainWindow.HistoryLimit; i++) messages.Add(new GotifyMessage { Id = 1000 + i });
            wire.id = 9999;
            add.Invoke(window, new object[] { wire });
            Assert(messages.Count == MainWindow.HistoryLimit && ((GotifyMessage)messages[0]!).Id == 9999, "Message history bounded to 5000 newest items");
            var tray = (NativeTray)typeof(MainWindow).GetField("_tray", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            Assert(tray.Visible && tray.HasIcon && tray.Commands.Count == 7 && NativeTray.DataSize == 976, "Native tray icon and all six menu actions initialize");
            window.ClearLocalHistory();
            Assert(messages.Count == 0 && JsonSerializer.Deserialize<List<GotifyMessage>>(File.ReadAllText(Path.Combine(profile, "messages.json")))!.Count == 0, "Clear deletes only the local saved history and resets pagination");
            CheckTransport(window, checks);
            CheckExperience(window, checks, reportPath, profile);
            CheckFiltersAndNotifications(window, checks, reportPath);
            CheckRichMessagesAndPolicy(window, checks, reportPath);
            CheckMenuAndStatus(window, checks, reportPath);
            CheckNativeTitleBar(window, checks, reportPath);
            CheckManagement(window, checks, reportPath, profile);
            CheckMemoryBehavior(window, checks, profile);
            CheckApplicationSending(checks, reportPath, profile);
            CheckNotificationCopy(window, checks, profile);
            CheckPublicReadiness(checks, profile);
            window.DisposeResources();
            window.Close();
            window = null;
        }
        catch (Exception ex) { error = ex.ToString(); }
        finally { window?.DisposeResources(); }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
        File.WriteAllText(reportPath, JsonSerializer.Serialize(new { passed = error == null, checks, error, runtime = Environment.Version.ToString(), os = Environment.OSVersion.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
        return error == null ? 0 : 1;

        void Assert(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException(description);
            checks.Add(description);
            File.AppendAllText(reportPath + ".progress", description + "\n");
        }
    }

    private static void CheckExperience(MainWindow window, List<string> checks, string reportPath, string profile)
    {
        void Check(bool value, string description) { if (!value) throw new InvalidOperationException(description); checks.Add(description); }
        object Element(string name) => window.FindName(name);
        void Invoke(string name) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { window, new System.Windows.RoutedEventArgs() });
        var config = (AppConfig)typeof(MainWindow).GetField("_config", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var list = (ListView)Element("MessagesList");
        var messages = (IList)typeof(MainWindow).GetField("_messages", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var composer = (System.Windows.Controls.Border)Element("ComposerPanel");
        Check(composer.Visibility == System.Windows.Visibility.Collapsed, "Composer is collapsed by default after a successful completed send");
        window.ToggleComposer(true);
        ((TextBox)Element("TitleBox")).Text = "Saved draft";
        ((TextBox)Element("MessageContentBox")).Text = "Keep this draft while reading";
        window.ToggleComposer(false); window.ToggleComposer(true);
        Check(((TextBox)Element("MessageContentBox")).Text == "Keep this draft while reading", "Collapsing and reopening the composer preserves the draft");
        window.ToggleComposer(false);

        window.ClearLocalHistory();
        Check(((TextBlock)Element("EmptyTitle")).Text == "还没有消息", "Configured empty state offers server refresh");
        string token = config.ClientToken; config.ClientToken = ""; window.ClearLocalHistory();
        Check(((TextBlock)Element("EmptyTitle")).Text == "连接你的 Gotify" && ((System.Windows.Controls.Button)Element("EmptyActionButton")).Content.ToString() == "打开设置", "First-run empty state explains setup with a settings action");
        config.ClientToken = token; window.ClearLocalHistory();
        Render((System.Windows.FrameworkElement)window.Content, Path.ChangeExtension(reportPath, ".empty.png"), 1120, 740);
        ((TextBox)Element("SearchBox")).Text = "nonexistent";
        Check(((TextBlock)Element("EmptyTitle")).Text == "没有找到匹配消息", "Search with no matches offers a clear-filter empty state");
        ((TextBox)Element("SearchBox")).Clear();
        var add = typeof(MainWindow).GetMethod("AddMessage", BindingFlags.NonPublic | BindingFlags.Instance)!;
        string fullBody = "这是一条用于界面检查的完整消息。\n\n列表只显示两行摘要，右侧详情保留全部内容。可以在此选择文字，或点击下方按钮复制完整消息。\n\n关闭发送区不会丢失草稿；新收到的消息会以未读圆点提示。";
        add.Invoke(window, new object[] { new WireMessage { id = 900001, appid = 0, title = "重要消息：请查看完整内容", message = fullBody, priority = 9, date = DateTime.UtcNow } });
        var first = (GotifyMessage)messages[0]!;
        Check(first.IsUnread, "New realtime messages are locally unread");
        list.SelectedItem = first;
        PumpUntil(() => ((TextBox)Element("DetailBody")).Text == fullBody, TimeSpan.FromSeconds(2));
        Check(!first.IsUnread && ((TextBox)Element("DetailBody")).Text == fullBody && ((System.Windows.Controls.Grid)Element("DetailPanel")).Visibility == System.Windows.Visibility.Visible, "Selecting a message shows its full detail and marks it read");
        Check(JsonSerializer.Deserialize<List<GotifyMessage>>(File.ReadAllText(Path.Combine(profile, "messages.json")))!.Single().IsUnread == false, "Reading a message persists its read state");
        Invoke("MarkUnread_Click");
        Check(first.IsUnread, "A selected message can be marked unread again");
        ((System.Windows.Controls.CheckBox)Element("UnreadOnlyBox")).IsChecked = true;
        window.SelectMessage(first);
        PumpUntil(() => list.Items.Count == 0, TimeSpan.FromSeconds(2));
        Check(list.Items.Count == 0 && ((TextBox)Element("DetailBody")).Text == fullBody && ((TextBlock)Element("EmptyTitle")).Text == "未读消息已处理完", "Reading in unread-only mode removes the row while keeping the open detail visible");
        ((System.Windows.Controls.CheckBox)Element("UnreadOnlyBox")).IsChecked = false;
        add.Invoke(window, new object[] { new WireMessage { id = 900002, appid = 0, title = "服务状态更新", message = "本地模拟服务器的连接和消息同步检查已完成。", priority = 5, date = DateTime.UtcNow } });
        window.MarkAllRead();
        Check(messages.Cast<GotifyMessage>().All(x => !x.IsUnread) && JsonSerializer.Deserialize<List<GotifyMessage>>(File.ReadAllText(Path.Combine(profile, "messages.json")))!.All(x => !x.IsUnread), "Mark-all-read updates the entire local collection and saved history");
        add.Invoke(window, new object[] { new WireMessage { id = 900003, appid = 0, title = "一条新的未读消息", message = "可以使用“只看未读”快速筛选待处理内容。", priority = 8, date = DateTime.UtcNow } });
        Check(first.IsImportant && !((GotifyMessage)messages[1]!).IsImportant, "High-priority and regular messages expose distinct visual importance");
        ((TextBox)Element("TitleBox")).Clear(); ((TextBox)Element("MessageContentBox")).Clear(); ((TextBox)Element("PriorityBox")).Text = "5";
        window.SetNotice("这条成功提示会自动收起。", NoticeTone.Success, transient: true);
        PumpUntil(() => ((System.Windows.Controls.Border)Element("NoticePanel")).Visibility == System.Windows.Visibility.Collapsed, TimeSpan.FromSeconds(6));
        Check(((TextBlock)Element("NoticeLabel")).Text == "", "Transient success feedback dismisses itself");
        window.SetNotice("Client Token 无效，请更新配置。", NoticeTone.Error, NoticeAction.Settings);
        Check(((System.Windows.Controls.Button)Element("NoticeActionButton")).Content.ToString() == "打开设置" && ((TextBlock)Element("NoticeHeading")).Text == "需要处理", "Persistent error banner presents a concrete settings action");
        Check(MainWindow.ActionForError(new System.Net.Http.HttpRequestException(null, null, HttpStatusCode.Unauthorized), NoticeAction.History) == NoticeAction.Settings && MainWindow.ActionForError(new TimeoutException(), NoticeAction.History) == NoticeAction.History, "Authentication and transient errors select the appropriate recovery action");
        window.DismissNotice();
        ((TextBlock)Element("StatusLabel")).Text = "离线界面预览";
        if (ThemeManager.IsDark) Invoke("Theme_Click");
        Check(Contrast("TextBrush", "SurfaceBrush") >= 4.5 && Contrast("OnAccentBrush", "PrimaryBrush") >= 4.5, "Light theme has readable text and primary-button contrast");
        Render((System.Windows.FrameworkElement)window.Content, Path.ChangeExtension(reportPath, ".png"), 1120, 740);
        window.ToggleComposer(true);
        Render((System.Windows.FrameworkElement)window.Content, Path.ChangeExtension(reportPath, ".compose.png"), 1120, 740);
        var detailScroll = (ScrollViewer)Element("DetailScroll");
        Check(detailScroll.ViewportHeight > 0 && detailScroll.ScrollableHeight > 0 && ((System.Windows.Controls.RichTextBox)Element("DetailRichBody")).ActualHeight >= 80, "Expanded composer keeps complete message content accessible by scrolling");
        detailScroll.ScrollToEnd(); window.UpdateLayout();
        Check(detailScroll.VerticalOffset > 0, "Detail pane scrolls to the end while composing");
        detailScroll.ScrollToTop();
        Render((System.Windows.FrameworkElement)window.Content, Path.ChangeExtension(reportPath, ".compose.png"), 1120, 740);
        window.ToggleComposer(false);
        Invoke("Theme_Click");
        Check(ThemeManager.IsDark && AppConfig.Load(Path.Combine(profile, "config.json")).Theme == "Dark", "Dark theme selection persists alongside encrypted tokens");
        Check(Contrast("TextBrush", "SurfaceBrush") >= 4.5 && Contrast("MutedBrush", "SurfaceBrush") >= 4.5 && Contrast("OnAccentBrush", "PrimaryBrush") >= 4.5, "Dark theme preserves readable text, muted text and primary-button contrast");
        Render((System.Windows.FrameworkElement)window.Content, Path.ChangeExtension(reportPath, ".dark.png"), 1120, 740);
        Render((System.Windows.FrameworkElement)window.Content, Path.ChangeExtension(reportPath, ".compact.png"), 912, 570);
        var settings = new SettingsWindow(config);
        Check(((System.Windows.Controls.ComboBox)settings.FindName("ThemeBox")).SelectedIndex == 1 && settings.Config.Theme == "Dark", "Settings retains the selected theme without losing existing token values");
        Render((System.Windows.FrameworkElement)settings.Content, Path.ChangeExtension(reportPath, ".settings-dark.png"), 500, 650);
        settings.Close();
        Invoke("Theme_Click");
        Check(!ThemeManager.IsDark && AppConfig.Load(Path.Combine(profile, "config.json")).Theme == "Light", "Switching back to light mode persists the preference");
        checks.Add("Light, dark, compact, composer, empty and settings layouts rendered to PNG");
    }

    private static void CheckFiltersAndNotifications(MainWindow window, List<string> checks, string reportPath)
    {
        void Check(bool value, string description) { if (!value) throw new InvalidOperationException(description); checks.Add(description); }
        object Element(string name) => window.FindName(name);
        var config = (AppConfig)typeof(MainWindow).GetField("_config", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var apps = (Dictionary<int, (string Name, string Icon)>)typeof(MainWindow).GetField("_applications", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var messages = (IList)typeof(MainWindow).GetField("_messages", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var list = (ListView)Element("MessagesList");
        var appBox = (ComboBox)Element("ApplicationFilter");
        var timeBox = (ComboBox)Element("TimeFilter");
        void Invoke(string method) => typeof(MainWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { window, new System.Windows.RoutedEventArgs() });
        window.ResetFilters(); window.ClearLocalHistory(); config.NotificationsEnabled = false; config.ApplicationNotifications.Clear(); apps.Clear();
        const string icon = "pack://application:,,,/Assets/logo.png";
        apps[7] = ("短信转发", icon); apps[8] = ("服务状态", icon); apps[9] = ("暂无消息的应用", icon);
        var add = typeof(MainWindow).GetMethod("AddMessage", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var now = DateTime.Now;
        add.Invoke(window, new object[] { new WireMessage { id = 1000001, appid = 7, title = "Daily status", message = "今日收到的完整示例消息。", priority = 9, date = now.AddHours(-1).ToUniversalTime() } });
        add.Invoke(window, new object[] { new WireMessage { id = 1000002, appid = 8, title = "Daily status", message = "服务运行正常。", priority = 9, date = now.AddHours(-2).ToUniversalTime() } });
        add.Invoke(window, new object[] { new WireMessage { id = 1000003, appid = 7, title = "Older status", message = "八天前的示例消息。", priority = 5, date = now.AddDays(-8).ToUniversalTime() } });
        add.Invoke(window, new object[] { new WireMessage { id = 1000004, appid = 8, title = "Daily status", message = "两天前的示例消息。", priority = 8, date = now.AddDays(-2).ToUniversalTime() } });
        Check(list.Items.Count == 4 && window.GetKnownApplications().Any(x => x.Id == 9), "Application choices merge live history and catalog-only applications");
        appBox.SelectedItem = appBox.Items.Cast<ApplicationFilterChoice>().Single(x => x.Id == 7);
        Check(list.Items.Count == 2 && list.Items.Cast<GotifyMessage>().All(x => x.AppId == 7), "Application filter matches stable application IDs");
        timeBox.SelectedIndex = 3;
        Check(list.Items.Count == 1, "Application and rolling seven-day filters combine");
        ((TextBox)Element("SearchBox")).Text = "Older";
        Check(list.Items.Count == 0 && ((TextBlock)Element("CountLabel")).Text.Contains("匹配 0 / 4"), "Search combines with time/application filters and updates result counts");
        ((TextBox)Element("SearchBox")).Clear();
        ((CheckBox)Element("UnreadOnlyBox")).IsChecked = true;
        var recent = list.Items.Cast<GotifyMessage>().Single();
        window.SelectMessage(recent); PumpUntil(() => list.Items.Count == 0, TimeSpan.FromSeconds(2));
        Check(list.Items.Count == 0 && ((TextBox)Element("DetailBody")).Text == recent.Message, "Unread-only reading works alongside application and time filters");
        window.ResetFilters();
        Check(list.Items.Count == 4 && timeBox.SelectedIndex == 0 && appBox.SelectedIndex == 0, "Clear filters resets search, unread, application and time conditions together");
        timeBox.SelectedIndex = 5;
        string date = now.AddDays(-2).ToString("yyyy-MM-dd");
        ((TextBox)Element("TimeFromBox")).Text = date; ((TextBox)Element("TimeUntilBox")).Text = date;
        Invoke("ApplyTime_Click");
        Check(list.Items.Count == 1 && ((GotifyMessage)list.Items[0]).Id == 1000004, "A custom date range includes the entire specified end date");
        ((TextBox)Element("TimeUntilBox")).Text = now.AddDays(-3).ToString("yyyy-MM-dd"); Invoke("ApplyTime_Click");
        Check(list.Items.Count == 1 && ((TextBlock)Element("TimeFilterHint")).Text.Contains("不能早于"), "Reversed custom ranges preserve the previous applied results and show an error");
        ((TextBox)Element("TimeFromBox")).Text = "invalid"; Invoke("ApplyTime_Click");
        Check(list.Items.Count == 1 && ((TextBlock)Element("TimeFilterHint")).Text.Contains("格式"), "Invalid custom time text cannot silently replace the active range");
        var fixedNow = new DateTime(2026, 11, 6, 12, 0, 0);
        Check(MessageTimeFilter.Matches(fixedNow.AddHours(-24), 2, fixedNow, null, null) && !MessageTimeFilter.Matches(fixedNow.AddHours(-24).AddTicks(-1), 2, fixedNow, null, null) && !MessageTimeFilter.Matches(fixedNow.AddTicks(1), 2, fixedNow, null, null), "Rolling 24-hour bounds include the start and exclude future timestamps");
        Check(MessageTimeFilter.Matches(fixedNow.Date, 1, fixedNow, null, null) && !MessageTimeFilter.Matches(fixedNow.Date.AddDays(1), 1, fixedNow, null, null) && MessageTimeFilter.Matches(fixedNow.AddDays(-30), 4, fixedNow, null, null), "Today and thirty-day preset boundaries are correct");
        Check(MessageTimeFilter.TryRange("2026-11-06 09:30", "2026-11-06 09:30", out var from, out var until, out _) && from == fixedNow.Date.AddHours(9.5) && until == from.Value.AddMinutes(1) && !MessageTimeFilter.TryRange("2025-02-29", "", out _, out _, out _), "Custom minute ranges include the full minute and reject invalid calendar dates");
        Check(MessageTimeFilter.TryRange("", "2026-11-06", out from, out until, out _) && from == null && until == fixedNow.Date.AddDays(1) && MessageTimeFilter.TryRange("2026-11-06", "", out from, out until, out _) && until == null, "Custom ranges support an open start or open end");
        window.ResetFilters();
        appBox.SelectedItem = appBox.Items.Cast<ApplicationFilterChoice>().Single(x => x.Id == 7);
        apps[7] = ("短信转发（已更新）", icon); Invoke("ResetFilters_Click");
        appBox.SelectedItem = appBox.Items.Cast<ApplicationFilterChoice>().Single(x => x.Id == 7);
        Check(list.Items.Count == 2 && ((ApplicationFilterChoice)appBox.SelectedItem).Name.Contains("已更新"), "Application metadata refresh updates labels without changing ID-based matching");
        config.ApplicationNotifications[7] = ApplicationNotificationMode.Enabled; config.ApplicationNotifications[8] = ApplicationNotificationMode.Disabled; config.ApplicationNotifications[99] = ApplicationNotificationMode.Enabled;
        Check(MainWindow.ShouldNotify(config, false, 7, 9) && !MainWindow.ShouldNotify(config, false, 8, 9) && !MainWindow.ShouldNotify(config, false, 9, 9), "An application can enable reminders while the default is off; disabled and unconfigured apps remain silent");
        config.NotificationsEnabled = true;
        Check(!MainWindow.ShouldNotify(config, false, 8, 9) && MainWindow.ShouldNotify(config, false, 9, 9), "An application can disable reminders while other applications follow the enabled default");
        Check(Enumerable.Range(0, 11).All(priority => MainWindow.ShouldNotify(config, false, 7, priority) == (priority >= 8)) && !MainWindow.ShouldNotify(config, false, 7, 11) && !MainWindow.ShouldNotify(config, true, 7, 9), "Application overrides preserve priority 8–10 and the tray pause stops every application");
        typeof(MainWindow).GetField("_lastNotifiedMessage", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, null);
        add.Invoke(window, new object[] { new WireMessage { id = 1000005, appid = 8, title = "静默应用的消息", message = "关闭提醒后仍接收、保存并标记未读。", priority = 9, date = now.ToUniversalTime() } });
        string profile = (string)typeof(MainWindow).GetField("_dataDir", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        Check(messages.Cast<GotifyMessage>().Any(x => x.Id == 1000005 && x.IsUnread) && File.ReadAllText(Path.Combine(profile, "messages.json")).Contains("1000005") && typeof(MainWindow).GetField("_lastNotifiedMessage", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window) == null, "A muted application still receives, saves and marks realtime messages unread without a notification");
        config.Save(Path.Combine(profile, "config.json")); var loaded = AppConfig.Load(Path.Combine(profile, "config.json"));
        Check(loaded.ApplicationNotifications.Count == 3 && loaded.ApplicationNotifications[8] == ApplicationNotificationMode.Disabled && loaded.ClientToken == config.ClientToken, "Application notification rules persist alongside DPAPI-protected tokens");
        File.WriteAllText(Path.Combine(profile, "legacy-config.json"), "{\"NotificationsEnabled\":false}");
        Check(AppConfig.Load(Path.Combine(profile, "legacy-config.json")).ApplicationNotifications.Count == 0, "Older configurations without application rules retain their existing default notification setting");
        var settings = new SettingsWindow(config, applications: window.GetKnownApplications());
        settings.Config.ApplicationNotifications[7] = ApplicationNotificationMode.Disabled;
        Check(config.ApplicationNotifications[7] == ApplicationNotificationMode.Enabled, "Canceling settings cannot mutate the original application rules"); settings.Close();
        var rulesWindow = new ApplicationNotificationsWindow(window.GetKnownApplications(), config.ApplicationNotifications, true);
        rulesWindow.Rows.Single(x => x.Application.Id == 7).Mode = ApplicationNotificationMode.Disabled;
        rulesWindow.Rows.Single(x => x.Application.Id == 8).Mode = ApplicationNotificationMode.FollowDefault;
        var rules = rulesWindow.BuildRules();
        Check(rules[7] == ApplicationNotificationMode.Disabled && !rules.ContainsKey(8) && rules[99] == ApplicationNotificationMode.Enabled && config.ApplicationNotifications[7] == ApplicationNotificationMode.Enabled, "The per-application editor preserves unknown IDs, removes inherited overrides and isolates canceled edits");
        ThemeManager.Apply("Dark"); Render((System.Windows.FrameworkElement)rulesWindow.Content, Path.ChangeExtension(reportPath, ".application-rules-dark.png"), 576, 480);
        var row = rulesWindow.Rows.Single(x => x.Application.Id == 8);
        var ruleList = (ListView)rulesWindow.FindName("ApplicationRulesList");
        var container = ruleList.ItemContainerGenerator.ContainerFromItem(row);
        var modeBox = FindVisual<ComboBox>(container)!;
        modeBox.SelectedValue = ApplicationNotificationMode.Enabled;
        Check(row.Mode == ApplicationNotificationMode.Enabled && rulesWindow.BuildRules()[8] == ApplicationNotificationMode.Enabled, "Changing a rendered application selector updates the editable notification rule");
        ThemeManager.Apply("Light"); Render((System.Windows.FrameworkElement)rulesWindow.Content, Path.ChangeExtension(reportPath, ".application-rules-light.png"), 576, 480); rulesWindow.Close();
        Check(!loaded.ResetApplicationRulesForConnection(loaded.ServerUrl + "/", loaded.ClientToken) && loaded.ApplicationNotifications.Count == 3, "Keeping the same connection preserves application rules");
        loaded.ClientToken = "different-client";
        Check(loaded.ResetApplicationRulesForConnection(config.ServerUrl, config.ClientToken) && loaded.ApplicationNotifications.Count == 0, "Changing the client identity clears application-ID rules from the previous account");
        loaded.ApplicationNotifications[7] = ApplicationNotificationMode.Disabled; loaded.ServerUrl = "https://example.invalid";
        Check(loaded.ResetApplicationRulesForConnection(config.ServerUrl, loaded.ClientToken) && loaded.ApplicationNotifications.Count == 0, "Changing servers clears application-ID rules from the previous server");
        window.ResetFilters(); timeBox.SelectedIndex = 3;
        appBox.SelectedItem = appBox.Items.Cast<ApplicationFilterChoice>().Single(x => x.Id == 7);
        window.DismissNotice(); window.SelectMessage(messages.Cast<GotifyMessage>().Single(x => x.Id == 1000001));
        ((System.Windows.Controls.Button)Element("ThemeButton")).Content = "深色模式";
        Render((System.Windows.FrameworkElement)window.Content, Path.ChangeExtension(reportPath, ".filters-light.png"), 1120, 740);
        ThemeManager.Apply("Dark"); ((System.Windows.Controls.Button)Element("ThemeButton")).Content = "浅色模式";
        Render((System.Windows.FrameworkElement)window.Content, Path.ChangeExtension(reportPath, ".filters-dark.png"), 1120, 740);
        timeBox.SelectedIndex = 5; ((TextBox)Element("TimeFromBox")).Text = date; ((TextBox)Element("TimeUntilBox")).Text = date; Invoke("ApplyTime_Click");
        Render((System.Windows.FrameworkElement)window.Content, Path.ChangeExtension(reportPath, ".filters-custom-compact.png"), 912, 570);
        window.ResetFilters(); config.NotificationsEnabled = false; config.ApplicationNotifications.Clear(); ThemeManager.Apply("Light");
    }

    private static T? FindVisual<T>(System.Windows.DependencyObject? root) where T : System.Windows.DependencyObject
    {
        if (root == null) return null;
        if (root is T match) return match;
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var found = FindVisual<T>(System.Windows.Media.VisualTreeHelper.GetChild(root, i));
            if (found != null) return found;
        }
        return null;
    }

    private static void CheckMenuAndStatus(MainWindow window, List<string> checks, string reportPath)
    {
        void Check(bool value, string description) { if (!value) throw new InvalidOperationException(description); checks.Add(description); }
        var badge = (System.Windows.Controls.Border)window.FindName("ConnectionBadge");
        var dot = (System.Windows.Shapes.Ellipse)window.FindName("StatusDot");
        var label = (TextBlock)window.FindName("StatusLabel");
        var root = (System.Windows.FrameworkElement)window.Content;
        var setStatus = typeof(MainWindow).GetMethod("SetStatus", BindingFlags.Instance | BindingFlags.NonPublic)!;
        ThemeManager.Apply("Dark");
        foreach (var state in new[] { "正在连接", "60 秒后重连", "已连接" })
        {
            setStatus.Invoke(window, new object[] { state, state == "已连接" ? System.Windows.Media.Colors.SeaGreen : System.Windows.Media.Colors.DarkOrange });
            Render(root, Path.ChangeExtension(reportPath, ".status-dark.png"), 1120, 740, 1.25);
            double dotCenter = dot.TranslatePoint(new System.Windows.Point(0, dot.ActualHeight / 2), badge).Y;
            double textCenter = label.TranslatePoint(new System.Windows.Point(0, label.ActualHeight / 2), badge).Y;
            Check(Math.Abs(dotCenter - textCenter) <= 0.75, $"Status dot and text are vertically centered: {state}");
        }
        var menus = new[] { ((System.Windows.Controls.Button)window.FindName("MoreButton")).ContextMenu, ((ListView)window.FindName("MessagesList")).ContextMenu };
        foreach (string theme in new[] { "Dark", "Light", "Dark" })
        {
            ThemeManager.Apply(theme);
            foreach (string name in new[] { "ApplicationFilter", "TimeFilter" })
            {
                var combo = (ComboBox)window.FindName(name);
                combo.IsDropDownOpen = true;
                try
                {
                    PumpUntil(() => false, TimeSpan.FromMilliseconds(100));
                    var popup = (System.Windows.Controls.Primitives.Popup)combo.Template.FindName("PART_Popup", combo);
                    var surface = (System.Windows.Controls.Border)popup.Child;
                    Check(((System.Windows.Media.SolidColorBrush)surface.Background).Color == ((System.Windows.Media.SolidColorBrush)System.Windows.Application.Current.Resources["SurfaceBrush"]).Color, $"{theme} {name} dropdown follows the current theme after reopening");
                }
                finally { combo.IsDropDownOpen = false; }
            }
            for (int i = 0; i < menus.Length; i++)
            {
                var menu = menus[i]!;
                menu.Placement = System.Windows.Controls.Primitives.PlacementMode.AbsolutePoint;
                menu.HorizontalOffset = 100; menu.VerticalOffset = 100;
                menu.IsOpen = true;
                try
                {
                    menu.ApplyTemplate();
                    Render(menu, Path.ChangeExtension(reportPath, $".menu-{theme.ToLowerInvariant()}-{i}.png"), 230, i == 0 ? 94 : 84, 1.25);
                    var surface = menu.Template.FindName("MenuSurface", menu) as System.Windows.Controls.Border;
                    var expected = (System.Windows.Media.SolidColorBrush)System.Windows.Application.Current.Resources["SurfaceBrush"];
                    bool valid = surface != null && ((System.Windows.Media.SolidColorBrush)surface.Background).Color == expected.Color && menu.Items.OfType<MenuItem>().All(item => item.ActualHeight >= 36 && item.Template.FindName("MenuRow", item) != null);
                    if (!valid) throw new InvalidOperationException($"{theme} menu mismatch: actual={surface?.Background}, expected={expected}, frozen={expected.IsFrozen}, shared={ReferenceEquals(surface?.Background, expected)}, rows={string.Join(",", menu.Items.OfType<MenuItem>().Select(item => $"{item.ActualHeight}/{item.Template.FindName("MenuRow", item) != null}"))}");
                    Check(true, $"{theme} {(i == 0 ? "More" : "message")} menu uses themed chrome and complete item templates");
                    if (i == 0)
                    {
                        Check(menu.Items.OfType<Separator>().All(separator => separator.Template.FindName("MenuDivider", separator) is System.Windows.Controls.Border divider && ((System.Windows.Media.SolidColorBrush)divider.Background).Color == ((System.Windows.Media.SolidColorBrush)System.Windows.Application.Current.Resources["LineBrush"]).Color), $"{theme} menu separators follow the palette after reopening");
                    }
                }
                finally { menu.IsOpen = false; }
            }
        }
        checks.Add("Status alignment and both context menus rendered at 125 percent scale");
        ThemeManager.Apply("Light");
    }

    private static void Render(System.Windows.FrameworkElement root, string path, int width, int height, double scale = 1)
    {
        root.Measure(new System.Windows.Size(width, height)); root.Arrange(new System.Windows.Rect(0, 0, width, height)); root.UpdateLayout();
        PumpUntil(() => false, TimeSpan.FromMilliseconds(100)); root.UpdateLayout();
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale), 96 * scale, 96 * scale, System.Windows.Media.PixelFormats.Pbgra32); bitmap.Render(root);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var output = File.Create(path); encoder.Save(output);
    }
    private static double Contrast(string foreground, string background)
    {
        double L(string key) { var color = ((System.Windows.Media.SolidColorBrush)System.Windows.Application.Current.Resources[key]).Color; double Linear(byte c) { double s = c / 255d; return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); } return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B); }
        double a = L(foreground), b = L(background); return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    private static void CheckTransport(MainWindow window, List<string> checks)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        int streams = 0;
        int historyRequests = 0;
        bool posted = false; int postCount = 0; int olderRequests = 0; int applicationRequests = 0;
        Exception? serverError = null;
        using var logoStream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/logo.png"))!.Stream;
        using var logoMemory = new MemoryStream(); logoStream.CopyTo(logoMemory); var logoBytes = logoMemory.ToArray();
        var server = Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    using var client = await listener.AcceptTcpClientAsync(stop.Token);
                    using var network = client.GetStream();
                    network.ReadTimeout = 4000;
                    var header = new StringBuilder();
                    while (!header.ToString().EndsWith("\r\n\r\n"))
                    {
                        int b = network.ReadByte();
                        if (b < 0 || header.Length > 16000) throw new IOException("Invalid mock HTTP headers");
                        header.Append((char)b);
                    }
                    var lines = header.ToString().Split("\r\n");
                    var request = lines[0].Split(' ');
                    var url = new Uri("http://localhost" + request[1]);
                    var fields = lines.Skip(1).Where(x => x.Contains(':')).ToDictionary(x => x[..x.IndexOf(':')], x => x[(x.IndexOf(':') + 1)..].Trim(), StringComparer.OrdinalIgnoreCase);
                    if (url.AbsolutePath == "/stream")
                    {
                        if (url.Query != "?token=diagnostic-client-secret") throw new Exception("Client token not used for WebSocket");
                        var accept = Convert.ToBase64String(System.Security.Cryptography.SHA1.HashData(Encoding.ASCII.GetBytes(fields["Sec-WebSocket-Key"] + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                        await network.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"), stop.Token);
                        using var ws = WebSocket.CreateFromStream(network, true, null, TimeSpan.FromSeconds(30));
                        int sequence = Interlocked.Increment(ref streams);
                        var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new WireMessage { id = 800000 + sequence, title = "Mock WebSocket", message = "Fragmented local message", priority = 8, date = DateTime.UtcNow }));
                        await ws.SendAsync(new ArraySegment<byte>(payload, 0, 8), WebSocketMessageType.Text, false, stop.Token);
                        await ws.SendAsync(new ArraySegment<byte>(payload, 8, payload.Length - 8), WebSocketMessageType.Text, true, stop.Token);
                        await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Reconnect check", stop.Token);
                        ws.Dispose();
                    }
                    else if (url.AbsolutePath == "/message" && request[0] == "GET")
                    {
                        if (!fields.TryGetValue("X-Gotify-Key", out var token) || token != "diagnostic-client-secret" || !url.Query.StartsWith("?limit=100")) throw new Exception("History request must use Client Token and limit=100");
                        if (url.Query.Contains("&since="))
                        {
                            int cursor = int.Parse(url.Query.Split("&since=")[1]);
                            Interlocked.Increment(ref olderRequests);
                            var older = JsonSerializer.SerializeToUtf8Bytes(new { messages = cursor == 799999 ? new[] { new WireMessage { id = 799998, appid = 0, title = "Older page", message = "Older server history", priority = 4, date = DateTime.UtcNow.AddDays(-1) } } : Array.Empty<WireMessage>() });
                            await network.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {older.Length}\r\nConnection: close\r\n\r\n"), stop.Token);
                            await network.WriteAsync(older, stop.Token);
                            continue;
                        }
                        int number = Interlocked.Increment(ref historyRequests);
                        if (number == 3)
                        {
                            await network.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 401 Unauthorized\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), stop.Token);
                        }
                        else
                        {
                            // Duplicate the stream message to exercise merging across the two transports.
                            var data = JsonSerializer.SerializeToUtf8Bytes(new { messages = new[] {
                                new WireMessage { id = 800000 + streams, title = "Mock WebSocket", message = "Duplicate stream event", priority = 8, date = DateTime.UtcNow },
                                new WireMessage { id = 799999, title = "Offline history", message = "Missed while offline", priority = 10, date = DateTime.UtcNow.AddMinutes(-5) }
                            }});
                            await network.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {data.Length}\r\nConnection: close\r\n\r\n"), stop.Token);
                            await network.WriteAsync(data, stop.Token);
                        }
                    }
                    else if (url.AbsolutePath == "/application" || url.AbsolutePath == "/image/mock.png")
                    {
                        if (!fields.TryGetValue("X-Gotify-Key", out var token) || token != "diagnostic-client-secret") throw new Exception("Application metadata/image authentication mismatch");
                        var data = url.AbsolutePath == "/application" ? JsonSerializer.SerializeToUtf8Bytes(new[] { new WireApplication { id = 0, name = "Mock App", image = "image/mock.png" } }) : logoBytes;
                        Interlocked.Increment(ref applicationRequests);
                        await network.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: {(url.AbsolutePath == "/application" ? "application/json" : "image/png")}\r\nContent-Length: {data.Length}\r\nConnection: close\r\n\r\n"), stop.Token);
                        await network.WriteAsync(data, stop.Token);
                    }
                    else if (url.AbsolutePath == "/message")
                    {
                        if (!fields.TryGetValue("X-Gotify-Key", out var appToken) || appToken != "diagnostic-app-secret" || url.Query != "" || request[0] != "POST") throw new Exception("Application token or REST verb mismatch");
                        // HttpClient may send HTTP/1.1 chunked JSON without a Content-Length.
                        using var bodyStream = new MemoryStream();
                        if (fields.TryGetValue("Content-Length", out var length))
                        {
                            var bodyBytes = new byte[int.Parse(length)];
                            await network.ReadExactlyAsync(bodyBytes, stop.Token);
                            bodyStream.Write(bodyBytes);
                        }
                        else
                        {
                            while (true)
                            {
                                var chunkLine = new StringBuilder();
                                int c;
                                while ((c = network.ReadByte()) != '\n') { if(c < 0) throw new IOException(); if(c != '\r') chunkLine.Append((char)c); }
                                int chunkSize = Convert.ToInt32(chunkLine.ToString().Split(';')[0], 16);
                                if (chunkSize == 0) { network.ReadByte(); network.ReadByte(); break; }
                                var chunk = new byte[chunkSize];
                                await network.ReadExactlyAsync(chunk, stop.Token);
                                bodyStream.Write(chunk);
                                network.ReadByte(); network.ReadByte();
                            }
                        }
                        using var doc = JsonDocument.Parse(bodyStream.ToArray());
                        var body = doc.RootElement;
                        if (body.GetProperty("title").GetString() != "REST check" || body.GetProperty("message").GetString() != "Synthetic REST body" || body.GetProperty("priority").GetInt32() != 9) throw new Exception("REST payload mismatch");
                        int postNumber = Interlocked.Increment(ref postCount);
                        if (postNumber == 2)
                        {
                            await network.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), stop.Token);
                            continue;
                        }
                        await Task.Delay(300, stop.Token);
                        var response = Encoding.UTF8.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 9\r\nConnection: close\r\n\r\n{\"id\":42}");
                        await network.WriteAsync(response, stop.Token);
                        posted = true;
                    }
                    else throw new Exception("Unexpected mock route");
                }
            }
            catch (Exception ex) { if (!stop.IsCancellationRequested) serverError = ex; }
        });
        try
        {
            var config = (AppConfig)typeof(MainWindow).GetField("_config", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            config.ServerUrl = $"http://localhost:{port}";
            config.NotificationsEnabled = true;
            typeof(MainWindow).GetField("_notificationsPaused", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, false);
            typeof(MainWindow).GetMethod("StartConnection", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
            var list = (ListView)window.FindName("MessagesList");
            PumpUntil(() => (Volatile.Read(ref historyRequests) >= 2 && list.Items.Cast<GotifyMessage>().Any(x => x.Id == 800002)) || serverError != null, TimeSpan.FromSeconds(20));
            if (serverError != null) throw serverError;
            if (streams < 2 || !list.Items.Cast<GotifyMessage>().Any(x => x.Title == "Mock WebSocket")) throw new Exception("WebSocket reconnect/receive failed");
            checks.Add("Local WebSocket authentication, fragmented JSON receive, and automatic reconnect");
            var merged = list.Items.Cast<GotifyMessage>().ToList();
            if (!merged.Any(x => x.Id == 799999) || merged.Count(x => x.Id == 800001) != 1 || merged.Count(x => x.Id == 800002) != 1 || !merged.Select(x => x.Id).SequenceEqual(merged.Select(x => x.Id).OrderByDescending(x => x))) throw new Exception("Recent history merge, deduplication or ordering failed");
            checks.Add("Automatic recent-100 GET on connect and reconnect with Client Token; history and live messages merged once in descending ID order");
            if (typeof(MainWindow).GetField("_lastNotifiedMessage", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window) != null) throw new Exception("History replay unexpectedly triggered a notification");
            checks.Add("Historical high-priority messages do not trigger notifications even when notifications are enabled");
            config.NotificationsEnabled = false;
            PumpUntil(() => (historyRequests >= 3 && list.Items.Cast<GotifyMessage>().Any(x => x.Id == 800003)) || serverError != null, TimeSpan.FromSeconds(20));
            if (serverError != null) throw serverError;
            if (!list.Items.Cast<GotifyMessage>().Any(x => x.Id == 800003) || !((TextBlock)window.FindName("NoticeLabel")).Text.Contains("拉取失败")) throw new Exception($"Failed history request interrupted live receive: streams={streams}; history={historyRequests}; ids={string.Join(",", list.Items.Cast<GotifyMessage>().Select(x => x.Id))}; notice={((TextBlock)window.FindName("NoticeLabel")).Text}");
            checks.Add("History HTTP 401 is reported without interrupting live WebSocket reception");
            PumpUntil(() => list.Items.Cast<GotifyMessage>().Any(x => x.AppName == "Mock App" && File.Exists(x.AppIconPath)), TimeSpan.FromSeconds(10));
            if (serverError != null) throw serverError;
            if (!list.Items.Cast<GotifyMessage>().Any(x => x.AppName == "Mock App" && File.Exists(x.AppIconPath))) throw new Exception("Application name/icon caching failed");
            checks.Add("Authenticated application catalog and image download update message names and cached PNG icons");
            ((CancellationTokenSource?)typeof(MainWindow).GetField("_streamCts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window))?.Cancel();
            var fetch = typeof(MainWindow).GetMethod("FetchHistoryAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            for (int page = 0; page < 2; page++)
            {
                var task = (Task)fetch.Invoke(window, new object[] { new Uri(config.ServerUrl), config.ClientToken, true, CancellationToken.None })!;
                PumpUntil(() => task.IsCompleted || serverError != null, TimeSpan.FromSeconds(10)); task.GetAwaiter().GetResult();
            }
            if (serverError != null) throw serverError;
            if (olderRequests != 2 || !list.Items.Cast<GotifyMessage>().Any(x => x.Id == 799998) || ((System.Windows.Controls.Button)window.FindName("OlderButton")).IsEnabled) throw new Exception("Older page cursor or end-of-history behavior failed");
            checks.Add("Older messages use the minimum ID cursor, merge correctly and disable pagination at the end");
            if (((TextBlock)window.FindName("LastSyncLabel")).Text == "尚未同步") throw new Exception("Last synchronization timestamp missing");
            checks.Add("Successful server synchronization displays a timestamp independent of error text");
            ((TextBox)window.FindName("TitleBox")).Text = "REST check";
            ((TextBox)window.FindName("MessageContentBox")).Text = "Synthetic REST body";
            ((TextBox)window.FindName("PriorityBox")).Text = "9";
            typeof(MainWindow).GetMethod("Send_Click", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { window, new System.Windows.RoutedEventArgs() });
            if (((System.Windows.Controls.Button)window.FindName("SendButton")).IsEnabled) throw new Exception("Send button not disabled during submission");
            typeof(MainWindow).GetMethod("Send_Click", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { window, new System.Windows.RoutedEventArgs() });
            PumpUntil(() => (posted && ((TextBlock)window.FindName("NoticeLabel")).Text == "消息已发送") || serverError != null, TimeSpan.FromSeconds(10));
            if (serverError != null) throw serverError;
            if (!posted || ((TextBox)window.FindName("MessageContentBox")).Text != "") throw new Exception("REST submission did not complete");
            checks.Add("Local REST POST with independent Application Token, title/content/priority payload, and successful composer reset");
            if (postCount != 1 || !((System.Windows.Controls.Button)window.FindName("SendButton")).IsEnabled) throw new Exception("Duplicate submission was not suppressed or button was not restored");
            checks.Add("Rapid repeated sends create one request and restore the send button after success");
            ((TextBox)window.FindName("TitleBox")).Text = "REST check";
            ((TextBox)window.FindName("MessageContentBox")).Text = "Synthetic REST body";
            typeof(MainWindow).GetMethod("Send_Click", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { window, new System.Windows.RoutedEventArgs() });
            PumpUntil(() => ((TextBlock)window.FindName("NoticeLabel")).Text.Contains("发送失败"), TimeSpan.FromSeconds(10));
            if (((TextBox)window.FindName("MessageContentBox")).Text != "Synthetic REST body" || !((TextBlock)window.FindName("NoticeLabel")).Text.Contains("Application Token") || !((System.Windows.Controls.Button)window.FindName("SendButton")).IsEnabled) throw new Exception("Failed send did not preserve the composer or report token permissions");
            checks.Add("Forbidden REST sends preserve the draft, report Application Token permissions and restore the send button");
        }
        finally
        {
            ((CancellationTokenSource?)typeof(MainWindow).GetField("_streamCts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window))?.Cancel();
            stop.Cancel();
            listener.Stop();
            try { server.Wait(TimeSpan.FromSeconds(2)); } catch { }
        }
    }

    private static void PumpUntil(Func<bool> complete, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(20), DispatcherPriority.Background, (_, _) => { if (complete() || DateTime.UtcNow >= deadline) frame.Continue = false; }, Dispatcher.CurrentDispatcher);
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
    }
}




