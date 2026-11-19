using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace GotifyDesktop;

internal enum NoticeTone { Info, Success, Error, Warning }
internal enum NoticeAction { None, Settings, Reconnect, History, Compose }

public partial class MainWindow
{
    internal static NoticeAction ActionForError(Exception ex, NoticeAction fallback) => ex is System.Net.Http.HttpRequestException http && http.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.NotFound ? NoticeAction.Settings : fallback;
    private GotifyMessage? _selectedMessage;
    private DispatcherTimer? _noticeTimer;
    private DateTime? _noticeExpiresAt;
    private NoticeAction _noticeAction;
    private bool _pendingReadRefresh;

    private void InitializeExperience()
    {
        InitializeFilters();
        InitializeNotificationPolicy();
        DetailRichBody.LayoutUpdated += ResizeRichReader;
        ThemeManager.TrackMenu(MoreButton.ContextMenu);
        ThemeManager.TrackMenu(MessagesList.ContextMenu);
        SourceInitialized += (_, _) => ThemeManager.TitleBar(this);
        ThemeButton.Content = ThemeManager.IsDark ? "浅色模式" : "深色模式";
        _noticeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _noticeTimer.Tick += (_, _) => { if (_noticeExpiresAt is DateTime until && DateTime.UtcNow >= until) DismissNotice(); };
        UpdateEmptyState();
    }
    internal void SetNotice(string text, NoticeTone tone = NoticeTone.Info, NoticeAction action = NoticeAction.None, bool transient = false)
    {
        _noticeTimer?.Stop(); _noticeExpiresAt = null;
        NoticeLabel.Text = text;
        _noticeAction = action;
        NoticePanel.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        NoticeHeading.Text = tone switch { NoticeTone.Error => "需要处理", NoticeTone.Warning => "请留意", NoticeTone.Success => "已完成", _ => "提示" };
        string color = tone switch { NoticeTone.Error => "Danger", NoticeTone.Warning => "Warning", NoticeTone.Success => "Success", _ => "Accent" };
        NoticePanel.SetResourceReference(Border.BackgroundProperty, color + "SoftBrush");
        NoticeHeading.SetResourceReference(TextBlock.ForegroundProperty, color + "Brush");
        NoticeActionButton.Visibility = action == NoticeAction.None ? Visibility.Collapsed : Visibility.Visible;
        NoticeActionButton.Content = action switch { NoticeAction.Settings => "打开设置", NoticeAction.Reconnect => "重试连接", NoticeAction.History => "重新同步", NoticeAction.Compose => "查看草稿", _ => "" };
        if (transient && !string.IsNullOrEmpty(text)) { _noticeExpiresAt = DateTime.UtcNow.AddSeconds(4); _noticeTimer?.Start(); }
    }
    internal void DismissNotice() { _noticeTimer?.Stop(); _noticeExpiresAt = null; NoticeLabel.Text = ""; NoticePanel.Visibility = Visibility.Collapsed; _noticeAction = NoticeAction.None; }
    private void DismissNotice_Click(object sender, RoutedEventArgs e) => DismissNotice();
    private void NoticeAction_Click(object sender, RoutedEventArgs e)
    {
        switch (_noticeAction)
        {
            case NoticeAction.Settings: OpenSettings(); break;
            case NoticeAction.Reconnect: StartConnection(); break;
            case NoticeAction.History: Refresh_Click(sender, e); break;
            case NoticeAction.Compose: ToggleComposer(true); break;
        }
    }
    internal void ToggleComposer(bool? open = null)
    {
        bool show = open ?? ComposerPanel.Visibility != Visibility.Visible;
        ComposerPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ComposeToggle.Content = show ? "收起发送区" : "＋ 撰写消息";
        if (show)
        {
            TitleBox.Focus();
            if (_automaticServices && !_sendCatalogLoaded) _ = RefreshSendApplications();
        }
    }
    private void Compose_Click(object sender, RoutedEventArgs e) => ToggleComposer();
    private void More_Click(object sender, RoutedEventArgs e)
    {
        MoreButton.ContextMenu.PlacementTarget = MoreButton;
        MoreButton.ContextMenu.Placement = PlacementMode.Bottom;
        MoreButton.ContextMenu.IsOpen = true;
    }
    private void UnreadFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (_view is null) return;
        _view.Refresh();
        if (_selectedMessage is { IsUnread: false } selected && _view.Contains(selected)) MessagesList.SelectedItem = selected;
        UpdateCount();
    }
    private void Message_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0 && e.AddedItems[0] is GotifyMessage message) SelectMessage(message);
    }
    internal void SelectMessage(GotifyMessage message)
    {
        if (!ReferenceEquals(_selectedMessage, message)) DetailScroll.ScrollToTop();
        _selectedMessage = message;
        DetailPanel.DataContext = message;
        UpdateRichDetails(message);
        DetailPanel.Visibility = Visibility.Visible; DetailEmpty.Visibility = Visibility.Collapsed;
        if (message.IsUnread)
        {
            message.IsUnread = false; SaveHistory(); UpdateCount();
            if (UnreadOnlyBox.IsChecked == true && !_pendingReadRefresh)
            {
                _pendingReadRefresh = true;
                Dispatcher.BeginInvoke(() => { _pendingReadRefresh = false; if (!_exiting) { _view.Refresh(); UpdateCount(); } });
            }
        }
    }
    private void ClearDetails() { _selectedMessage = null; DetailPanel.DataContext = null; ReleaseRichReader(); DetailPanel.Visibility = Visibility.Collapsed; DetailEmpty.Visibility = Visibility.Visible; }
    internal void MarkAllRead()
    {
        foreach (var message in _messages) message.IsUnread = false;
        _view.Refresh(); SaveHistory(); UpdateCount();
        SetNotice("全部本机消息已标为已读。", NoticeTone.Success, transient: true);
    }
    private void MarkAllRead_Click(object sender, RoutedEventArgs e) => MarkAllRead();
    private void MarkUnread_Click(object sender, RoutedEventArgs e)
    {
        var message = MessagesList.SelectedItem as GotifyMessage ?? _selectedMessage;
        if (message is null) return;
        message.IsUnread = true; _view.Refresh(); SaveHistory(); UpdateCount();
        SetNotice("已标为未读。", NoticeTone.Success, transient: true);
    }
    private void UpdateEmptyState()
    {
        if (_view is null || _config is null) return;
        EmptyPanel.Visibility = _view.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        if (!string.IsNullOrWhiteSpace(SearchBox.Text))
        {
            EmptyTitle.Text = "没有找到匹配消息"; EmptyText.Text = "试试更短的关键词，或清除筛选查看全部记录。"; EmptyActionButton.Content = "清除筛选";
        }
        else if (HasExtraFilters)
        {
            EmptyTitle.Text = "没有符合筛选条件的消息"; EmptyText.Text = "试试其他应用或时间范围，也可以清除筛选查看全部记录。"; EmptyActionButton.Content = "清除筛选";
        }
        else if (UnreadOnlyBox.IsChecked == true)
        {
            EmptyTitle.Text = "未读消息已处理完"; EmptyText.Text = "新消息到达后会显示在这里，也可以查看全部消息。"; EmptyActionButton.Content = "查看全部";
        }
        else if (string.IsNullOrWhiteSpace(_config.ServerUrl) || string.IsNullOrWhiteSpace(_config.ClientToken))
        {
            EmptyTitle.Text = "连接你的 Gotify"; EmptyText.Text = "在设置中填写你自己的服务器地址和 Client Token，即可接收并拉取消息。"; EmptyActionButton.Content = "打开设置";
        }
        else
        {
            EmptyTitle.Text = "还没有消息"; EmptyText.Text = "连接成功后自动拉取最近消息，也可以立即刷新。"; EmptyActionButton.Content = "刷新消息";
        }
        if (_selectedMessage != null && !_messages.Contains(_selectedMessage)) ClearDetails();
    }
    private void EmptyAction_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(SearchBox.Text) || UnreadOnlyBox.IsChecked == true || HasExtraFilters) ResetFilters();
        else if (string.IsNullOrWhiteSpace(_config.ServerUrl) || string.IsNullOrWhiteSpace(_config.ClientToken)) OpenSettings();
        else Refresh_Click(sender, e);
    }
    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        _config.Theme = ThemeManager.IsDark ? "Light" : "Dark";
        ThemeManager.Apply(_config.Theme);
        ThemeButton.Content = ThemeManager.IsDark ? "浅色模式" : "深色模式";
        try { _config.Save(ConfigPath); } catch { SetNotice("主题已切换，但偏好保存失败，请检查目录权限。", NoticeTone.Warning); }
    }
    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        if (e.Key == Key.F) { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; }
        else if (e.Key == Key.N) { ToggleComposer(true); e.Handled = true; }
        else if (e.Key == Key.Enter && ComposerPanel.Visibility == Visibility.Visible) { Send_Click(sender, e); e.Handled = true; }
    }
}
