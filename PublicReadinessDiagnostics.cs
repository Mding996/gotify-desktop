using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Windows;
using TextBox = System.Windows.Controls.TextBox;

namespace GotifyDesktop;

internal static partial class Diagnostics
{
    private sealed class FirstRunTransport : HttpMessageHandler
    {
        internal readonly List<Uri> Requests = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") });
        }
    }
    private static void CheckPublicReadiness(List<string> checks, string profile)
    {
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); checks.Add(label); }
        var assembly = Assembly.GetExecutingAssembly();
        var resources = assembly.GetManifestResourceNames();
        foreach (string suffix in new[] { ".LICENSE", ".THIRD_PARTY_NOTICES.md", ".licenses.Gotify-logo-CC-BY-4.0.txt", ".licenses.dotnet.runtime-LICENSE.txt", ".licenses.dotnet.runtime-THIRD-PARTY-NOTICES.txt", ".licenses.dotnet.windowsdesktop-LICENSE.txt" })
            Check(resources.Any(name => name.EndsWith(suffix, StringComparison.Ordinal)), "The standalone EXE embeds its project, logo and runtime license notices: " + suffix);
        string directory = Path.Combine(profile, "first-run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "config.json");
        Check(AppConfig.Load(path).ServerUrl.Length == 0, "A fresh installation has no preconfigured personal or public server");
        File.WriteAllText(path, "{\"ServerUrl\":null}");
        Check(AppConfig.Load(path).ServerUrl.Length == 0, "A null saved server is normalized to an unconfigured address");
        File.WriteAllText(path, "invalid-json");
        Check(AppConfig.Load(path).ServerUrl.Length == 0, "Corrupt configuration never falls back to a shared server");
        new AppConfig { ServerUrl = "https://my-server.invalid/gotify", ClientToken = "synthetic-existing-client", NotificationsEnabled = false }.Save(path);
        var upgrade = AppConfig.Load(path);
        Check(upgrade.ServerUrl == "https://my-server.invalid/gotify" && upgrade.ClientToken == "synthetic-existing-client", "Upgrading preserves a user's explicitly configured server and protected token");
        File.Delete(path);
        var handler = new FirstRunTransport();
        var window = new MainWindow(directory, false, httpHandler: handler);
        try
        {
            var config = window.ManagementConfig;
            Check(config.ServerUrl.Length == 0 && window.ServerLabel.Text.Contains("设置"), "The first-run header asks the user to configure their own server");
            foreach (string method in new[] { "Refresh_Click", "Older_Click" })
                typeof(MainWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { window, new RoutedEventArgs() });
            typeof(MainWindow).GetMethod("StartConnection", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
            Check(handler.Requests.Count == 0 && typeof(MainWindow).GetField("_streamCts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window) == null, "First-run refresh, pagination and reconnect are safe and start no network connection");
            config.ApplicationToken = "synthetic-first-run-app"; config.ClientToken = "synthetic-first-run-client";
            window.RefreshSendContext();
            ((TextBox)window.FindName("TitleBox")).Text = "Synthetic draft";
            ((TextBox)window.FindName("MessageContentBox")).Text = "Synthetic first-run content";
            window.SubmitMessageAsync().GetAwaiter().GetResult();
            var refresh = (Task)typeof(MainWindow).GetMethod("RefreshSendApplications", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null)!;
            refresh.GetAwaiter().GetResult();
            Check(handler.Requests.Count == 0 && window.MessageContentBox.Text == "Synthetic first-run content", "Sending and refreshing the app picker without a server preserve the draft and make no request");
            foreach (string invalid in new[] { " ", "not-a-url", "file:///C:/test", "https://user:password@example.invalid", "https://example.invalid?token=synthetic" })
            {
                config.ServerUrl = invalid;
                Check(!window.TryConnectionServer(out _) && handler.Requests.Count == 0, "An invalid or credential-bearing server is rejected before networking");
            }
            config.ServerUrl = "https://custom.invalid/base";
            Check(window.TryConnectionServer(out var target) && target.Host == "custom.invalid", "The first-run guard accepts a user's explicit HTTPS server and base path");
            window.RefreshSendContext();
            window.SubmitMessageAsync().GetAwaiter().GetResult();
            Check(handler.Requests.Count == 1 && handler.Requests[0].AbsoluteUri == "https://custom.invalid/base/message", "After setup, sending targets only the selected custom server rather than any built-in service");
        }
        finally { window.DisposeResources(); window.Close(); }
    }
}
