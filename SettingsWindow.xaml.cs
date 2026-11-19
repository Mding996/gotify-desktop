using System.Windows;
using MessageBox = System.Windows.MessageBox;

namespace GotifyDesktop;

public partial class SettingsWindow : Window
{
    private readonly Action? _testNotification;
    private readonly List<KnownApplication> _applications;
    public AppConfig Config { get; }
    public SettingsWindow(AppConfig config, Action? testNotification = null, IEnumerable<KnownApplication>? applications = null)
    {
        InitializeComponent();
        ThemeManager.TrackComboBox(ThemeBox);
        _testNotification = testNotification;
        _applications = applications?.ToList() ?? new();
        Config = new AppConfig { ServerUrl = config.ServerUrl, ClientToken = config.ClientToken, ApplicationToken = config.ApplicationToken, NotificationsEnabled = config.NotificationsEnabled, NotificationMinPriority = config.NotificationMinPriority, RunAtStartup = config.RunAtStartup, Theme = config.Theme, ApplicationNotifications = new(config.ApplicationNotifications), NotificationOptions = config.NotificationOptions.Clone() };
        ThemeBox.SelectedIndex = config.Theme == "Dark" ? 1 : 0;
        SourceInitialized += (_, _) => ThemeManager.TitleBar(this);
        ServerBox.Text = config.ServerUrl; ClientBox.Password = config.ClientToken; ApplicationBox.Password = config.ApplicationToken;
        NotificationsCheck.IsChecked = config.NotificationsEnabled; StartupCheck.IsChecked = config.RunAtStartup;
    }
    private void TestNotification_Click(object sender, RoutedEventArgs e) => _testNotification?.Invoke();
    private void NotificationOptions_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new NotificationOptionsWindow(Config.NotificationOptions) { Owner = this };
        if (dialog.ShowDialog() == true) Config.NotificationOptions = dialog.Options;
    }
    private void ApplicationNotifications_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ApplicationNotificationsWindow(_applications, Config.ApplicationNotifications, NotificationsCheck.IsChecked == true) { Owner = this };
        if (dialog.ShowDialog() == true) Config.ApplicationNotifications = dialog.Rules;
    }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!Uri.TryCreate(ServerBox.Text.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
        { MessageBox.Show(this, "请输入有效的 http 或 https 服务器地址。", "地址无效", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.UserInfo))
        { MessageBox.Show(this, "服务器地址不能包含查询参数、片段或用户名密码。", "地址无效", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        Config.ServerUrl = uri.ToString().TrimEnd('/'); Config.ClientToken = ClientBox.Password.Trim(); Config.ApplicationToken = ApplicationBox.Password.Trim();
        Config.NotificationsEnabled = NotificationsCheck.IsChecked == true; Config.RunAtStartup = StartupCheck.IsChecked == true;
        Config.Theme = ThemeBox.SelectedIndex == 1 ? "Dark" : "Light";
        DialogResult = true;
    }
}
