using System.ComponentModel;
using System.Windows;

namespace GotifyDesktop;

public sealed class ApplicationNotificationRow : INotifyPropertyChanged
{
    public KnownApplication Application { get; }
    private ApplicationNotificationMode _mode;
    public ApplicationNotificationMode Mode { get => _mode; set { if (_mode == value) return; _mode = value; PropertyChanged?.Invoke(this, new(nameof(Mode))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    public ApplicationNotificationRow(KnownApplication application, ApplicationNotificationMode mode) { Application = application; _mode = mode; }
}

public partial class ApplicationNotificationsWindow : Window
{
    internal List<ApplicationNotificationRow> Rows { get; }
    public Dictionary<int, ApplicationNotificationMode> Rules { get; private set; } = new();
    public ApplicationNotificationsWindow(IEnumerable<KnownApplication> applications, IReadOnlyDictionary<int, ApplicationNotificationMode> rules, bool defaultEnabled)
    {
        InitializeComponent();
        var known = applications.GroupBy(x => x.Id).ToDictionary(x => x.Key, x => x.First());
        foreach (int id in rules.Keys) known.TryAdd(id, new(id, $"App {id}"));
        Rows = known.Values.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(x => x.Id).Select(app => new ApplicationNotificationRow(app, rules.GetValueOrDefault(app.Id))).ToList();
        ApplicationRulesList.ItemsSource = Rows;
        NoApplicationsLabel.Visibility = Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DefaultNoticeLabel.Text = $"默认提醒：{(defaultEnabled ? "开启" : "关闭")}。未单独配置的应用使用此开关。";
        SourceInitialized += (_, _) => ThemeManager.TitleBar(this);
    }
    internal Dictionary<int, ApplicationNotificationMode> BuildRules() => Rows.Where(x => x.Mode is ApplicationNotificationMode.Enabled or ApplicationNotificationMode.Disabled).ToDictionary(x => x.Application.Id, x => x.Mode);
    private void RuleMode_Loaded(object sender, RoutedEventArgs e) => ThemeManager.TrackComboBox((System.Windows.Controls.ComboBox)sender);
    private void Reset_Click(object sender, RoutedEventArgs e) { foreach (var row in Rows) row.Mode = ApplicationNotificationMode.FollowDefault; }
    private void Save_Click(object sender, RoutedEventArgs e) { Rules = BuildRules(); DialogResult = true; }
}
