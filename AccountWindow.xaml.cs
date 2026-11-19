using System.Windows;

namespace GotifyDesktop;

public partial class AccountWindow : Window
{
    internal AccountCredentials? Credentials { get; set; }
    internal string Server { get; private set; } = "";
    internal AccountWindow(string server, string user = "", bool login = false)
    {
        InitializeComponent(); ServerBox.Text = server; ServerBox.IsReadOnly = !login; UserBox.Text = user;
        Title = login ? "账号登录" : "验证当前账号"; ConfirmButton.Content = login ? "登录" : "验证并继续";
        Explanation.Text = login ? "登录后为这台电脑创建一个接收客户端，并加密保存 Client Token。也可以继续在设置中手动填写 Token。" : "此操作需要当前 Gotify 账号的密码。验证只用于本次操作，不会保存密码。";
        SourceInitialized += (_, _) => ThemeManager.TitleBar(this);
        Loaded += (_, _) => { if (user.Length == 0) UserBox.Focus(); else PasswordBox.Focus(); };
        Closed += (_, _) => PasswordBox.Clear();
    }
    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Server = ManagementApi.NormalizeServer(ServerBox.Text);
            if (string.IsNullOrWhiteSpace(UserBox.Text) || PasswordBox.Password.Length == 0) throw new ArgumentException("请填写用户名和密码。");
            if (UserBox.Text.Contains(':')) throw new ArgumentException("用户名不能包含冒号。");
            Credentials = new(UserBox.Text.Trim(), PasswordBox.Password); DialogResult = true;
        }
        catch (ArgumentException ex) { ValidationLabel.Text = ex.Message; }
    }
}
