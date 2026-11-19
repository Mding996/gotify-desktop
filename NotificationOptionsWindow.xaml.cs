using System.Windows;

namespace GotifyDesktop;

public partial class NotificationOptionsWindow : Window
{
    public NotificationPreferences Options { get; }
    public NotificationOptionsWindow(NotificationPreferences options)
    {
        InitializeComponent(); Options = options.Clone();
        AggregateCheck.IsChecked = options.AggregateBursts; QuietCheck.IsChecked = options.QuietHoursEnabled;
        QuietStartBox.Text = options.QuietStart; QuietEndBox.Text = options.QuietEnd; CriticalCheck.IsChecked = options.AllowPriority10DuringQuiet;
        SourceInitialized += (_, _) => ThemeManager.TitleBar(this);
    }
    internal bool ApplyInputs()
    {
        string start = QuietStartBox.Text.Trim(), end = QuietEndBox.Text.Trim();
        if (QuietCheck.IsChecked == true && (!NotificationPreferences.TryTime(start, out _) || !NotificationPreferences.TryTime(end, out _) || start == end))
        {
            OptionsErrorLabel.Text = "请输入有效且不同的起止时间，例如 22:00 和 08:00。"; OptionsErrorLabel.Visibility = Visibility.Visible; return false;
        }
        Options.AggregateBursts = AggregateCheck.IsChecked == true; Options.QuietHoursEnabled = QuietCheck.IsChecked == true;
        if (NotificationPreferences.TryTime(start, out _)) Options.QuietStart = start;
        if (NotificationPreferences.TryTime(end, out _)) Options.QuietEnd = end;
        Options.AllowPriority10DuringQuiet = CriticalCheck.IsChecked == true;
        OptionsErrorLabel.Visibility = Visibility.Collapsed; return true;
    }
    private void Save_Click(object sender, RoutedEventArgs e) { if (ApplyInputs()) DialogResult = true; }
}
