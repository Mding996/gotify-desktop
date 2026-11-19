using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using MessageBox = System.Windows.MessageBox;
using TextBox = System.Windows.Controls.TextBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace GotifyDesktop;

public partial class ManagementWindow : Window
{
    private readonly MainWindow _main;
    private readonly TokenVault _vault;
    private ManagementApi _api;
    private ManagedUser? _user;
    private bool _busy, _revealed;
    private string Server => _main.ManagementConfig.ServerUrl.TrimEnd('/');
    private ManagedApplication? SelectedApplication => ApplicationsList.SelectedItem as ManagedApplication;
    private ManagedClient? SelectedClient => ClientsList.SelectedItem as ManagedClient;
    private VaultToken? SelectedToken => VaultList.SelectedItem as VaultToken;
    internal ManagementWindow(MainWindow main, string vaultPath, bool load = true, ManagementApi? api = null)
    {
        InitializeComponent(); _main = main; _vault = new(vaultPath); _api = api ?? new(Server, main.ManagementConfig.ClientToken);
        SourceInitialized += (_, _) => ThemeManager.TitleBar(this);
        Closing += (_, e) => { if (_busy) { e.Cancel = true; StatusText.Text = "正在完成操作，请稍候再关闭。"; } };
        Closed += (_, _) => { TokenValue.Clear(); _api.Dispose(); };
        CaptureConfiguredTokens(); RefreshVault();
        if (load) Loaded += async (_, _) => await Run(Reload);
    }
    private async Task Run(Func<Task> action)
    {
        if (_busy) return;
        _busy = true; ManagementTabs.IsEnabled = LoginButton.IsEnabled = ReloadButton.IsEnabled = false;
        StatusText.Text = "正在处理…";
        try { await action(); }
        catch (Exception ex) { StatusText.Text = Describe(ex); }
        finally { _busy = false; ManagementTabs.IsEnabled = LoginButton.IsEnabled = ReloadButton.IsEnabled = true; }
    }
    internal static string Describe(Exception ex) => ex switch
    {
        ManagementApiException or ArgumentException or InvalidDataException => ex.Message,
        TaskCanceledException => "连接超时。若刚才执行了新建或轮换，请先刷新确认结果，避免重复操作。",
        HttpRequestException => "无法连接服务器，请检查网络和服务器地址。",
        IOException or System.Security.Cryptography.CryptographicException or UnauthorizedAccessException => "本机数据读取或保存失败，请检查目录权限；保险箱只能由原 Windows 账号解密。",
        _ => "操作未完成，请刷新确认当前状态后重试。"
    };
    private void CaptureConfiguredTokens()
    {
        var config = _main.ManagementConfig;
        if (config.ClientToken.Length > 0)
            _vault.Store(Server, _user?.Id, "Client", _user?.ClientId, "当前接收客户端", config.ClientToken);
        if (config.ApplicationToken.Length > 0 && !_vault.Tokens.Any(x => x.Server == Server && x.Kind == "Application" && x.Reveal() == config.ApplicationToken))
            _vault.Store(Server, null, "Application", null, "当前发送 Token（应用尚未关联）", config.ApplicationToken);
    }
    private async Task Reload()
    {
        if (_main.ManagementConfig.ClientToken.Length == 0) { AccountLabel.Text = Server + " · 尚未登录"; StatusText.Text = "请先账号登录，或在设置中填写 Client Token。"; return; }
        _user = await _api.User();
        AccountLabel.Text = $"{_user.Name} · {Server} · 当前客户端 #{_user.ClientId}";
        var apps = await _api.Applications(); var clients = await _api.Clients();
        int? appId = SelectedApplication?.Id, clientId = SelectedClient?.Id;
        ApplicationsList.ItemsSource = apps; ClientsList.ItemsSource = clients;
        ApplicationsList.SelectedItem = apps.FirstOrDefault(x => x.Id == appId);
        ClientsList.SelectedItem = clients.FirstOrDefault(x => x.Id == clientId);
        CaptureConfiguredTokens(); _main.UpdateManagedApplications(apps); RefreshVault();
        StatusText.Text = $"{apps.Count} 个应用，{clients.Count} 个客户端。";
    }
    private async void Reload_Click(object sender, RoutedEventArgs e) => await Run(Reload);
    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AccountWindow(Server, _user?.Name ?? "", true) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var credentials = dialog.Credentials!;
        await Run(async () =>
        {
            using var loginApi = new ManagementApi(dialog.Server, "");
            var user = await loginApi.User(credentials);
            // Logging in is explicit: only one client is created, with no retry on ambiguous failure.
            var client = await loginApi.SaveClient(null, $"Gotify Desktop · {Environment.MachineName}", 0, credentials);
            if (string.IsNullOrEmpty(client.Token)) throw new InvalidDataException("服务器未返回 Client Token。");
            StoreToken(dialog.Server, user.Id, "Client", client.Id, client.Name, client.Token);
            bool identityChanged = Server != dialog.Server || _user?.Id != user.Id;
            _main.UseManagedClient(dialog.Server, client.Token, identityChanged);
            _api.Dispose(); _api = new(Server, client.Token); _user = user;
            await Reload(); StatusText.Text = "登录成功，接收客户端已创建，Token 已加密保存。";
        });
        dialog.Credentials = null;
    }
    private async Task<AccountCredentials?> VerifyAccount()
    {
        _user ??= await _api.User();
        var dialog = new AccountWindow(Server, _user.Name) { Owner = this };
        if (dialog.ShowDialog() != true) { StatusText.Text = "操作已取消。"; return null; }
        var credentials = dialog.Credentials!; dialog.Credentials = null;
        var verified = await _api.User(credentials);
        if (verified.Id != _user.Id) throw new ArgumentException("请使用当前已连接账号的密码，不能用其他账号执行此操作。");
        return credentials;
    }
    private bool Confirm(string message) => MessageBox.Show(this, message, "确认操作", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
    private ManagedApplication RequireApplication() => SelectedApplication ?? throw new ArgumentException("请先选择应用。");
    private ManagedClient RequireClient() => SelectedClient ?? throw new ArgumentException("请先选择客户端。");
    private VaultToken RequireToken() => SelectedToken ?? throw new ArgumentException("请先选择一条 Token 记录。");
    private void Application_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (ApplicationName == null) return;
        var app = SelectedApplication; ApplicationName.Text = app?.Name ?? ""; ApplicationDescription.Text = app?.Description ?? "";
        ApplicationPriority.Text = (app?.DefaultPriority ?? 5).ToString(); ApplicationEditorLabel.Text = app == null ? "新建应用" : $"编辑应用 #{app.Id}";
    }
    private void NewApplication_Click(object sender, RoutedEventArgs e) { ApplicationsList.SelectedItem = null; Application_Selected(sender, null!); ApplicationName.Focus(); }
    private async void SaveApplication_Click(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        _user ??= await _api.User();
        string name = ApplicationName.Text.Trim();
        if (name.Length == 0 || !int.TryParse(ApplicationPriority.Text, out int priority) || priority < 0 || priority > 10) throw new ArgumentException("请填写应用名称，优先级应为 0～10。");
        var app = await _api.SaveApplication(SelectedApplication?.Id, name, ApplicationDescription.Text, priority);
        if (!string.IsNullOrEmpty(app.Token)) StoreToken(Server, _user?.Id, "Application", app.Id, app.Name, app.Token);
        await Reload(); ApplicationsList.SelectedItem = ((IEnumerable<ManagedApplication>)ApplicationsList.ItemsSource).FirstOrDefault(x => x.Id == app.Id);
        StatusText.Text = "应用已保存。新建应用的 Token 已加密存入保险箱。";
    });
    private async void UploadIcon_Click(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        var app = RequireApplication(); var dialog = new OpenFileDialog { Filter = "图标图片|*.png;*.jpg;*.jpeg;*.gif", Title = "选择应用图标" };
        if (dialog.ShowDialog(this) != true) { StatusText.Text = "已取消。"; return; }
        await _api.UploadIcon(app.Id, dialog.FileName); await Reload(); _main.RefreshManagedIcons(); StatusText.Text = "应用图标已上传。";
    });
    private async void RemoveIcon_Click(object sender, RoutedEventArgs e) => await Run(async () => { var app = RequireApplication(); await _api.RemoveIcon(app.Id); await Reload(); _main.RefreshManagedIcons(); StatusText.Text = "已恢复默认图标。"; });
    private async void RotateToken_Click(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        var app = RequireApplication();
        if (!Confirm($"轮换“{app.Name}”的 Application Token？\n\n旧 Token 会立即失效，使用它的短信转发和其他发送设备需要更新。")) { StatusText.Text = "已取消。"; return; }
        var credentials = await VerifyAccount(); if (credentials == null) return;
        var previous = _vault.Tokens.Where(x => x.Server == Server && x.Kind == "Application" && x.ObjectId == app.Id && x.AccountId == _user!.Id).ToList();
        string token = await _api.RotateApplication(app.Id, credentials);
        StoreToken(Server, _user!.Id, "Application", app.Id, app.Name, token);
        if (previous.Any(x => x.Reveal() == _main.ManagementConfig.ApplicationToken)) _main.UseManagedApplication(token);
        _vault.Revoke(Server, _user.Id, "Application", app.Id, token); RefreshVault(); ShowSavedToken("Application", app.Id);
        StatusText.Text = "Token 已轮换并加密保存，请更新其他发送设备。";
    });
    private async void DeleteApplication_Click(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        var app = RequireApplication(); if (app.Internal) throw new ArgumentException("内部应用不能删除。");
        if (!Confirm($"永久删除应用“{app.Name}”及其全部服务器消息？\n其 Token 会立即失效，此操作无法撤销。")) { StatusText.Text = "已取消。"; return; }
        var credentials = await VerifyAccount(); if (credentials == null) return;
        await _main.DeleteManagedApplication(_api, app.Id, credentials);
        _main.ForgetManagedApplication(app.Id);
        bool active = _vault.Tokens.Any(x => x.Server == Server && x.AccountId == _user!.Id && x.Kind == "Application" && x.ObjectId == app.Id && x.Reveal() == _main.ManagementConfig.ApplicationToken);
        if (active) _main.UseManagedApplication("");
        _vault.Revoke(Server, _user!.Id, "Application", app.Id); await Reload(); StatusText.Text = "应用及其服务器消息已删除。";
    });
    private async void ClearApplication_Click(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        var app = RequireApplication();
        if (!Confirm($"永久清空“{app.Name}”的全部服务器消息？\n消息会从服务器永久移除；其他设备的本地缓存可能保留。无法撤销。")) { StatusText.Text = "已取消。"; return; }
        await _main.DeleteManagedMessages(_api, applicationId: app.Id); StatusText.Text = "此应用的服务器消息已清空。";
    });
    private void Client_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (ClientName == null) return;
        var client = SelectedClient; ClientName.Text = client?.Name ?? "";
        ClientDays.Text = ((client?.ExpiresAfterInactivitySeconds ?? 0) / 86400m).ToString(CultureInfo.InvariantCulture);
    }
    private void NewClient_Click(object sender, RoutedEventArgs e) { ClientsList.SelectedItem = null; Client_Selected(sender, null!); ClientName.Focus(); }
    private async void SaveClient_Click(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        _user ??= await _api.User();
        string name = ClientName.Text.Trim();
        if (name.Length == 0 || !decimal.TryParse(ClientDays.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal days) || days < 0 || days > 36500) throw new ArgumentException("请填写客户端名称，有效期应为 0～36500 天。0 表示永不过期。");
        long seconds = checked((long)(days * 86400)); if (days > 0 && seconds == 0) throw new ArgumentException("有效期至少为 1 秒。");
        var client = await _api.SaveClient(SelectedClient?.Id, name, seconds);
        if (!string.IsNullOrEmpty(client.Token)) StoreToken(Server, _user?.Id, "Client", client.Id, client.Name, client.Token);
        await Reload(); ClientsList.SelectedItem = ((IEnumerable<ManagedClient>)ClientsList.ItemsSource).FirstOrDefault(x => x.Id == client.Id); StatusText.Text = "客户端已保存，新 Token 已加密存入保险箱。";
    });
    private async void Elevate_Click(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        var client = RequireClient();
        if (!Confirm($"为“{client.Name}”临时提权 15 分钟？\n该客户端在此期间可以执行删除应用、撤销客户端等敏感操作。")) { StatusText.Text = "已取消。"; return; }
        var credentials = await VerifyAccount(); if (credentials == null) return;
        await _api.ElevateClient(client.Id, credentials); await Reload(); StatusText.Text = "客户端已临时提权 15 分钟。";
    });
    private async void DeleteClient_Click(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        var client = RequireClient(); bool current = client.Id == _user?.ClientId;
        if (!Confirm($"撤销客户端“{client.Name}”？\n其 Token 会立即失效，对应设备需要重新配置。{(current ? "\n这是当前客户端，本机将断开接收连接。" : "")}")) { StatusText.Text = "已取消。"; return; }
        var credentials = await VerifyAccount(); if (credentials == null) return;
        await _api.DeleteClient(client.Id, credentials);
        _vault.Revoke(Server, _user!.Id, "Client", client.Id);
        if (current) { _main.DisconnectManagedClient(); _user = null; ClientsList.ItemsSource = null; ApplicationsList.ItemsSource = null; RefreshVault(); AccountLabel.Text = Server + " · 已断开"; }
        else await Reload();
        StatusText.Text = "客户端已撤销。";
    });
    private void RefreshVault(Guid? selected = null)
    {
        VaultList.ItemsSource = _vault.Tokens.Where(x => x.Server == Server).OrderByDescending(x => x.SavedAt).ToList();
        VaultList.SelectedItem = _vault.Tokens.FirstOrDefault(x => x.Key == selected); TokenValue.Text = "选择一条记录后点击显示或复制"; _revealed = false; RevealButton.Content = "显示 Token";
    }
    private void StoreToken(string server, int? account, string kind, int id, string name, string value)
    {
        try { var token = _vault.Store(server, account, kind, id, name, value); RefreshVault(token.Key); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            // A successful server mutation must never lose its once-only secret on a disk failure.
            var text = new TextBox { Text = value, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16), VerticalAlignment = VerticalAlignment.Center };
            var dialog = new Window { Owner = this, Title = "Token 未能写入保险箱，请立即选中复制并妥善保存", Width = 720, Height = 180, Content = text, Background = (System.Windows.Media.Brush)FindResource("SurfaceBrush"), Foreground = (System.Windows.Media.Brush)FindResource("TextBrush"), WindowStartupLocation = WindowStartupLocation.CenterOwner };
            dialog.SourceInitialized += (_, _) => ThemeManager.TitleBar(dialog); dialog.ShowDialog(); text.Clear();
            throw new IOException("Token 保险箱保存失败。", ex);
        }
    }
    private void Vault_Selected(object sender, SelectionChangedEventArgs e) { if (TokenValue == null) return; TokenValue.Text = "••••••••••••••••"; _revealed = false; RevealButton.Content = "显示 Token"; }
    private async void Reveal_Click(object sender, RoutedEventArgs e) => await Run(() => { var token = RequireToken(); _revealed = !_revealed; TokenValue.Text = _revealed ? token.Reveal() : "••••••••••••••••"; RevealButton.Content = _revealed ? "隐藏 Token" : "显示 Token"; StatusText.Text = token.Revoked ? "此 Token 已撤销或轮换，仅保留本机记录。" : "仅在可信环境查看 Token。"; return Task.CompletedTask; });
    private async void CopyToken_Click(object sender, RoutedEventArgs e) => await Run(() => { System.Windows.Clipboard.SetText(RequireToken().Reveal()); StatusText.Text = "Token 已复制。"; return Task.CompletedTask; });
    private async void UseToken_Click(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        var token = RequireToken(); if (token.Revoked) throw new ArgumentException("此 Token 已撤销或轮换，不能用于连接。");
        if (token.Kind == "Client")
        {
            using var verify = new ManagementApi(Server, token.Reveal()); var user = await verify.User();
            if (!Confirm($"将接收连接切换到“{token.Name}”（账号 {user.Name}）？\n切换后重新同步该账号的消息。")) { StatusText.Text = "已取消。"; return; }
            _main.UseManagedClient(Server, token.Reveal(), _user?.Id != user.Id); _api.Dispose(); _api = new(Server, token.Reveal()); await Reload();
        }
        else { _main.UseManagedApplication(token.Reveal(), selectForSending: true); StatusText.Text = "已设为当前发送用的 Application Token。"; }
    });
    private async void ForgetToken_Click(object sender, RoutedEventArgs e) => await Run(() =>
    {
        var token = RequireToken(); if (!Confirm("删除这条 Token 的本机保险箱副本？\n不会撤销服务器上的 Token，也不会清除设置中正在使用的 Token。")) { StatusText.Text = "已取消。"; return Task.CompletedTask; }
        _vault.Remove(token.Key); RefreshVault(); StatusText.Text = "本机副本已删除。"; return Task.CompletedTask;
    });
    private void ShowSavedToken(string kind, int id)
    {
        var token = _vault.Tokens.Where(x => x.Server == Server && x.AccountId == _user?.Id && x.Kind == kind && x.ObjectId == id && !x.Revoked).OrderByDescending(x => x.SavedAt).FirstOrDefault();
        if (token == null) { StatusText.Text = "本机没有保存此 Token。新版 Gotify 仅在新建或轮换时返回 Token。"; return; }
        RefreshVault(token.Key); ManagementTabs.SelectedIndex = 2; StatusText.Text = "已定位本机保存的 Token，可显示或复制。";
    }
    private async void ApplicationToken_Click(object sender, RoutedEventArgs e) => await Run(() => { ShowSavedToken("Application", RequireApplication().Id); return Task.CompletedTask; });
    private async void ClientToken_Click(object sender, RoutedEventArgs e) => await Run(() => { ShowSavedToken("Client", RequireClient().Id); return Task.CompletedTask; });
}
