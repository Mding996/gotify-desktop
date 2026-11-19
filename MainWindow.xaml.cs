using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

using MessageBox = System.Windows.MessageBox;

namespace GotifyDesktop;

public partial class MainWindow : Window
{
    internal const int HistoryLimit = 5000;
    private readonly string _dataDir;
    private readonly bool _automaticServices;
    private string ConfigPath => Path.Combine(_dataDir, "config.json");
    private string HistoryPath => Path.Combine(_dataDir, "messages.json");
    private readonly ObservableCollection<GotifyMessage> _messages = new();
    private readonly HttpClient _http;
    private readonly NativeTray _tray = new();

    private AppConfig _config;
    private ICollectionView _view = null!;
    private CancellationTokenSource? _streamCts;
    private readonly SemaphoreSlim _historyGate = new(1, 1);
    private bool _exiting, _notificationsPaused, _sending, _appsBusy, _hasMore = true;
    private GotifyMessage? _lastNotifiedMessage;
    private readonly Dictionary<int, (string Name, string Icon)> _applications = new();
    private NativeToast? _toast;

    public MainWindow() : this(false) { }
    public MainWindow(bool background) : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GotifyDesktop"), true, background) { }
    internal MainWindow(string dataDirectory, bool startServices, bool background = false, HttpMessageHandler? httpHandler = null)
    {
        _dataDir = dataDirectory;
        _automaticServices = startServices;
        _http = new HttpClient(httpHandler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) };
        InitializeComponent();
        Directory.CreateDirectory(_dataDir);
        _config = AppConfig.Load(ConfigPath);
        ThemeManager.Apply(_config.Theme);
        _view = CollectionViewSource.GetDefaultView(_messages);
        _view.Filter = item => MatchesActiveFilters((GotifyMessage)item);
        MessagesList.ItemsSource = _view;
        InitializeExperience();
        InitializeMemoryLifecycle();
        InitializeSendApplicationPicker();
        LoadHistory(); SetupTray(); ApplyConfig(startServices);
        if (!startServices) return;
        try
        {
            _toast = new NativeToast(new NotificationCopyActions(Path.Combine(_dataDir, "notification-actions")))
            { CopyActionUnavailable = () => SetNotice("验证码通知的复制记录保存失败，可从消息列表复制。", NoticeTone.Warning) };
            _toast.Register();
        }
        catch (Exception) { SetNotice("通知中心初始化失败，将使用托盘弹窗。", NoticeTone.Warning, NoticeAction.Settings); _toast = null; }
        if (string.IsNullOrWhiteSpace(_config.ClientToken))
        {
            if (!background) Dispatcher.BeginInvoke(() => OpenSettings());
        }
        else StartConnection();
    }
    internal static bool MatchesSearch(GotifyMessage msg, string term) => string.IsNullOrWhiteSpace(term) || new[] { msg.Title, msg.Message, msg.AppName }.Any(x => x.Contains(term.Trim(), StringComparison.OrdinalIgnoreCase));
    internal static string CopyText(GotifyMessage msg) => $"{msg.Title}\n{msg.AppName} · {msg.Date:yyyy-MM-dd HH:mm:ss} · 优先级 {msg.Priority}\n\n{msg.Message}";
    internal static string StartupCommand(string path) => $"\"{path}\" --background";

    private void SetupTray()
    {
        _tray.Commands.Add(new(() => "打开消息中心", ShowWindow));
        _tray.Commands.Add(new(() => "发送消息", () => { ShowWindow(); ToggleComposer(true); }));
        _tray.Commands.Add(new(() => _notificationsPaused ? "恢复通知" : "暂停通知", () => { _notificationsPaused = !_notificationsPaused; if (_notificationsPaused) _notificationBuffer.Clear(); }, () => _notificationsPaused));
        _tray.Commands.Add(new(() => "重新连接", StartConnection));
        _tray.Commands.Add(new(() => "设置", OpenSettings));
        _tray.Commands.Add(null);
        _tray.Commands.Add(new(() => "退出", ExitApplication));
        _tray.Open = ShowWindow;
        _tray.BalloonClicked = () => OpenFromActivation(_lastNotifiedMessage?.Id);
        _tray.Show();
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) Hide(); };
        Closing += (_, e) => { if (!_exiting) { e.Cancel = true; Hide(); } };
    }
    private void ApplyConfig(bool updateStartup = true)
    {
        _notificationBuffer.Clear();
        ThemeManager.Apply(_config.Theme);
        ThemeButton.Content = ThemeManager.IsDark ? "浅色模式" : "深色模式";
        ServerLabel.Text = string.IsNullOrWhiteSpace(_config.ServerUrl) ? "请设置你自己的 Gotify 服务器" : _config.ServerUrl;
        RefreshSendContext();
        SetStatus(string.IsNullOrEmpty(_config.ClientToken) ? "需要配置" : "未连接", Colors.Gray);
        if (updateStartup) UpdateStartup();
        UpdateCount();
    }
    private void LoadHistory()
    {
        try
        {
            if (!File.Exists(HistoryPath)) return;
            using var input = File.OpenRead(HistoryPath);
            foreach (var item in (JsonSerializer.Deserialize<List<GotifyMessage>>(input) ?? new()).OrderByDescending(x => x.Id).Take(HistoryLimit))
            {
                if (item.AppId == 0 && item.AppName.StartsWith("App ") && int.TryParse(item.AppName[4..], out var appId)) item.AppId = appId;
                if (!item.AppIconPath.StartsWith("pack:") && !File.Exists(item.AppIconPath)) item.AppIconPath = "pack://application:,,,/Assets/logo.png";
                _messages.Add(item);
            }
            UpdateCount();
        }
        catch { SetNotice("本地历史记录无法读取，可刷新服务器消息。"); }
    }
    private void SaveHistory()
    {
        try
        {
            using (var output = File.Create(HistoryPath + ".tmp")) JsonSerializer.Serialize(output, _messages.Take(HistoryLimit));
            File.Move(HistoryPath + ".tmp", HistoryPath, true);
        }
        catch { SetNotice("消息已接收，但本地历史保存失败，请检查磁盘和目录权限。"); }
    }
    private void StartConnection()
    {
        _streamCts?.Cancel();
        if (!TryConnectionServer(out _)) return;
        if (string.IsNullOrWhiteSpace(_config.ClientToken)) { SetStatus("需要 Client Token", Colors.DarkOrange); return; }
        _streamCts = new CancellationTokenSource();
        _ = StreamLoopAsync(_streamCts.Token);
    }
    internal bool TryConnectionServer(out Uri server)
    {
        try { server = new Uri(ManagementApi.NormalizeServer(_config.ServerUrl)); return true; }
        catch (ArgumentException)
        {
            server = null!;
            SetStatus("需要服务器地址", Colors.DarkOrange);
            SetNotice("请先在设置中填写你自己的 Gotify 服务器地址。", NoticeTone.Warning, NoticeAction.Settings);
            return false;
        }
    }
    private async Task StreamLoopAsync(CancellationToken ct)
    {
        var delay = 2;
        var server = new Uri(_config.ServerUrl);
        var token = _config.ClientToken;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                SetStatus("正在连接", Colors.DarkOrange);
                var uri = new UriBuilder(server) { Scheme = server.Scheme == "https" ? "wss" : "ws", Path = server.AbsolutePath.TrimEnd('/') + "/stream", Query = "token=" + Uri.EscapeDataString(token) }.Uri;
                using var socket = new ClientWebSocket();
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(25);
                socket.Options.CollectHttpResponseDetails = true;
                try { await socket.ConnectAsync(uri, ct); }
                catch (WebSocketException ex) when ((int)socket.HttpStatusCode >= 400) { throw new HttpRequestException(null, ex, socket.HttpStatusCode); }
                ct.ThrowIfCancellationRequested();
                delay = 2; SetStatus("已连接", Colors.SeaGreen);
                await SyncRecentMessagesAsync(server, token, ct);
                _ = FetchApplicationsAsync(server, token, ct);
                var buffer = new byte[16 * 1024];
                while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    using var ms = new MemoryStream();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                        if (result.MessageType == WebSocketMessageType.Close) break;
                        ms.Write(buffer, 0, result.Count);
                        if (ms.Length > 4 * 1024 * 1024) throw new InvalidDataException();
                    } while (!result.EndOfMessage);
                    if (result.MessageType == WebSocketMessageType.Close) break;
                    var wire = JsonSerializer.Deserialize<WireMessage>(ms.ToArray());
                    if (wire is not null && !ct.IsCancellationRequested) AddMessage(wire);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { if (!ct.IsCancellationRequested) SetNotice(UserError.Describe(ex, "Client Token"), NoticeTone.Error, ActionForError(ex, NoticeAction.Reconnect)); }
            if (!ct.IsCancellationRequested)
            {
                SetStatus($"{delay} 秒后重连", Colors.DarkOrange);
                try { await Task.Delay(TimeSpan.FromSeconds(delay), ct); } catch (OperationCanceledException) { break; }
                delay = Math.Min(delay * 2, 60);
            }
        }
    }
    private void AddMessage(WireMessage wire) => InsertMessage(wire, true, true);
    private void InsertMessage(WireMessage wire, bool notify, bool persist, bool? unread = null)
    {
        if (_messages.Any(x => x.Id == wire.id && wire.id != 0)) return;
        var msg = new GotifyMessage { Id = wire.id, AppId = wire.appid, AppName = $"App {wire.appid}", Title = wire.title ?? "Gotify", Message = wire.message ?? "", Priority = wire.priority, Date = wire.date == default ? DateTime.Now : wire.date.ToLocalTime(), IsUnread = unread ?? notify, Extras = wire.extras };
        ApplyApplication(msg);
        int index = 0;
        while (index < _messages.Count && _messages[index].Id > msg.Id) index++;
        _messages.Insert(index, msg);
        while (_messages.Count > HistoryLimit) _messages.RemoveAt(_messages.Count - 1);
        if (!_batchingHistory) UpdateCount(); if (persist) SaveHistory();
        if (notify) QueueNotification(msg);
    }
    private Task SyncRecentMessagesAsync(Uri server, string token, CancellationToken ct) => FetchHistoryAsync(server, token, false, ct);
    private async Task FetchHistoryAsync(Uri server, string token, bool older, CancellationToken ct)
    {
        if (_exiting || ct.IsCancellationRequested) return;
        try { if (!await _historyGate.WaitAsync(0, ct)) return; }
        catch (OperationCanceledException) { return; }
        RefreshButton.IsEnabled = OlderButton.IsEnabled = false;
        try
        {
            if (string.IsNullOrWhiteSpace(token)) { SetNotice("请先填写 Client Token 以拉取消息。", NoticeTone.Warning, NoticeAction.Settings); return; }
            if (older && _messages.Count >= HistoryLimit) { SetNotice($"已达到本地 {HistoryLimit} 条容量。"); return; }
            long cursor = older ? _messages.Where(x => x.Id > 0).Select(x => x.Id).DefaultIfEmpty(0).Min() : 0;
            var endpoint = new UriBuilder(server) { Path = server.AbsolutePath.TrimEnd('/') + "/message", Query = "limit=100" + (cursor > 0 ? "&since=" + cursor : "") }.Uri;
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            request.Headers.Add("X-Gotify-Key", token);
            using var response = await _http.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            var page = await response.Content.ReadFromJsonAsync<WireMessagePage>(cancellationToken: ct);
            if (page?.messages is null) throw new InvalidDataException();
            ct.ThrowIfCancellationRequested();
            _batchingHistory = true;
            try { foreach (var item in page.messages.Take(100)) InsertMessage(item, false, false, !older); }
            finally { _batchingHistory = false; }
            _hasMore = page.messages.Count > 0;
            SaveHistory(); _view.Refresh(); UpdateCount();
            LastSyncLabel.Text = $"最后同步：{DateTime.Now:MM-dd HH:mm:ss}";
            SetNotice(page.messages.Count == 0 && older ? "没有更早的服务器消息。" : $"已{(older ? "加载" : "同步")} {Math.Min(page.messages.Count, 100)} 条服务器消息", NoticeTone.Success, transient: true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { if (!ct.IsCancellationRequested) SetNotice("消息拉取失败；" + UserError.Describe(ex, "Client Token") + " 实时接收继续。", NoticeTone.Error, ActionForError(ex, NoticeAction.History)); }
        finally { RefreshButton.IsEnabled = true; OlderButton.IsEnabled = _hasMore && _messages.Count < HistoryLimit; _historyGate.Release(); }
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (!TryConnectionServer(out var server)) return;
        await FetchHistoryAsync(server, _config.ClientToken, false, _streamCts?.Token ?? CancellationToken.None);
        _ = FetchApplicationsAsync(server, _config.ClientToken, _streamCts?.Token ?? CancellationToken.None);
    }
    private async void Older_Click(object sender, RoutedEventArgs e)
    {
        if (TryConnectionServer(out var server)) await FetchHistoryAsync(server, _config.ClientToken, true, _streamCts?.Token ?? CancellationToken.None);
    }
    private void Search_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e) { if (_view is not null) { _view.Refresh(); UpdateCount(); } }
    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        var msg = _selectedMessage ?? MessagesList.SelectedItem as GotifyMessage;
        if (msg is null) { SetNotice("请先选中一条消息。"); return; }
        try { System.Windows.Clipboard.SetText(CopyText(msg)); SetNotice("消息已复制。", NoticeTone.Success, transient: true); } catch { SetNotice("剪贴板暂不可用，请重试。", NoticeTone.Warning); }
    }
    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "清空本机消息记录？服务器消息不会删除，刷新后可重新拉取。", "清空本机记录", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        ClearLocalHistory();
    }
    internal void ClearLocalHistory() { _notificationBuffer.Clear(); _messages.Clear(); ClearDetails(); _lastNotifiedMessage = null; _hasMore = true; OlderButton.IsEnabled = true; SaveHistory(); UpdateCount(); SetNotice("本机记录已清空。", NoticeTone.Success, transient: true); }
    private void UpdateCount()
    {
        RefreshApplicationChoices();
        int unread = _messages.Count(x => x.IsUnread);
        UnreadLabel.Text = $"{unread} 未读";
        CountLabel.Text = string.IsNullOrWhiteSpace(SearchBox.Text) && UnreadOnlyBox.IsChecked != true && !HasExtraFilters ? $"{_messages.Count} 条消息 · {unread} 未读" : $"匹配 {_view.Cast<object>().Count()} / {_messages.Count} 条 · {unread} 未读";
        UpdateEmptyState();
    }
    private void SetStatus(string text, System.Windows.Media.Color color)
    {
        StatusLabel.Text = text;
        string key = text == "已连接" ? "Success" : text.Contains("连接") || text.Contains("重连") ? "Warning" : "Muted";
        StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, key + "Brush");
        StatusLabel.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, key + "Brush");
        ConnectionBadge.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, key == "Muted" ? "RaisedBrush" : key + "SoftBrush");
    }
    internal static bool ShouldNotify(bool enabled, bool paused, int priority, int minimum = 8) => enabled && !paused && priority >= minimum && priority <= 10;

    private async void Send_Click(object sender, RoutedEventArgs e) => await SubmitMessageAsync();
    private void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings();
    private void OpenSettings()
    {
        ShowWindow();
        string previousServer = _config.ServerUrl, previousToken = _config.ClientToken;
        var dialog = new SettingsWindow(_config, () =>
        {
            try { if (_toast != null) _toast.Show(new GotifyMessage { Id = 0, Title = "Gotify Desktop 通知测试", Message = "这是一条本机测试通知。", AppName = "Gotify Desktop" }); else _tray.ShowBalloonTip(3000, "Gotify Desktop", "这是一条托盘测试通知。"); }
            catch (Exception) { SetNotice("通知发送失败，请检查 Windows 通知设置及勿扰模式。"); }
        }, GetKnownApplications()) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        bool connectionChanged = dialog.Config.ResetApplicationRulesForConnection(previousServer, previousToken);
        try { dialog.Config.Save(ConfigPath); } catch (Exception) { SetNotice("配置保存失败，请检查目录权限。"); return; }
        _config = dialog.Config;
        if (connectionChanged)
        {
            _streamCts?.Cancel(); _applications.Clear(); ResetFilters(); ClearLocalHistory(); LastSyncLabel.Text = "尚未同步";
        }
        ApplyConfig(); StartConnection();
    }
    private void UpdateStartup()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (_config.RunAtStartup) key.SetValue("GotifyDesktop", StartupCommand(Environment.ProcessPath!)); else key.DeleteValue("GotifyDesktop", false);
        }
        catch { SetNotice("开机启动设置未保存，请检查用户目录/注册表权限。"); }
    }
    internal void OpenFromActivation(long? id)
    {
        ShowWindow();
        if (id is null) return;
        ResetFilters();
        var message = _messages.FirstOrDefault(x => x.Id == id);
        if (message is not null) { MessagesList.SelectedItem = message; SelectMessage(message); MessagesList.ScrollIntoView(message); }
        else SetNotice("此通知对应的消息已不在本机记录中，可刷新或加载更早消息。");
    }
    private void ShowWindow() { Show(); WindowState = WindowState.Normal; Activate(); }
    private void ExitApplication() { DisposeResources(); System.Windows.Application.Current.Shutdown(); }
    internal void DisposeResources() { _exiting = true; _memoryIdleTimer?.Stop(); _noticeTimer?.Stop(); _filterTimer?.Stop(); _notificationTimer?.Stop(); _notificationBuffer.Clear(); _streamCts?.Cancel(); _tray.Dispose(); _http.Dispose(); }

    private void ApplyApplication(GotifyMessage msg)
    {
        if (_applications.TryGetValue(msg.AppId, out var app)) { msg.AppName = app.Name; msg.AppIconPath = app.Icon; }
    }
    private async Task FetchApplicationsAsync(Uri server, string token, CancellationToken ct)
    {
        if (_appsBusy || string.IsNullOrWhiteSpace(token)) return;
        _appsBusy = true;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, new UriBuilder(server) { Path = server.AbsolutePath.TrimEnd('/') + "/application", Query = "" }.Uri);
            req.Headers.Add("X-Gotify-Key", token);
            using var res = await _http.SendAsync(req, ct); res.EnsureSuccessStatusCode();
            var apps = await res.Content.ReadFromJsonAsync<List<WireApplication>>(cancellationToken: ct) ?? new();
            ct.ThrowIfCancellationRequested();
            if (server.ToString().TrimEnd('/') != _config.ServerUrl.TrimEnd('/') || token != _config.ClientToken) return;
            UpdateSendApplications(apps.Select(x => new ManagedApplication { Id = x.id, Name = x.name ?? "", DefaultPriority = x.defaultPriority }));
            foreach (var app in apps.Take(200))
            {
                ct.ThrowIfCancellationRequested();
                string icon = await CacheApplicationIconAsync(server, token, app.image, ct);
                ct.ThrowIfCancellationRequested();
                _applications[app.id] = (string.IsNullOrWhiteSpace(app.name) ? $"App {app.id}" : app.name, icon);
            }
            foreach (var msg in _messages) ApplyApplication(msg);
            _view.Refresh(); UpdateCount(); SaveHistory();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { if (!ct.IsCancellationRequested && !_exiting) SetNotice("应用名称/图标更新失败；" + UserError.Describe(ex, "Client Token"), NoticeTone.Warning, ActionForError(ex, NoticeAction.History)); }
        finally { _appsBusy = false; }
    }
    private async Task<string> CacheApplicationIconAsync(Uri server, string token, string? image, CancellationToken ct)
    {
        const string fallback = "pack://application:,,,/Assets/logo.png";
        if (string.IsNullOrWhiteSpace(image)) return fallback;
        var uri = new Uri(new Uri(server.ToString().TrimEnd('/') + "/"), image.TrimStart('/'));
        if (uri.Scheme != server.Scheme || uri.Host != server.Host || uri.Port != server.Port) return fallback;
        string directory = Path.Combine(_dataDir, "icons"); Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri))) + ".png");
        if (File.Exists(path)) return path;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, uri); req.Headers.Add("X-Gotify-Key", token);
            using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct); res.EnsureSuccessStatusCode();
            using var input = await res.Content.ReadAsStreamAsync(ct); using var data = new MemoryStream();
            var buffer = new byte[8192]; int read;
            while ((read = await input.ReadAsync(buffer, ct)) > 0) { data.Write(buffer, 0, read); if (data.Length > 2 * 1024 * 1024) return fallback; }
            data.Position = 0;
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.DecodePixelWidth = 64; bitmap.StreamSource = data; bitmap.EndInit(); bitmap.Freeze();
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(path + ".tmp"); encoder.Save(output); output.Close(); File.Move(path + ".tmp", path, true);
            return path;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return fallback; }
    }
}




