using System.Collections;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using TextBox = System.Windows.Controls.TextBox;

namespace GotifyDesktop;

internal static partial class Diagnostics
{
    private static IEnumerable<Inline> DocumentInlines(InlineCollection inlines)
    {
        foreach (Inline inline in inlines)
        {
            yield return inline;
            if (inline is Span span) foreach (var child in DocumentInlines(span.Inlines)) yield return child;
        }
    }
    private static void CheckRichMessagesAndPolicy(MainWindow window, List<string> checks, string reportPath)
    {
        void Check(bool value, string description) { if (!value) throw new InvalidOperationException(description); checks.Add(description); }
        object Element(string name) => window.FindName(name);
        var config = (AppConfig)typeof(MainWindow).GetField("_config", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var messages = (IList)typeof(MainWindow).GetField("_messages", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var add = typeof(MainWindow).GetMethod("AddMessage", BindingFlags.Instance | BindingFlags.NonPublic)!;
        void Invoke(string method) => typeof(MainWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { window, new RoutedEventArgs() });
        config.NotificationsEnabled = false; config.ApplicationNotifications.Clear(); config.NotificationOptions = new();
        window.ResetFilters(); window.ClearLocalHistory(); window.ToggleComposer(false);
        // Load the real WPF text view without taking focus or displaying a test window.
        var root = (FrameworkElement)window.Content;
        root.Width = 1120; root.Height = 740;
        window.ShowActivated = false; window.ShowInTaskbar = false; window.Opacity = 0;
        window.Show(); PumpUntil(() => false, TimeSpan.FromMilliseconds(100));
        string markdown = "# 部署完成\n**状态正常**，验证码：482619\n\n- [查看发布记录](https://example.invalid/releases?a=1&b=2)\n> 本条用于离线界面检查。\n\n`inline code`\n```text\n<sample> & untouched\n```\n![示例图片](https://images.example.invalid/pic.png)\n<script>never executed</script>\n[unsafe](javascript:alert)";
        var extras = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"client::display":{"contentType":"text/markdown"},"client::notification":{"click":{"url":"https://example.invalid/details?a=1&b=2"}}} """)!;
        var wire = JsonSerializer.Deserialize<WireMessage>(JsonSerializer.Serialize(new WireMessage { id = 2000001, appid = 7, title = "验证码与发布提醒", message = markdown, priority = 9, date = DateTime.UtcNow, extras = extras }))!;
        add.Invoke(window, new object[] { wire });
        var selected = messages.Cast<GotifyMessage>().Single(); window.SelectMessage(selected);
        string profile = (string)typeof(MainWindow).GetField("_dataDir", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var persisted = JsonSerializer.Deserialize<List<GotifyMessage>>(File.ReadAllText(Path.Combine(profile, "messages.json")))!.Single();
        Check(MessageActions.IsMarkdown(selected) && MessageActions.IsMarkdown(persisted) && persisted.Message == markdown && MessageActions.OriginalUrl(persisted)?.Query == "?a=1&b=2", "Wire extras and the exact source survive local history persistence");
        Check(MessageActions.VerificationCode(selected) == "482619" && MessageActions.VerificationCode(new() { Message = "Your verification code is 173940" }) == "173940" && MessageActions.VerificationCode(new() { Message = "735104 is your verification code" }) == "735104", "Verification code extraction supports Chinese and English context on either side");
        Check(MessageActions.VerificationCode(new() { Message = "订单号123456，电话13800138000" }) == "" && MessageActions.VerificationCode(new() { Message = "验证码已发送至尾号1234的手机" }) == "" && MessageActions.VerificationCode(new() { Message = "验证码123456，校验码654321" }) == "", "Unrelated numbers, masked-phone notices and ambiguous multiple codes do not expose a copy-code action");
        var smsTemplates = new[]
        {
            ("95588", "95588\n【示例平台】验证码：582413，请在5分钟内完成验证。如非本人操作，请联系客服95588。\nSIM2_示例运营商_+8613800000000\nSubId：1\n2026-11-07 20:23:18\nexample device", "582413"),
            ("106800123", "106800123\n【示例平台】动态码：947362，5分钟内有效。此动态码用于账号登录验证。\nSIM2_示例运营商_+8613800000000\nSubId：1\n2026-11-07 20:22:18\nexample device", "947362"),
            ("106800123456789", "106800123456789\n【示例平台】您本次的统一身份验证码是614829 ，请在5分钟内完成验证。\nSIM2_示例运营商_+8613800000000\nSubId：1\n2026-11-07 16:40:18\nexample device", "614829")
        };
        Check(smsTemplates.All(item => MessageActions.VerificationCode(new() { Title = item.Item1, Message = item.Item2 }) == item.Item3), "Forwarded SMS templates extract the body code and ignore sender, hotline, SIM, subscription ID, timestamp and device numbers");
        string? copied = null; Uri? opened = null;
        window.CopyCodeSink = value => copied = value; window.OpenUrlSink = value => opened = value;
        Invoke("CopyCode_Click"); Invoke("OpenOriginal_Click");
        Check(copied == "482619" && opened?.AbsolutePath == "/details" && ((Button)Element("CopyCodeButton")).Visibility == Visibility.Visible && ((Button)Element("OpenOriginalButton")).Visibility == Visibility.Visible, "Quick-action handlers copy only the recognized code and open the preferred extra URL");
        Check(((TextBox)Element("VerificationCodeBox")).Text == "482619" && ((System.Windows.Controls.Border)Element("VerificationCard")).Visibility == Visibility.Visible, "A separate verification-code card displays the exact value that will be copied");
        add.Invoke(window, new object[] { new WireMessage { id = 2000000, appid = 7, title = "另一条验证码短信", message = "【示例平台】动态码：735104，5分钟内有效。", priority = 9, date = DateTime.UtcNow } });
        add.Invoke(window, new object[] { new WireMessage { id = 1999999, appid = 7, title = "普通通知", message = "订单号123456，电话13800138000", priority = 5, date = DateTime.UtcNow } });
        add.Invoke(window, new object[] { new WireMessage { id = 1999998, appid = 7, title = "多个候选码", message = "验证码123456，校验码654321", priority = 5, date = DateTime.UtcNow } });
        var list = (System.Windows.Controls.ListView)Element("MessagesList");
        list.SelectedItem = selected;
        Button RowButton(GotifyMessage message)
        {
            list.ScrollIntoView(message); window.UpdateLayout(); PumpUntil(() => false, TimeSpan.FromMilliseconds(100));
            return FindVisual<Button>(list.ItemContainerGenerator.ContainerFromItem(message)) ?? throw new InvalidOperationException("List copy button was not instantiated");
        }
        var otherCode = messages.Cast<GotifyMessage>().Single(x => x.Id == 2000000);
        var rowButton = RowButton(otherCode);
        Check(rowButton.Visibility == Visibility.Visible && otherCode.IsUnread, "A recognized verification code exposes a list action before the message is opened");
        rowButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(copied == "735104" && ReferenceEquals(list.SelectedItem, selected) && otherCode.IsUnread && ((TextBox)Element("VerificationCodeBox")).Text == "482619", "Clicking an unselected row copies that row's code without changing the selected detail or unread state");
        RowButton(selected).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(copied == "482619", "Successive row copy actions never reuse a different message's verification code");
        Check(RowButton(messages.Cast<GotifyMessage>().Single(x => x.Id == 1999999)).Visibility == Visibility.Collapsed && RowButton(messages.Cast<GotifyMessage>().Single(x => x.Id == 1999998)).Visibility == Visibility.Collapsed, "Ordinary numbers and ambiguous codes do not display a list copy action");
        list.ScrollIntoView(selected); window.DismissNotice();
        root.Width = 960; root.Height = 660; ThemeManager.Apply("Dark");
        Render(root, Path.ChangeExtension(reportPath, ".list-code-compact-dark.png"), 960, 660);
        var compactButton = RowButton(selected);
        var buttonBounds = compactButton.TransformToAncestor(list).TransformBounds(new Rect(compactButton.RenderSize));
        Check(compactButton.ActualHeight >= 28 && buttonBounds.Left >= 0 && buttonBounds.Right <= list.ActualWidth, "The list verification-code button remains inside its message row at the minimum window width");
        root.Width = 1120; root.Height = 740; ThemeManager.Apply("Light");
        Render(root, Path.ChangeExtension(reportPath, ".list-code-light.png"), 1120, 740);
        window.CopyCodeSink = null; window.OpenUrlSink = null;
        var rich = (System.Windows.Controls.RichTextBox)Element("DetailRichBody");
        var inlines = rich.Document.Blocks.OfType<Paragraph>().SelectMany(p => DocumentInlines(p.Inlines)).ToList();
        var links = inlines.OfType<Hyperlink>().ToList();
        string rendered = new TextRange(rich.Document.ContentStart, rich.Document.ContentEnd).Text;
        Check(rich.Document.Blocks.OfType<Paragraph>().Any(p => p.FontSize > 14) && inlines.Any(i => i.FontWeight == FontWeights.Bold) && rendered.Contains("<sample> & untouched") && !rendered.Contains("<script>"), "Markdown headings, bold text and literal code blocks render while HTML tags stay inactive");
        Check(links.Count == 2 && links.All(link => link.NavigateUri.Scheme == "https") && links.Any(link => link.NavigateUri.Host == "images.example.invalid"), "Markdown links and image placeholders are clickable without creating embedded remote images or unsafe protocols");
        Check(MessageActions.SafeUrl("javascript:alert(1)") == null && MessageActions.SafeUrl("file:///C:/Windows/win.ini") == null && MessageActions.SafeUrl("https://user:password@example.invalid/") == null && MessageActions.SafeUrl("https://example.invalid/\r\n") == null, "External actions reject scripts, files, credential URLs and control characters");
        var plainDoc = MessageDocument.Create(new GotifyMessage { Message = "请查看 https://example.invalid/a?q=1。" }, _ => { });
        Check(plainDoc.Blocks.OfType<Paragraph>().SelectMany(p => DocumentInlines(p.Inlines)).OfType<Hyperlink>().Single().NavigateUri.AbsoluteUri == "https://example.invalid/a?q=1", "Plain-text links become clickable and exclude trailing Chinese punctuation");
        ((CheckBox)Element("ShowSourceBox")).IsChecked = true;
        Check(((TextBox)Element("DetailBody")).Visibility == Visibility.Visible && ((TextBox)Element("DetailBody")).Text == markdown && rich.Visibility == Visibility.Collapsed, "Source mode preserves the full original message instead of copying rendered text");
        ((CheckBox)Element("ShowSourceBox")).IsChecked = false;
        var toast = System.Xml.Linq.XElement.Parse(NativeToast.Payload(selected));
        Check(toast.Attribute("launch")!.Value == "gotify-desktop://message?id=2000001" && toast.Descendants("action").Single().Attribute("arguments")!.Value == MessageActions.OriginalUrl(selected)!.AbsoluteUri, "Toast body activation still opens the message and its explicit action opens the original page");
        Check(!System.Xml.Linq.XElement.Parse(NativeToast.Payload(new() { Message = "No links", Extras = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"client::notification":{"click":{"url":"javascript:alert"}}}""") })).Descendants("action").Any(), "Unsafe or absent click URLs never create a toast action");
        window.DismissNotice(); ThemeManager.Apply("Light"); ((Button)Element("ThemeButton")).Content = "深色模式";
        Render((FrameworkElement)window.Content, Path.ChangeExtension(reportPath, ".rich-light.png"), 1120, 740);
        ThemeManager.Apply("Dark"); ((Button)Element("ThemeButton")).Content = "浅色模式";
        Render((FrameworkElement)window.Content, Path.ChangeExtension(reportPath, ".rich-dark.png"), 1120, 740);
        rich.ScrollToEnd(); PumpUntil(() => false, TimeSpan.FromMilliseconds(100)); window.UpdateLayout();
        var outer = (ScrollViewer)Element("DetailScroll");
        File.WriteAllText(Path.ChangeExtension(reportPath, ".rich-layout.json"), JsonSerializer.Serialize(new { rich.ActualHeight, rich.ExtentHeight, rich.ViewportHeight, rich.VerticalOffset, outer.ScrollableHeight }));
        Check(rich.ActualHeight >= Math.Min(rich.ExtentHeight, 360) && (rich.ExtentHeight <= rich.ViewportHeight || rich.VerticalOffset > 0), "Long rich messages remain accessible through their reading scrollbar"); rich.ScrollToHome();
        add.Invoke(window, new object[] { new WireMessage { id = 2000002, appid = 7, title = "Long reading check", message = string.Join("\n", Enumerable.Range(1, 100).Select(i => "Message line " + i)), priority = 5, date = DateTime.UtcNow, extras = extras } });
        window.SelectMessage(messages.Cast<GotifyMessage>().Single(x => x.Id == 2000002));
        Render((FrameworkElement)window.Content, Path.ChangeExtension(reportPath, ".rich-long.png"), 1120, 740);
        rich.ScrollToEnd(); PumpUntil(() => false, TimeSpan.FromMilliseconds(100));
        Check(rich.ActualHeight <= 360 && rich.ViewportHeight > 0 && rich.VerticalOffset > 0 && ((System.Windows.Controls.Border)Element("VerificationCard")).Visibility == Visibility.Collapsed, "Longer-than-viewport messages scroll to the end and a non-code message clears the previous verification card");
        window.SelectMessage(selected);

        var start = new DateTimeOffset(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
        GotifyMessage Event(long id, int app = 7, int priority = 9) => new() { Id = id, AppId = app, AppName = "示例应用", Title = "事件 " + id, Message = "测试内容", Priority = priority, Extras = extras };
        var buffer = new NotificationBurstBuffer();
        Check(buffer.Accept(Event(1), start).Count == 1 && buffer.Accept(Event(2, priority: 10), start.AddSeconds(1)).Count == 0 && buffer.Accept(Event(3), start.AddSeconds(9)).Count == 0, "Burst aggregation sends the first message immediately and buffers same-application followups");
        Check(buffer.Accept(Event(4, 8), start.AddSeconds(2)).Single().AppId == 8 && buffer.Flush(start.AddSeconds(9.9)).Count == 0, "Different applications have independent windows and summaries never flush early");
        var summary = buffer.Flush(start.AddSeconds(10)).Single();
        Check(summary.Id == 3 && summary.Priority == 10 && summary.Title.Contains("3 条") && summary.Extras == extras && buffer.Flush(start.AddSeconds(20)).Count == 0, "A burst emits one summary with total count, latest target and highest priority, without duplicate summaries");
        Check(buffer.Accept(Event(5), start.AddSeconds(21)).Count == 1, "A later notification window starts with an immediate first alert");
        buffer.Accept(Event(6), start.AddSeconds(22)); buffer.Clear();
        Check(buffer.Flush(start.AddSeconds(40)).Count == 0, "Clearing the notification buffer cancels every pending summary");
        var options = new NotificationPreferences { QuietHoursEnabled = true, QuietStart = "22:00", QuietEnd = "08:00" };
        Check(options.IsQuiet(new(2026, 11, 6, 22, 0, 0)) && options.IsQuiet(new(2026, 11, 7, 7, 59, 59)) && !options.IsQuiet(new(2026, 11, 7, 8, 0, 0)) && !options.IsQuiet(new(2026, 11, 6, 21, 59, 59)), "Cross-midnight quiet hours include the start and exclude the end");
        options.QuietStart = "09:00"; options.QuietEnd = "18:00";
        Check(options.IsQuiet(new(2026, 11, 6, 12, 0, 0)) && !options.IsQuiet(new(2026, 11, 6, 18, 0, 0)), "Same-day quiet hours also respect their boundaries");
        config.NotificationsEnabled = true; config.NotificationOptions = options; var quietNow = new DateTime(2026, 11, 6, 12, 0, 0);
        Check(!MainWindow.ShouldNotify(config, false, 7, 9, quietNow) && !MainWindow.ShouldNotify(config, false, 7, 10, quietNow), "Quiet hours suppress both normal high-priority and priority-10 alerts by default");
        options.AllowPriority10DuringQuiet = true;
        Check(MainWindow.ShouldNotify(config, false, 7, 10, quietNow) && !MainWindow.ShouldNotify(config, false, 7, 9, quietNow) && !MainWindow.ShouldNotify(config, true, 7, 10, quietNow), "An explicit priority-10 exception bypasses quiet hours but cannot bypass the tray pause");
        config.ApplicationNotifications[7] = ApplicationNotificationMode.Disabled;
        Check(!MainWindow.ShouldNotify(config, false, 7, 10, quietNow), "Per-application silence still blocks critical quiet-hour exceptions"); config.ApplicationNotifications.Clear();
        config.Save(Path.Combine(profile, "config.json"));
        var saved = AppConfig.Load(Path.Combine(profile, "config.json"));
        Check(saved.NotificationOptions.QuietStart == "09:00" && saved.NotificationOptions.AllowPriority10DuringQuiet && saved.ClientToken == config.ClientToken, "Notification preferences persist without changing protected credentials");
        var settings = new SettingsWindow(config); settings.Config.NotificationOptions.QuietStart = "00:00";
        Check(config.NotificationOptions.QuietStart == "09:00", "Canceling general settings isolates pending notification preference changes"); settings.Close();
        var optionsWindow = new NotificationOptionsWindow(options);
        ((TextBox)optionsWindow.FindName("QuietStartBox")).Text = "25:00";
        Check(!optionsWindow.ApplyInputs() && options.QuietStart == "09:00", "Invalid quiet-hour input is rejected without mutating saved options");
        ((TextBox)optionsWindow.FindName("QuietStartBox")).Text = "22:00"; ((TextBox)optionsWindow.FindName("QuietEndBox")).Text = "08:00";
        Check(optionsWindow.ApplyInputs() && optionsWindow.Options.QuietStart == "22:00", "The reminder policy editor accepts a valid cross-midnight interval");
        Render((FrameworkElement)optionsWindow.Content, Path.ChangeExtension(reportPath, ".notification-options-dark.png"), 476, 450); optionsWindow.Close();

        config.NotificationOptions = new(); window.ClearLocalHistory();
        var delivered = new List<GotifyMessage>(); window.NotificationSink = delivered.Add;
        for (int i = 0; i < 3; i++) add.Invoke(window, new object[] { new WireMessage { id = 2100000 + i, appid = 20, title = "Burst", message = "A", priority = 9, date = DateTime.UtcNow } });
        add.Invoke(window, new object[] { new WireMessage { id = 2100003, appid = 21, title = "Other app", message = "B", priority = 9, date = DateTime.UtcNow } });
        Check(delivered.Count == 2 && messages.Count == 4, "The realtime path aggregates alerts independently while preserving every original message");
        window.FlushNotifications(DateTimeOffset.UtcNow.AddSeconds(11));
        Check(delivered.Count == 3 && delivered.Last().Id == 2100002 && messages.Count == 4, "The realtime summary targets the latest original message and is never inserted into history");
        add.Invoke(window, new object[] { new WireMessage { id = 2100004, appid = 20, title = "Pending", message = "C", priority = 9 } });
        add.Invoke(window, new object[] { new WireMessage { id = 2100005, appid = 20, title = "Pending", message = "D", priority = 9 } });
        config.ApplicationNotifications[20] = ApplicationNotificationMode.Disabled;
        int before = delivered.Count; window.FlushNotifications(DateTimeOffset.UtcNow.AddSeconds(11));
        Check(delivered.Count == before, "Pending summaries recheck current per-application rules before delivery");
        config.ApplicationNotifications.Clear(); config.NotificationOptions.AggregateBursts = false;
        for (int i = 0; i < 2; i++) add.Invoke(window, new object[] { new WireMessage { id = 2100006 + i, appid = 22, title = "Direct", message = "E", priority = 9 } });
        Check(delivered.Count == before + 2, "Turning burst aggregation off restores immediate individual notifications");
        window.NotificationSink = null; config.NotificationsEnabled = false; config.NotificationOptions = new(); window.ClearLocalHistory();
        add.Invoke(window, new object[] { wire }); window.SelectMessage(messages.Cast<GotifyMessage>().Single()); window.DismissNotice();
        ThemeManager.Apply("Light");
        window.Hide(); window.Opacity = 1; root.ClearValue(FrameworkElement.WidthProperty); root.ClearValue(FrameworkElement.HeightProperty);
    }
}
