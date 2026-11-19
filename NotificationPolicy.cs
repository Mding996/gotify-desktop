using System.Globalization;
using System.Windows;
using System.Windows.Threading;

namespace GotifyDesktop;

public sealed class NotificationPreferences
{
    public bool AggregateBursts { get; set; } = true;
    public bool QuietHoursEnabled { get; set; }
    public string QuietStart { get; set; } = "22:00";
    public string QuietEnd { get; set; } = "08:00";
    public bool AllowPriority10DuringQuiet { get; set; }
    public NotificationPreferences Clone() => (NotificationPreferences)MemberwiseClone();
    internal static bool TryTime(string value, out TimeSpan time) => TimeSpan.TryParseExact(value, @"hh\:mm", CultureInfo.InvariantCulture, out time) && time < TimeSpan.FromDays(1);
    internal bool IsQuiet(DateTime localNow)
    {
        if (!QuietHoursEnabled || !TryTime(QuietStart, out var from) || !TryTime(QuietEnd, out var until) || from == until) return false;
        var time = localNow.TimeOfDay;
        return from < until ? time >= from && time < until : time >= from || time < until;
    }
}

internal sealed class NotificationBurstBuffer
{
    internal static readonly TimeSpan Window = TimeSpan.FromSeconds(10);
    private sealed class Group(GotifyMessage first, DateTimeOffset now)
    {
        internal readonly DateTimeOffset Started = now;
        internal int Count = 1;
        internal GotifyMessage Latest = first;
        internal int HighestPriority = first.Priority;
    }
    private readonly Dictionary<int, Group> _groups = new();
    internal void DiscardMessages(HashSet<long> deleted)
    {
        foreach (var key in _groups.Where(x => deleted.Contains(x.Value.Latest.Id)).Select(x => x.Key).ToArray()) _groups.Remove(key);
    }
    internal List<GotifyMessage> Accept(GotifyMessage message, DateTimeOffset now)
    {
        var output = Flush(now);
        if (_groups.TryGetValue(message.AppId, out var group)) { group.Count++; group.Latest = message; group.HighestPriority = Math.Max(group.HighestPriority, message.Priority); }
        else { _groups[message.AppId] = new(message, now); output.Add(message); }
        return output;
    }
    internal List<GotifyMessage> Flush(DateTimeOffset now)
    {
        var output = new List<GotifyMessage>();
        foreach (var pair in _groups.Where(x => now - x.Value.Started >= Window).ToList())
        {
            _groups.Remove(pair.Key);
            var group = pair.Value;
            if (group.Count <= 1) continue;
            var latest = group.Latest;
            output.Add(new GotifyMessage { Id = latest.Id, AppId = latest.AppId, AppName = latest.AppName, AppIconPath = latest.AppIconPath, Date = latest.Date, Priority = group.HighestPriority, Extras = latest.Extras, Title = $"{latest.AppName}：{group.Count} 条新消息", Message = $"10 秒内共收到 {group.Count} 条消息。\n最新：{latest.Title}\n{latest.Message}" });
        }
        return output;
    }
    internal void Clear() => _groups.Clear();
}

public partial class MainWindow
{
    private readonly NotificationBurstBuffer _notificationBuffer = new();
    private DispatcherTimer? _notificationTimer;
    internal Action<GotifyMessage>? NotificationSink { get; set; }
    private void InitializeNotificationPolicy()
    {
        _notificationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _notificationTimer.Tick += (_, _) => FlushNotifications(DateTimeOffset.UtcNow);
        _notificationTimer.Start();
    }
    private void QueueNotification(GotifyMessage message)
    {
        if (!ShouldNotify(_config, _notificationsPaused, message.AppId, message.Priority)) return;
        if (message.HasVerificationCode || !_config.NotificationOptions.AggregateBursts) { ShowNotification(message); return; }
        foreach (var output in _notificationBuffer.Accept(message, DateTimeOffset.UtcNow)) DispatchNotification(output);
    }
    internal void FlushNotifications(DateTimeOffset now)
    {
        foreach (var output in _notificationBuffer.Flush(now)) DispatchNotification(output);
    }
    private void DispatchNotification(GotifyMessage message)
    {
        if (!_exiting && ShouldNotify(_config, _notificationsPaused, message.AppId, message.Priority)) ShowNotification(message);
    }
    private void ShowNotification(GotifyMessage message)
    {
        _lastNotifiedMessage = message;
        if (NotificationSink != null) { NotificationSink(message); return; }
        try { if (_toast != null) { _toast.Show(message); return; } }
        catch { SetNotice("通知中心发送失败，已改用托盘弹窗。", NoticeTone.Warning); }
        _tray.ShowBalloonTip(5000, string.IsNullOrEmpty(message.Title) ? "Gotify" : message.Title, message.Message);
    }
}

