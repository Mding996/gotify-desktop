using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;

namespace GotifyDesktop;

internal static partial class Diagnostics
{
    private sealed class SendingMock : HttpMessageHandler
    {
        internal Func<HttpRequestMessage, Task<HttpResponseMessage>> Handle = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Handle(request);
    }
    private sealed record SentRequest(string Server, string Query, string Token, string Body);
    private static void CheckApplicationSending(List<string> checks, string reportPath, string profile)
    {
        void Check(bool ok, string label) { if (!ok) throw new InvalidOperationException(label); checks.Add(label); }
        void Complete(Task task) { PumpUntil(() => task.IsCompleted, TimeSpan.FromSeconds(10)); if (!task.IsCompleted) throw new TimeoutException("Application sending test timed out."); task.GetAwaiter().GetResult(); }
        var directory = Path.Combine(profile, "app-sending"); Directory.CreateDirectory(directory);
        new AppConfig { ServerUrl = "https://sender.invalid", ClientToken = "client-only-synthetic", NotificationsEnabled = false }.Save(Path.Combine(directory, "config.json"));
        var mock = new SendingMock(); var sent = new List<SentRequest>();
        Func<HttpResponseMessage> response = () => new(HttpStatusCode.OK);
        TaskCompletionSource<HttpResponseMessage>? pending = null;
        mock.Handle = async request =>
        {
            Check(request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/message", "Application sender uses the message POST endpoint");
            string body = await request.Content!.ReadAsStringAsync();
            sent.Add(new(request.RequestUri!.GetLeftPart(UriPartial.Authority), request.RequestUri.Query, request.Headers.GetValues("X-Gotify-Key").Single(), body));
            return pending != null ? await pending.Task : response();
        };
        var window = new MainWindow(directory, false, httpHandler: mock) { ShowInTaskbar = false, ShowActivated = false };
        try
        {
            var box = (ComboBox)window.FindName("SendApplicationBox");
            var title = (TextBox)window.FindName("TitleBox"); var body = (TextBox)window.FindName("MessageContentBox"); var priority = (TextBox)window.FindName("PriorityBox");
            var button = (Button)window.FindName("SendButton"); var refresh = (Button)window.FindName("RefreshSendAppsButton"); var notice = (TextBlock)window.FindName("NoticeLabel");
            var appA = new ManagedApplication { Id = 17, Name = "A 短信转发", DefaultPriority = 9 };
            var appB = new ManagedApplication { Id = 19, Name = "B 工作机消息转发", DefaultPriority = 3 };
            void Choose(int id) => box.SelectedItem = box.Items.OfType<SendApplicationChoice>().Single(x => x.Id == id);
            window.UpdateSendApplications(new[] { appA, appB });
            Check(((SendApplicationChoice)box.SelectedItem).Id == 17 && priority.Text == "9", "An account with no application token can select a catalog app and use its default priority");
            priority.Text = "7"; Choose(19);
            Check(priority.Text == "7", "Switching the send app preserves a manually edited priority");
            Choose(17); window.ToggleComposer(true); title.Text = "App title"; body.Text = "App body";
            pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task sending = window.SubmitMessageAsync();
            PumpUntil(() => sent.Count == 1, TimeSpan.FromSeconds(4));
            Check(!button.IsEnabled && !box.IsEnabled && !refresh.IsEnabled, "Sending locks the target picker, refresh and submit controls until completion");
            Complete(window.SubmitMessageAsync());
            Check(sent.Count == 1, "Repeated submit during an application send creates only one request");
            using (var payload = JsonDocument.Parse(sent[0].Body))
                Check(sent[0].Token == "client-only-synthetic" && sent[0].Query == "" && payload.RootElement.GetProperty("appid").GetInt32() == 17 && payload.RootElement.GetProperty("priority").GetInt32() == 7 && payload.RootElement.GetProperty("title").GetString() == "App title" && payload.RootElement.GetProperty("message").GetString() == "App body", "Client-authenticated sending snapshots the selected app ID, title, body and priority without a token in the URL");
            body.Text = "A new draft while the first request is pending";
            pending.SetResult(new(HttpStatusCode.OK)); Complete(sending); pending = null;
            Check(title.Text == "App title" && body.Text.StartsWith("A new draft") && button.IsEnabled && box.IsEnabled, "A successful send preserves the complete draft when it was edited during the request");
            title.Text = "Completed"; body.Text = "Fresh app message"; Complete(window.SubmitMessageAsync());
            Check(title.Text == "" && body.Text == "" && ((System.Windows.Controls.Border)window.FindName("ComposerPanel")).Visibility == Visibility.Collapsed, "An unchanged successfully submitted application draft clears and collapses normally");
            window.ToggleComposer(true); title.Text = "Keep failed"; body.Text = "Keep failed body";
            response = () => new(HttpStatusCode.Forbidden); int count = sent.Count; Complete(window.SubmitMessageAsync());
            Check(sent.Count == count + 1 && body.Text == "Keep failed body" && notice.Text.Contains("Client Token") && notice.Text.Contains("Gotify 3.x") && button.IsEnabled, "A forbidden application send reports client permissions and server support, retains the draft and does not retry");
            window.UpdateSendApplications(new[] { appB, new ManagedApplication { Id = 17, Name = "A renamed app", DefaultPriority = 2 } });
            Check(((SendApplicationChoice)box.SelectedItem).Id == 17 && priority.Text == "7", "Refreshing or renaming applications preserves the target by ID and does not overwrite draft priority");
            window.UpdateSendApplications(new[] { appB }); count = sent.Count; Complete(window.SubmitMessageAsync());
            Check(((SendApplicationChoice)box.SelectedItem).Key == "none" && sent.Count == count && body.Text == "Keep failed body", "Deleting the selected app requires a fresh selection and never silently sends to another app");
            window.UpdateSendApplications(new[] { appA, appB });
            Check(((SendApplicationChoice)box.SelectedItem).Key == "none", "Later catalog refreshes do not silently replace an invalidated send target");
            Choose(17); window.UseManagedApplication("application-only-synthetic", selectForSending: true);
            Check(((SendApplicationChoice)box.SelectedItem).ManualToken, "Explicitly using a vault application token selects that sending mode instead of leaving an unrelated catalog app active");
            response = () => new(HttpStatusCode.OK); Complete(window.SubmitMessageAsync());
            using (var payload = JsonDocument.Parse(sent[^1].Body)) Check(sent[^1].Token == "application-only-synthetic" && !payload.RootElement.TryGetProperty("appid", out _), "Legacy Application Token sending remains available and omits appid");
            Choose(17); title.Text = "Original account"; body.Text = "Retain on connection change";
            pending = new(TaskCreationOptions.RunContinuationsAsynchronously); sending = window.SubmitMessageAsync();
            PumpUntil(() => sent[^1].Body.Contains("Original account"), TimeSpan.FromSeconds(4));
            window.ManagementConfig.ServerUrl = "https://new-sender.invalid"; window.ManagementConfig.ClientToken = "new-client-synthetic"; window.RefreshSendContext();
            Check(box.Items.OfType<SendApplicationChoice>().All(x => x.Id == null), "A changed connection clears all applications from the old account");
            pending.SetResult(new(HttpStatusCode.OK)); Complete(sending); pending = null;
            Check(body.Text == "Retain on connection change" && notice.Text.Contains("原先选择"), "A response from a previous connection cannot clear the new connection's draft");
            window.ManagementConfig.ApplicationToken = ""; window.ManagementConfig.ClientToken = ""; window.RefreshSendContext();
            window.UpdateSendApplications(new[] { appA }); count = sent.Count; Complete(window.SubmitMessageAsync());
            Check(box.Items.OfType<SendApplicationChoice>().All(x => x.Id == null) && sent.Count == count, "A disconnected client cannot reuse stale catalog applications or submit a client-authenticated message");

            // Real WPF views with synthetic account metadata; no message is sent here.
            window.ManagementConfig.ServerUrl = "https://sender.invalid"; window.ManagementConfig.ClientToken = "client-only-synthetic"; window.RefreshSendContext();
            window.UpdateSendApplications(new[] { appA, appB }); Choose(17);
            window.ToggleComposer(true); title.Text = "按应用发送测试"; body.Text = "选择短信转发应用，即可使用当前账号发送，无需查找应用 Token。"; priority.Text = "9";
            window.DismissNotice();
            window.Show();
            foreach (var theme in new[] { "Light", "Dark" })
            {
                ThemeManager.Apply(theme); Render(window, Path.ChangeExtension(reportPath, $".app-sending-{theme.ToLowerInvariant()}.png"), 1120, 840);
                Check(box.ActualWidth > 180 && box.ActualHeight >= 30 && button.IsVisible, $"Application picker and send controls remain usable in the {theme} theme");
                IEnumerable<TextBlock> Texts(DependencyObject parent)
                {
                    for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
                    {
                        var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                        if (child is TextBlock text) yield return text;
                        foreach (var descendant in Texts(child)) yield return descendant;
                    }
                }
                Check(Texts(box).Any(x => x.Text == "A 短信转发（#17）"), $"The {theme} selected application displays its friendly name and ID rather than internal record data");
                box.IsDropDownOpen = true; window.UpdateLayout();
                Check(box.Items.OfType<SendApplicationChoice>().Count(x => x.Id.HasValue) == 2, $"The {theme} application dropdown contains the authenticated catalog");
                box.IsDropDownOpen = false;
            }
            window.SetNotice("小窗口下的发送区仍可完整操作。", NoticeTone.Warning);
            window.Width = 960; window.Height = 660; window.UpdateLayout();
            var composerScroll = (ScrollViewer)window.FindName("ComposerScroll"); composerScroll.ScrollToEnd(); window.UpdateLayout();
            Render(window, Path.ChangeExtension(reportPath, ".app-sending-compact.png"), 960, 660);
            var point = body.TransformToAncestor(window).Transform(new System.Windows.Point(body.ActualWidth, body.ActualHeight));
            var footer = ((TextBlock)window.FindName("CountLabel")).TransformToAncestor(window).Transform(new System.Windows.Point(0, 0));
            Check(point.X <= ((FrameworkElement)window.Content).ActualWidth && point.Y < footer.Y && box.ActualWidth > 180, "At the minimum window size the message editor remains reachable by scrolling and is not covered by the footer");
        }
        finally { pending?.TrySetCanceled(); window.DisposeResources(); window.Close(); ThemeManager.Apply("Light"); }
    }
}
