using System.Collections;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using ListViewItem = System.Windows.Controls.ListViewItem;
using Button = System.Windows.Controls.Button;
using ListView = System.Windows.Controls.ListView;
using TextBox = System.Windows.Controls.TextBox;
using TabControl = System.Windows.Controls.TabControl;

namespace GotifyDesktop;

internal static partial class Diagnostics
{
    private sealed class ManagementMock : HttpMessageHandler
    {
        internal Func<HttpRequestMessage, Task<HttpResponseMessage>> Handler { get; set; } = _ => Task.FromResult(JsonResponse("{}"));
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Handler(request);
    }
    private static HttpResponseMessage JsonResponse(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static void CheckManagement(MainWindow main, List<string> checks, string reportPath, string profile)
    {
        void Check(bool ok, string label) { if (!ok) throw new InvalidOperationException(label); checks.Add(label); }
        T Wait<T>(Task<T> task) { PumpUntil(() => task.IsCompleted, TimeSpan.FromSeconds(8)); return task.GetAwaiter().GetResult(); }
        void Complete(Task task) { PumpUntil(() => task.IsCompleted, TimeSpan.FromSeconds(8)); task.GetAwaiter().GetResult(); }
        var vaultPath = Path.Combine(profile, "management-vault.json");
        if (File.Exists(vaultPath)) File.Delete(vaultPath);
        var vault = new TokenVault(vaultPath);
        var first = vault.Store("https://example.invalid/", null, "Client", null, "Current", "gtfyc.synthetic-only-secret");
        Check(!File.ReadAllText(vaultPath).Contains("gtfyc.synthetic-only-secret"), "Vault stores DPAPI ciphertext and never plaintext tokens");
        var enriched = vault.Store("https://example.invalid", 1, "Client", 17, "Desktop", "gtfyc.synthetic-only-secret");
        Check(first.Key == enriched.Key && vault.Tokens.Count == 1 && enriched.AccountId == 1 && enriched.ObjectId == 17, "Identifying a configured client enriches its vault entry without duplicating it");
        Check(new TokenVault(vaultPath).Tokens.Single().Reveal() == "gtfyc.synthetic-only-secret", "Vault survives reload and decrypts under the current Windows account");
        var old = vault.Store("https://example.invalid", 1, "Application", 3, "SMS", "gtfya.old-synthetic");
        var fresh = vault.Store("https://example.invalid", 1, "Application", 3, "SMS", "gtfya.new-synthetic");
        vault.Store("https://other.invalid", 1, "Application", 3, "Other", "gtfya.other-synthetic");
        vault.Revoke("https://example.invalid", 1, "Application", 3, fresh.Reveal());
        Check(vault.Tokens.Single(x => x.Key == old.Key).Revoked && !vault.Tokens.Single(x => x.Key == fresh.Key).Revoked && !vault.Tokens.Single(x => x.Server == "https://other.invalid").Revoked, "Rotation revokes only matching older tokens and isolates servers");
        vault.Remove(old.Key);
        Check(new TokenVault(vaultPath).Tokens.All(x => x.Key != old.Key), "Forgetting a vault entry persists without changing other tokens");
        File.WriteAllText(vaultPath + ".broken", "invalid json");
        try { _ = new TokenVault(vaultPath + ".broken"); Check(false, "Corrupt vault is rejected"); } catch (JsonException) { Check(File.ReadAllText(vaultPath + ".broken") == "invalid json", "Unreadable vault is not silently overwritten"); }
        foreach (var invalid in new[] { "https://user:pass@example.invalid", "https://example.invalid?token=secret", "file:///tmp", "https://example.invalid/#token" })
        { try { ManagementApi.NormalizeServer(invalid); Check(false, "Reject unsafe management URL"); } catch (ArgumentException) { Check(true, "Management rejects credential-bearing or non-HTTP server URL"); } }

        var requests = new List<(string Method, string Path, string Body, bool Basic, bool Client)>();
        var mock = new ManagementMock();
        mock.Handler = async request =>
        {
            string body = request.Content == null ? "" : await request.Content.ReadAsStringAsync();
            bool basic = request.Headers.Authorization?.Scheme == "Basic";
            bool client = request.Headers.TryGetValues("X-Gotify-Key", out var values) && values.Single() == "client-test";
            Check(request.RequestUri!.Host == "example.invalid" && request.RequestUri.Query.Length == 0, "Management secrets use headers and requests stay on the configured server");
            Check(!(basic && client), "Basic reauthentication and client-token headers are never combined");
            var path = request.RequestUri.AbsolutePath; requests.Add((request.Method.Method, path, body, basic, client));
            string result = path switch
            {
                "/current/user" => "{\"id\":1,\"name\":\"mock-user\",\"clientId\":17}",
                "/application" when request.Method == HttpMethod.Get => "[{\"id\":3,\"name\":\"SMS\",\"description\":\"Forwarder\",\"defaultPriority\":9}]",
                "/client" when request.Method == HttpMethod.Get => "[{\"id\":17,\"name\":\"Desktop\",\"createdAt\":\"2026-11-08T00:00:00Z\",\"expiresAfterInactivitySeconds\":0}]",
                "/application/3/security" => "{\"regenerateToken\":{\"token\":\"gtfya.rotated-test\"}}",
                "/application" => "{\"id\":3,\"name\":\"SMS\",\"token\":\"gtfya.created-test\"}",
                "/client" => "{\"id\":17,\"name\":\"Desktop\",\"token\":\"gtfyc.created-test\"}",
                _ => "{}"
            };
            return JsonResponse(result);
        };
        using var api = new ManagementApi("https://example.invalid", "client-test", mock);
        var credentials = new AccountCredentials("mock-user", "never-persist-this-password");
        Check(Wait(api.User()).ClientId == 17 && Wait(api.User(credentials)).Id == 1, "Client and password authentication read the current account and client ID");
        var login = Wait(api.SaveClient(null, "Desktop", 0, credentials));
        Check(login.Token == "gtfyc.created-test" && requests.Last().Basic && !requests.Last().Client, "Password login creates a client and captures its once-only token");
        Check(Wait(api.Applications()).Single().DefaultPriority == 9 && Wait(api.Clients()).Single().ExpiryDisplay == "永不过期", "Management loads app priorities and client expiry metadata");
        Check(Wait(api.SaveApplication(null, "SMS", "Forwarder", 9)).Token == "gtfya.created-test", "Creating an application receives its once-only token");
        Wait(api.SaveApplication(3, "Renamed", "Updated", 8));
        var update = requests.Last(); using (var body = JsonDocument.Parse(update.Body)) Check(update.Method == "PUT" && update.Path == "/application/3" && body.RootElement.GetProperty("defaultPriority").GetInt32() == 8 && body.RootElement.GetProperty("description").GetString() == "Updated", "App edits send the official name, description and priority fields");
        Wait(api.SaveClient(17, "Renamed desktop", 86400));
        Check(requests.Last().Method == "PUT" && requests.Last().Body.Contains("86400"), "Client rename and inactivity expiry use the official update API");
        Check(Wait(api.RotateApplication(3, credentials)) == "gtfya.rotated-test" && requests.Last().Basic && requests.Last().Body.Contains("regenerateToken"), "Token rotation reauthenticates and reads the nested v3 response");
        Wait(api.ElevateClient(17, credentials));
        Check(requests.Last().Path == "/client/17/elevate" && requests.Last().Body.Contains("900") && requests.Last().Basic, "Client elevation requests a 15-minute window with password authentication");
        Wait(api.DeleteApplication(3, credentials)); Wait(api.DeleteClient(17, credentials));
        Check(requests.TakeLast(2).All(x => x.Method == "DELETE" && x.Basic), "App deletion and client revocation use reauthenticated requests");
        var iconPath = Path.Combine(profile, "management-icon.png");
        using (var input = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/logo.png"))!.Stream)
        using (var output = File.Create(iconPath)) input.CopyTo(output);
        Wait(api.UploadIcon(3, iconPath));
        Check(requests.Last().Method == "POST" && requests.Last().Path == "/application/3/image" && requests.Last().Body.Contains("name=file"), "Icon upload uses multipart field file on the app image endpoint");
        Wait(api.RemoveIcon(3)); Check(requests.Last().Method == "DELETE" && requests.Last().Path.EndsWith("/image"), "Restoring the default icon uses the image deletion endpoint");

        var messages = (IList)typeof(MainWindow).GetField("_messages", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(main)!;
        main.ResetFilters(); main.ClearLocalHistory();
        messages.Add(new GotifyMessage { Id = 101, AppId = 3, Title = "Remove one" });
        messages.Add(new GotifyMessage { Id = 102, AppId = 3, Title = "Remove app" });
        messages.Add(new GotifyMessage { Id = 103, AppId = 4, Title = "Keep other app" });
        var list = (ListView)main.FindName("MessagesList"); list.SelectedItem = messages[0];
        list.ScrollIntoView(messages[1]); main.UpdateLayout();
        var secondRow = (ListViewItem)list.ItemContainerGenerator.ContainerFromItem(messages[1]);
        Check(main.ResolveServerContextTarget(secondRow, false) == 102, "Right-click deletion targets the clicked message rather than an older selection");
        Check(main.ResolveServerContextTarget(list, false) == null && !((MenuItem)main.FindName("DeleteServerMenu")).IsEnabled, "Opening the menu on empty space cannot delete a previously selected message");
        Check(main.ResolveServerContextTarget(list, true) == 101, "Keyboard context actions use the explicitly selected message");
        Complete(main.DeleteManagedMessages(api, messageId: 101));
        Check(messages.Count == 2 && messages.Cast<GotifyMessage>().All(x => x.Id != 101) && ((Grid)main.FindName("DetailPanel")).Visibility == System.Windows.Visibility.Collapsed, "Single-message server deletion removes its local record and clears stale details");
        Complete(main.DeleteManagedMessages(api, applicationId: 3));
        Check(messages.Count == 1 && ((GotifyMessage)messages[0]!).AppId == 4 && requests.Last().Path == "/application/3/message", "Per-app server deletion preserves all other applications");
        mock.Handler = _ => Task.FromResult(JsonResponse("{}", HttpStatusCode.Forbidden));
        try { Complete(main.DeleteManagedMessages(api)); Check(false, "Failed server deletion must throw"); }
        catch (ManagementApiException ex) { Check(ex.Status == HttpStatusCode.Forbidden && messages.Count == 1 && File.ReadAllText(Path.Combine(profile, "messages.json")).Contains("Keep other app"), "Failed server deletion preserves the list and persisted local history"); }
        var pending = new TaskCompletionSource<HttpResponseMessage>();
        mock.Handler = _ => pending.Task;
        var deletion = main.DeleteManagedMessages(api);
        messages.Add(new GotifyMessage { Id = 104, AppId = 4, Title = "Arrived during deletion" });
        pending.SetResult(JsonResponse("{}")); Complete(deletion);
        Check(messages.Count == 1 && ((GotifyMessage)messages[0]!).Id == 104, "Server clearing preserves messages arriving after the deletion request began");
        mock.Handler = _ => Task.FromResult(JsonResponse("{}")); Complete(main.DeleteManagedMessages(api));
        Check(messages.Count == 0 && File.ReadAllText(Path.Combine(profile, "messages.json")) == "[]", "Successful clear-all updates local history after the server response");
        var burst = new NotificationBurstBuffer(); var now = DateTimeOffset.UtcNow;
        foreach (int appId in new[] { 3, 4 }) { burst.Accept(new GotifyMessage { Id = appId * 10, AppId = appId }, now); burst.Accept(new GotifyMessage { Id = appId * 10 + 1, AppId = appId }, now); }
        burst.DiscardMessages(new() { 31 });
        Check(burst.Flush(now.AddSeconds(11)).Single().AppId == 4, "Deleting a message does not discard another application's pending notification summary");

        // Validate the real management surface with a synthetic API, never a live account.
        var config = main.ManagementConfig; config.ServerUrl = "https://example.invalid"; config.ClientToken = "client-test"; config.ApplicationToken = "app-test";
        var uiMock = new ManagementMock { Handler = request => Task.FromResult(JsonResponse(request.RequestUri!.AbsolutePath switch
        {
            "/current/user" => "{\"id\":1,\"name\":\"mock-user\",\"clientId\":17}",
            "/application" => "[{\"id\":3,\"name\":\"短信转发\",\"description\":\"测试应用\",\"defaultPriority\":9}]",
            "/client" => "[{\"id\":17,\"name\":\"桌面接收端\",\"createdAt\":\"2026-11-08T00:00:00Z\"}]",
            _ => "{}"
        })) };
        using var uiApi = new ManagementApi(config.ServerUrl, config.ClientToken, uiMock);
        var manager = new ManagementWindow(main, vaultPath, false, uiApi) { Owner = main, ShowInTaskbar = false };
        try
        {
            Complete((Task)typeof(ManagementWindow).GetMethod("Reload", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(manager, null)!);
            Check(((ListView)manager.FindName("ApplicationsList")).Items.Count == 1 && ((ListView)manager.FindName("ClientsList")).Items.Count == 1 && ((TextBlock)manager.FindName("AccountLabel")).Text.Contains("mock-user"), "Management UI loads the authenticated account, app and client lists");
            ((ListView)manager.FindName("ApplicationsList")).SelectedIndex = 0;
            Check(((TextBox)manager.FindName("ApplicationName")).Text == "短信转发" && ((TextBox)manager.FindName("ApplicationPriority")).Text == "9", "Selecting an application fills its editor with server data");
            ((ListView)manager.FindName("ClientsList")).SelectedIndex = 0;
            Check(((TextBox)manager.FindName("ClientName")).Text == "桌面接收端" && ((TextBox)manager.FindName("ClientDays")).Text == "0", "Selecting a client fills its name and expiry editor");
            var uiVault = (ListView)manager.FindName("VaultList"); uiVault.SelectedItem = uiVault.Items.OfType<VaultToken>().First(x => !x.Revoked);
            Check(((TextBox)manager.FindName("TokenValue")).Text == "••••••••••••••••", "Vault masks tokens when a record is selected");
            ((Button)manager.FindName("RevealButton")).RaiseEvent(new System.Windows.RoutedEventArgs(Button.ClickEvent));
            Check(((TextBox)manager.FindName("TokenValue")).Text == ((VaultToken)uiVault.SelectedItem).Reveal(), "Explicit reveal displays the decrypted selected token");
            ((Button)manager.FindName("RevealButton")).RaiseEvent(new System.Windows.RoutedEventArgs(Button.ClickEvent));
            Check(((TextBox)manager.FindName("TokenValue")).Text == "••••••••••••••••", "Hiding a revealed token masks it again");
            var tabs = (TabControl)manager.FindName("ManagementTabs"); manager.Show();
            foreach (var theme in new[] { "Light", "Dark" })
            {
                ThemeManager.Apply(theme);
                for (int tab = 0; tab < 3; tab++)
                {
                    tabs.SelectedIndex = tab; manager.UpdateLayout();
                    Render(manager, Path.ChangeExtension(reportPath, $".management-{theme.ToLowerInvariant()}-{tab}.png"), 1120, 760);
                    Check(((System.Windows.Media.SolidColorBrush)tabs.Background).Color == ((System.Windows.Media.SolidColorBrush)System.Windows.Application.Current.Resources["SurfaceBrush"]).Color, $"Management tab {tab + 1} follows the {theme} theme");
                    var activeList = (ListView)manager.FindName(tab == 0 ? "ApplicationsList" : tab == 1 ? "ClientsList" : "VaultList");
                    Check(activeList.ItemContainerGenerator.ContainerFromIndex(0) is ListViewItem row && row.ActualHeight >= 44, $"Management tab {tab + 1} keeps readable list row heights in {theme}");
                }
            }
        }
        finally { manager.Close(); ThemeManager.Apply("Light"); }
        var loginDialog = new AccountWindow("https://example.invalid", login: true);
        Check(((PasswordBox)loginDialog.FindName("PasswordBox")).Password.Length == 0 && !((TextBox)loginDialog.FindName("ServerBox")).IsReadOnly, "Account login starts with an empty password and an editable server address"); loginDialog.Close();
        Check(((Button)main.FindName("ManagementButton")).Content.ToString() == "管理", "The main header exposes the management entry point");
        Check(((Button)main.FindName("MoreButton")).ContextMenu.Items.OfType<MenuItem>().Any(x => x.Header.ToString()!.Contains("服务器")) && list.ContextMenu.Items.OfType<MenuItem>().Any(x => x.Header.ToString()!.Contains("服务器")), "Server-wide clearing and single-message deletion are explicitly labelled separately from local clearing");
    }
}

