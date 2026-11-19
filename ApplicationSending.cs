using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Windows;
using System.Windows.Controls;

namespace GotifyDesktop;

internal sealed record SendApplicationChoice(int? Id, string Name, int DefaultPriority = 5, bool ManualToken = false)
{
    public string DisplayName => Id.HasValue ? $"{Name}（#{Id}）" : Name;
    internal string Key => ManualToken ? "manual" : Id?.ToString() ?? "none";
}

public partial class MainWindow
{
    private List<SendApplicationChoice> _sendCatalog = new();
    private string _sendCatalogServer = "", _sendCatalogToken = "", _lastAutomaticPriority = "5";
    private bool _updatingSendChoices, _sendSelectionInitialized, _sendCatalogLoaded;
    private long _sendTargetRevision;
    private SendApplicationChoice? SelectedSendApplication => SendApplicationBox.SelectedItem as SendApplicationChoice;
    private void InitializeSendApplicationPicker()
    {
        ThemeManager.TrackComboBox(SendApplicationBox);
        MainContentGrid.SizeChanged += (_, _) => ResizeComposer();
        MainHeaderGrid.SizeChanged += (_, _) => ResizeComposer();
        NoticePanel.SizeChanged += (_, _) => ResizeComposer();
        NoticePanel.IsVisibleChanged += (_, _) => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(ResizeComposer));
        RefreshSendContext();
    }
    private void ResizeComposer()
    {
        if (MainContentGrid.ActualHeight <= 0) return;
        double available = MainContentGrid.ActualHeight - MainContentGrid.RowDefinitions[0].ActualHeight - MainContentGrid.RowDefinitions[1].ActualHeight - ComposerPanel.Margin.Top;
        double maximum = Math.Max(120, Math.Floor(available - 40));
        if (Math.Abs(ComposerPanel.MaxHeight - maximum) > 0.5) ComposerPanel.MaxHeight = maximum;
    }
    internal void RefreshSendContext()
    {
        if (_sendCatalogServer != _config.ServerUrl.TrimEnd('/') || _sendCatalogToken != _config.ClientToken)
        {
            _sendCatalogServer = _config.ServerUrl.TrimEnd('/'); _sendCatalogToken = _config.ClientToken;
            _sendCatalog.Clear(); _sendCatalogLoaded = false; _sendSelectionInitialized = false; _sendTargetRevision++;
            _updatingSendChoices = true; SendApplicationBox.SelectedItem = null; _updatingSendChoices = false;
        }
        RefreshSendChoices();
    }
    internal void UpdateSendApplications(IEnumerable<ManagedApplication> apps)
    {
        if (string.IsNullOrWhiteSpace(_config.ClientToken)) { RefreshSendContext(); return; }
        _sendCatalog = apps.Where(x => x.Id > 0).GroupBy(x => x.Id).Select(x => x.First())
            .Select(x => new SendApplicationChoice(x.Id, string.IsNullOrWhiteSpace(x.Name) ? $"App {x.Id}" : x.Name, Math.Clamp(x.DefaultPriority, 0, 10)))
            .OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(x => x.Id).ToList();
        _sendCatalogLoaded = true; RefreshSendChoices();
    }
    private void RefreshSendChoices()
    {
        if (SendApplicationBox == null) return;
        var previous = SelectedSendApplication;
        var none = new SendApplicationChoice(null, "请选择发送应用");
        var manual = new SendApplicationChoice(null, "使用已配置的 Application Token", ManualToken: true);
        var choices = new[] { none }.Concat(_sendCatalog).Append(manual).ToList();
        SendApplicationChoice selected = previous?.ManualToken == true ? manual
            : previous?.Id is int id ? choices.FirstOrDefault(x => x.Id == id) ?? none
            : !_sendSelectionInitialized ? !string.IsNullOrEmpty(_config.ApplicationToken) ? manual : _sendCatalog.FirstOrDefault() ?? none
            : none;
        bool removed = previous?.Id.HasValue == true && selected.Id == null;
        if (previous?.Key != selected.Key) _sendTargetRevision++;
        _updatingSendChoices = true;
        SendApplicationBox.ItemsSource = choices; SendApplicationBox.SelectedItem = selected;
        _updatingSendChoices = false;
        if (selected.Id.HasValue || selected.ManualToken) _sendSelectionInitialized = true;
        if (previous?.Key != selected.Key && selected.Id.HasValue) ApplySendDefaultPriority(selected);
        UpdateSendHint(removed);
    }
    private void ApplySendDefaultPriority(SendApplicationChoice choice)
    {
        if (choice.Id.HasValue && PriorityBox.Text.Trim() == _lastAutomaticPriority)
        { _lastAutomaticPriority = choice.DefaultPriority.ToString(); PriorityBox.Text = _lastAutomaticPriority; }
    }
    private void SendApplication_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingSendChoices || _config == null || PriorityBox == null) return;
        _sendSelectionInitialized = true; _sendTargetRevision++;
        if (SelectedSendApplication is { } selected) ApplySendDefaultPriority(selected);
        UpdateSendHint();
    }
    private void UpdateSendHint(bool removed = false)
    {
        SendApplicationHint.Text = removed ? "所选应用已不在当前列表，请重新选择发送应用。"
            : SelectedSendApplication?.ManualToken == true ? string.IsNullOrWhiteSpace(_config.ApplicationToken) ? "请先在设置中填写 Application Token。" : "使用设置中的 Application Token 发送。"
            : SelectedSendApplication?.Id.HasValue == true ? "使用当前账号发送到所选应用，无需另外填写应用 Token。"
            : string.IsNullOrWhiteSpace(_config.ClientToken) ? "登录后可直接选择应用，或在设置中填写 Application Token。"
            : _sendCatalogLoaded ? _sendCatalog.Count > 0 ? "请选择要发送的应用。" : "当前账号没有可选应用，可先在“管理”中新建应用。" : "点击“刷新应用”获取当前账号的应用列表。";
    }
    private async void RefreshSendApplications_Click(object sender, RoutedEventArgs e) => await RefreshSendApplications();
    private async Task RefreshSendApplications()
    {
        if (_sending || _appsBusy) return;
        if (string.IsNullOrWhiteSpace(_config.ClientToken)) { SetNotice("请先账号登录或配置 Client Token。", NoticeTone.Warning, NoticeAction.Settings); return; }
        if (!TryConnectionServer(out var server)) return;
        RefreshSendAppsButton.IsEnabled = false;
        try { await FetchApplicationsAsync(server, _config.ClientToken, _streamCts?.Token ?? CancellationToken.None); }
        finally { if (!_exiting && !_sending) RefreshSendAppsButton.IsEnabled = true; }
    }
    internal async Task SubmitMessageAsync()
    {
        if (_sending) return;
        var selected = SelectedSendApplication;
        bool byApplication = selected?.Id.HasValue == true;
        if (!byApplication && selected?.ManualToken != true) { SetNotice("请选择发送应用，或选择 Application Token 发送方式。", NoticeTone.Warning); return; }
        string credential = byApplication ? _config.ClientToken : _config.ApplicationToken;
        string credentialName = byApplication ? "Client Token" : "Application Token";
        if (string.IsNullOrWhiteSpace(credential)) { SetNotice($"请先配置 {credentialName}。", NoticeTone.Warning, NoticeAction.Settings); return; }
        if (byApplication && !_sendCatalog.Any(x => x.Id == selected!.Id)) { SetNotice("所选应用已失效，请刷新后重新选择。", NoticeTone.Warning); return; }
        string title = TitleBox.Text.Trim(), body = MessageContentBox.Text.Trim(), priorityText = PriorityBox.Text.Trim();
        if (body.Length == 0) { SetNotice("请输入消息内容。"); return; }
        if (!int.TryParse(priorityText, out int priority) || priority < 0 || priority > 10) { SetNotice("优先级须为 0～10 的整数。"); return; }
        if (!TryConnectionServer(out _)) return;
        string server = _config.ServerUrl.TrimEnd('/'); long targetRevision = _sendTargetRevision;
        _sending = true; SendButton.IsEnabled = SendApplicationBox.IsEnabled = RefreshSendAppsButton.IsEnabled = false; SendButton.Content = "发送中…";
        try
        {
            object payload = byApplication ? new { title, message = body, priority, appid = selected!.Id!.Value } : new { title, message = body, priority };
            using var request = new HttpRequestMessage(HttpMethod.Post, server + "/message") { Content = JsonContent.Create(payload) };
            request.Headers.Add("X-Gotify-Key", credential);
            using var response = await _http.SendAsync(request); response.EnsureSuccessStatusCode();
            if (_exiting) return;
            bool sameTarget = server == _config.ServerUrl.TrimEnd('/') && credential == (byApplication ? _config.ClientToken : _config.ApplicationToken) && targetRevision == _sendTargetRevision;
            bool unchangedDraft = TitleBox.Text.Trim() == title && MessageContentBox.Text.Trim() == body && PriorityBox.Text.Trim() == priorityText;
            if (sameTarget && unchangedDraft) { TitleBox.Clear(); MessageContentBox.Clear(); ToggleComposer(false); }
            SetNotice(sameTarget ? "消息已发送" : "消息已发送到原先选择的目标，当前草稿已保留。", NoticeTone.Success, transient: true);
        }
        catch (Exception ex)
        {
            if (!_exiting)
            {
                string extra = byApplication && ex is HttpRequestException { StatusCode: HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound }
                    ? " 请刷新应用并检查账号权限；按应用发送需要 Gotify 3.x。" : "";
                SetNotice("发送失败；" + UserError.Describe(ex, credentialName) + extra + " 内容已保留，请确认服务器是否已收到后再重试。", NoticeTone.Error, ActionForError(ex, NoticeAction.Compose));
            }
        }
        finally
        {
            _sending = false;
            if (!_exiting) { SendButton.IsEnabled = SendApplicationBox.IsEnabled = RefreshSendAppsButton.IsEnabled = true; SendButton.Content = "发送消息"; }
        }
    }
}
