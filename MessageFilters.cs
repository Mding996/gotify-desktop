using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace GotifyDesktop;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ApplicationNotificationMode { FollowDefault, Enabled, Disabled }

public sealed record KnownApplication(int Id, string Name)
{
    public string DisplayName => $"{Name}  ·  ID {Id}";
}

internal sealed record ApplicationFilterChoice(int? Id, string Name)
{
    public string DisplayName => Id is null ? "全部应用" : $"{Name} · ID {Id}";
}

internal static class MessageTimeFilter
{
    internal static bool Matches(DateTime date, int preset, DateTime now, DateTime? from, DateTime? until)
    {
        var bounds = preset switch
        {
            1 => ((DateTime?)now.Date, (DateTime?)now.Date.AddDays(1)),
            2 => ((DateTime?)now.AddHours(-24), (DateTime?)now.AddTicks(1)),
            3 => ((DateTime?)now.AddDays(-7), (DateTime?)now.AddTicks(1)),
            4 => ((DateTime?)now.AddDays(-30), (DateTime?)now.AddTicks(1)),
            5 => (from, until),
            _ => ((DateTime?)null, (DateTime?)null)
        };
        return (bounds.Item1 is null || date >= bounds.Item1) && (bounds.Item2 is null || date < bounds.Item2);
    }

    internal static bool TryRange(string start, string end, out DateTime? from, out DateTime? until, out string error)
    {
        from = until = null; error = "";
        string[] formats = { "yyyy-MM-dd", "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss" };
        if (!string.IsNullOrWhiteSpace(start))
        {
            if (!DateTime.TryParseExact(start.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)) { error = "开始时间格式不正确。"; return false; }
            from = value;
        }
        if (!string.IsNullOrWhiteSpace(end))
        {
            if (!DateTime.TryParseExact(end.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)) { error = "结束时间格式不正确。"; return false; }
            try { until = end.Trim().Length == 10 ? value.AddDays(1) : end.Trim().Length == 16 ? value.AddMinutes(1) : value.AddSeconds(1); }
            catch (ArgumentOutOfRangeException) { error = "结束时间超出支持范围。"; return false; }
        }
        if (from.HasValue && until.HasValue && until <= from) { error = "结束时间不能早于开始时间。"; return false; }
        return true;
    }
}

public partial class MainWindow
{
    private readonly ObservableCollection<ApplicationFilterChoice> _filterApplications = new();
    private bool _updatingFilterApplications;
    private DateTime? _customFrom, _customUntil;
    private DispatcherTimer? _filterTimer;
    private bool _customApplied;

    private void InitializeFilters()
    {
        ThemeManager.TrackComboBox(ApplicationFilter);
        ThemeManager.TrackComboBox(TimeFilter);
        ApplicationFilter.ItemsSource = _filterApplications;
        RefreshApplicationChoices();
        _filterTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _filterTimer.Tick += (_, _) => { if (TimeFilter.SelectedIndex is > 0 and < 5) RefreshFilteredView(); };
        _filterTimer.Start();
    }
    internal List<KnownApplication> GetKnownApplications()
    {
        var apps = _messages.GroupBy(x => x.AppId).ToDictionary(x => x.Key, x => x.First().AppName);
        foreach (var app in _applications) apps[app.Key] = app.Value.Name;
        foreach (int id in _config.ApplicationNotifications.Keys) apps.TryAdd(id, $"App {id}");
        return apps.Select(x => new KnownApplication(x.Key, x.Value)).OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(x => x.Id).ToList();
    }
    private void RefreshApplicationChoices()
    {
        if (_config is null || ApplicationFilter is null) return;
        var choices = new[] { new ApplicationFilterChoice(null, "全部应用") }.Concat(GetKnownApplications().Select(x => new ApplicationFilterChoice(x.Id, x.Name))).ToList();
        if (_filterApplications.SequenceEqual(choices)) return;
        int? selected = (ApplicationFilter.SelectedItem as ApplicationFilterChoice)?.Id;
        _updatingFilterApplications = true;
        try
        {
            _filterApplications.Clear(); foreach (var choice in choices) _filterApplications.Add(choice);
            ApplicationFilter.SelectedItem = _filterApplications.FirstOrDefault(x => x.Id == selected) ?? _filterApplications[0];
        }
        finally { _updatingFilterApplications = false; }
    }
    private bool MatchesActiveFilters(GotifyMessage message)
    {
        int? app = (ApplicationFilter.SelectedItem as ApplicationFilterChoice)?.Id;
        return MatchesSearch(message, SearchBox.Text) && (UnreadOnlyBox.IsChecked != true || message.IsUnread)
            && (app is null || message.AppId == app)
            && MessageTimeFilter.Matches(message.Date, TimeFilter.SelectedIndex, DateTime.Now, _customFrom, _customUntil);
    }
    private bool HasExtraFilters => (ApplicationFilter.SelectedItem as ApplicationFilterChoice)?.Id != null || TimeFilter.SelectedIndex > 0;
    private void RefreshFilteredView()
    {
        if (_view is null) return;
        _view.Refresh();
        if (_selectedMessage != null && !_view.Contains(_selectedMessage)) ClearDetails();
        UpdateCount();
    }
    private void Filter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingFilterApplications) RefreshFilteredView();
    }
    private void TimeFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (CustomTimePanel is null) return;
        bool custom = TimeFilter.SelectedIndex == 5;
        CustomTimePanel.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        TimeFilterHint.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        if (custom) UpdateCustomTimeHint();
        RefreshFilteredView();
    }
    private void CustomTime_Changed(object sender, TextChangedEventArgs e)
    {
        if (TimeFilterHint is null) return;
        _customApplied = false;
        UpdateCustomTimeHint();
    }
    private void UpdateCustomTimeHint()
    {
        TimeFilterHint.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        TimeFilterHint.Text = _customApplied ? "已应用自定义时间（本地时间）；结束日期包含当天，结束分钟包含整分钟。" : "格式：2026-11-06 或 2026-11-06 09:30，起止可留空；点击“应用”后生效。";
    }
    private void ApplyTime_Click(object sender, RoutedEventArgs e)
    {
        if (!MessageTimeFilter.TryRange(TimeFromBox.Text, TimeUntilBox.Text, out var from, out var until, out string error))
        {
            TimeFilterHint.Text = error + " 筛选仍使用上次已应用的范围。";
            TimeFilterHint.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush"); return;
        }
        _customFrom = from; _customUntil = until; _customApplied = true;
        UpdateCustomTimeHint(); RefreshFilteredView();
    }
    internal void ResetFilters(bool includeSearch = true)
    {
        if (includeSearch) SearchBox.Clear();
        UnreadOnlyBox.IsChecked = false; ApplicationFilter.SelectedIndex = 0; TimeFilter.SelectedIndex = 0;
        _customFrom = _customUntil = null; _customApplied = false;
        TimeFromBox.Clear(); TimeUntilBox.Clear(); RefreshFilteredView();
    }
    private void ResetFilters_Click(object sender, RoutedEventArgs e) => ResetFilters();

    internal static bool ShouldNotify(AppConfig config, bool paused, int appId, int priority, DateTime? localNow = null)
    {
        config.ApplicationNotifications.TryGetValue(appId, out var mode);
        bool enabled = mode switch { ApplicationNotificationMode.Enabled => true, ApplicationNotificationMode.Disabled => false, _ => config.NotificationsEnabled };
        return ShouldNotify(enabled, paused, priority, config.NotificationMinPriority)
            && (!config.NotificationOptions.IsQuiet(localNow ?? DateTime.Now) || (priority == 10 && config.NotificationOptions.AllowPriority10DuringQuiet));
    }
}
