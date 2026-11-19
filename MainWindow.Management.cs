using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using ListViewItem = System.Windows.Controls.ListViewItem;
using MessageBox = System.Windows.MessageBox;

namespace GotifyDesktop;

public partial class MainWindow
{
    internal AppConfig ManagementConfig => _config;
    private bool _deletingServerMessages;
    private long? _contextDeleteMessageId;
    private void Management_Click(object sender, RoutedEventArgs e)
    {
        if (!TryConnectionServer(out _)) { OpenSettings(); if (!TryConnectionServer(out _)) return; }
        try { new ManagementWindow(this, Path.Combine(_dataDir, "tokens.json")) { Owner = this }.ShowDialog(); }
        catch (Exception ex) { SetNotice(ManagementWindow.Describe(ex), NoticeTone.Error); }
    }
    private AppConfig CopyManagementConfig()
    {
        var copy = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(_config))!;
        copy.ClientToken = _config.ClientToken; copy.ApplicationToken = _config.ApplicationToken; return copy;
    }
    internal void UseManagedClient(string server, string token, bool identityChanged)
    {
        var next = CopyManagementConfig(); next.ServerUrl = ManagementApi.NormalizeServer(server); next.ClientToken = token;
        identityChanged |= next.ServerUrl != _config.ServerUrl.TrimEnd('/');
        if (identityChanged) { next.ApplicationToken = ""; next.ApplicationNotifications.Clear(); }
        next.Save(ConfigPath); _streamCts?.Cancel(); _config = next;
        if (identityChanged) { _applications.Clear(); ResetFilters(); ClearLocalHistory(); LastSyncLabel.Text = "尚未同步"; }
        ApplyConfig(false); StartConnection();
    }
    internal void UseManagedApplication(string token, bool selectForSending = false)
    {
        var next = CopyManagementConfig(); next.ApplicationToken = token; next.Save(ConfigPath); _config = next; RefreshSendContext();
        if (selectForSending && !string.IsNullOrWhiteSpace(token))
            SendApplicationBox.SelectedItem = SendApplicationBox.Items.OfType<SendApplicationChoice>().Single(x => x.ManualToken);
    }
    internal void DisconnectManagedClient()
    {
        var next = CopyManagementConfig(); next.ClientToken = ""; next.Save(ConfigPath); _streamCts?.Cancel(); _config = next; ApplyConfig(false);
    }
    internal void UpdateManagedApplications(IEnumerable<ManagedApplication> applications)
    {
        var apps = applications.ToList();
        UpdateSendApplications(apps);
        foreach (int stale in _applications.Keys.Except(apps.Select(x => x.Id)).ToArray()) _applications.Remove(stale);
        foreach (var app in apps) _applications[app.Id] = (app.Name, _applications.TryGetValue(app.Id, out var current) ? current.Icon : "pack://application:,,,/Assets/logo.png");
        foreach (var message in _messages) ApplyApplication(message);
        _view.Refresh(); UpdateCount(); SaveHistory();
    }
    internal void ForgetManagedApplication(int id)
    {
        _applications.Remove(id); _sendCatalog.RemoveAll(x => x.Id == id); RefreshSendChoices(); _config.ApplicationNotifications.Remove(id); _config.Save(ConfigPath); UpdateCount();
    }
    internal void RefreshManagedIcons()
    {
        ((SmallIconConverter)FindResource("SmallIcon")).Clear();
        var directory = Path.Combine(_dataDir, "icons");
        if (Directory.Exists(directory)) foreach (var path in Directory.EnumerateFiles(directory, "*.png")) { try { File.Delete(path); } catch (IOException) { } }
        if (TryConnectionServer(out var server)) _ = FetchApplicationsAsync(server, _config.ClientToken, _streamCts?.Token ?? CancellationToken.None);
    }
    internal void RemoveManagedMessages(long? messageId = null, int? applicationId = null, HashSet<long>? snapshot = null)
    {
        var deleted = _messages.Where(x => (!messageId.HasValue || x.Id == messageId) && (!applicationId.HasValue || x.AppId == applicationId) && (snapshot == null || snapshot.Contains(x.Id))).ToArray();
        foreach (var message in deleted) _messages.Remove(message);
        if (_selectedMessage != null && !_messages.Contains(_selectedMessage)) ClearDetails();
        _notificationBuffer.DiscardMessages(deleted.Select(x => x.Id).ToHashSet()); if (_lastNotifiedMessage != null && !_messages.Contains(_lastNotifiedMessage)) _lastNotifiedMessage = null;
        _hasMore = true; OlderButton.IsEnabled = true; SaveHistory(); _view.Refresh(); UpdateCount();
    }
    internal async Task DeleteManagedMessages(ManagementApi api, long? messageId = null, int? applicationId = null)
    {
        // Serialize with history sync. Keep messages arriving over WebSocket during the request;
        // they may have been created after the server applied this deletion.
        await _historyGate.WaitAsync();
        try
        {
            var snapshot = _messages.Select(x => x.Id).ToHashSet();
            await api.DeleteMessages(messageId, applicationId);
            RemoveManagedMessages(messageId, applicationId, snapshot);
        }
        finally { _historyGate.Release(); }
    }
    internal async Task DeleteManagedApplication(ManagementApi api, int id, AccountCredentials credentials)
    {
        await _historyGate.WaitAsync();
        try { await api.DeleteApplication(id, credentials); RemoveManagedMessages(applicationId: id); }
        finally { _historyGate.Release(); }
    }
    internal long? ResolveServerContextTarget(DependencyObject source, bool keyboard)
    {
        var row = ItemsControl.ContainerFromElement(MessagesList, source) as ListViewItem;
        var target = row?.Content as GotifyMessage ?? (keyboard ? MessagesList.SelectedItem as GotifyMessage : null);
        _contextDeleteMessageId = target?.Id; DeleteServerMenu.IsEnabled = target != null;
        return _contextDeleteMessageId;
    }
    private void MessageContext_Opening(object sender, ContextMenuEventArgs e) => ResolveServerContextTarget((DependencyObject)e.OriginalSource, e.CursorLeft < 0);
    private async Task ConfirmServerDeletion(bool all, long? messageId = null)
    {
        if (_deletingServerMessages) return;
        long? id = all ? null : messageId;
        if (!all && id == null) { SetNotice("请先选择要删除的消息。", NoticeTone.Warning); return; }
        if (string.IsNullOrEmpty(_config.ClientToken)) { SetNotice("请先登录或配置 Client Token。", NoticeTone.Warning, NoticeAction.Settings); return; }
        var target = _messages.FirstOrDefault(x => x.Id == id);
        string prompt = all ? "永久清空当前账号的全部服务器消息？\n\n包括筛选范围之外、尚未加载的消息。消息会从服务器永久移除，其他设备的本地缓存可能保留。无法撤销。" : $"永久删除这条服务器消息？\n\n#{id} · {target?.Title}\n\n消息会从服务器永久移除，其他设备的本地缓存可能保留。无法撤销。";
        if (MessageBox.Show(this, prompt, "删除服务器消息", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        _deletingServerMessages = true;
        try
        {
            using var api = new ManagementApi(_config.ServerUrl, _config.ClientToken);
            await DeleteManagedMessages(api, id); SetNotice(all ? "服务器消息已清空。" : "服务器消息已删除。", NoticeTone.Success, transient: true);
        }
        catch (Exception ex) { SetNotice(ManagementWindow.Describe(ex) + " 未确认成功前不会移除本机消息。", NoticeTone.Error); }
        finally { _deletingServerMessages = false; }
    }
    private async void DeleteServerMessage_Click(object sender, RoutedEventArgs e) => await ConfirmServerDeletion(false, _contextDeleteMessageId);
    private async void ClearServerMessages_Click(object sender, RoutedEventArgs e) => await ConfirmServerDeletion(true);
}



