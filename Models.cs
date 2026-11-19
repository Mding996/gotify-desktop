using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GotifyDesktop;

public sealed class GotifyMessage : INotifyPropertyChanged
{
    public long Id { get; set; }
    public int AppId { get; set; }
    private string _appName = "Gotify";
    public string AppName { get => _appName; set { _appName = value; PropertyChanged?.Invoke(this, new(nameof(AppName))); } }
    private string _appIconPath = "pack://application:,,,/Assets/logo.png";
    public string AppIconPath { get => _appIconPath; set { _appIconPath = value; PropertyChanged?.Invoke(this, new(nameof(AppIconPath))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    private bool _isUnread;
    public bool IsUnread { get => _isUnread; set { if (_isUnread == value) return; _isUnread = value; PropertyChanged?.Invoke(this, new(nameof(IsUnread))); } }
    [JsonIgnore] public bool IsImportant => Priority >= 8 && Priority <= 10;
    private string? _codeTitle, _codeBody, _code;
    [JsonIgnore] public string VerificationCode
    {
        get
        {
            if (_code == null || !ReferenceEquals(_codeTitle, Title) || !ReferenceEquals(_codeBody, Message))
            { _code = MessageActions.VerificationCode(this); _codeTitle = Title; _codeBody = Message; }
            return _code;
        }
    }
    [JsonIgnore] public string Preview => Message.Length <= 320 ? Message : Message[..(char.IsHighSurrogate(Message[319]) ? 319 : 320)] + "…";
    [JsonIgnore] public bool HasVerificationCode => VerificationCode.Length > 0;
    public string Message { get; set; } = "";
    public string Title { get; set; } = "";
    public int Priority { get; set; }
    public DateTime Date { get; set; } = DateTime.Now;
    public Dictionary<string, JsonElement>? Extras { get; set; }
}

public sealed class WireMessage
{
    public long id { get; set; }
    public int appid { get; set; }
    public string? message { get; set; }
    public string? title { get; set; }
    public int priority { get; set; }
    public DateTime date { get; set; }
    public Dictionary<string, JsonElement>? extras { get; set; }
}

public sealed class WireMessagePage
{
    public List<WireMessage>? messages { get; set; }
}

public sealed class WireApplication
{
    public int id { get; set; }
    public string? name { get; set; }
    public string? image { get; set; }
    public int defaultPriority { get; set; }
}

public sealed class AppConfig
{
    public string ServerUrl { get; set; } = "";
    public string ProtectedClientToken { get; set; } = "";
    public string ProtectedApplicationToken { get; set; } = "";
    public bool NotificationsEnabled { get; set; } = true;
    public int NotificationMinPriority { get; set; } = 8;
    public bool RunAtStartup { get; set; }
    public string Theme { get; set; } = "Light";
    public Dictionary<int, ApplicationNotificationMode> ApplicationNotifications { get; set; } = new();
    public NotificationPreferences NotificationOptions { get; set; } = new();
    [JsonIgnore] public string ClientToken { get; set; } = "";
    [JsonIgnore] public string ApplicationToken { get; set; } = "";
    public static AppConfig Load(string path)
    {
        if (!File.Exists(path)) return new();
        try
        {
            var c = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path)) ?? new();
            c.ServerUrl ??= "";
            c.ApplicationNotifications ??= new();
            c.NotificationOptions ??= new();
            c.ClientToken = Unprotect(c.ProtectedClientToken);
            c.ApplicationToken = Unprotect(c.ProtectedApplicationToken);
            return c;
        }
        catch { return new(); }
    }
    public void Save(string path)
    {
        var stored = new AppConfig
        {
            ServerUrl = ServerUrl,
            ProtectedClientToken = Protect(ClientToken),
            ProtectedApplicationToken = Protect(ApplicationToken),
            NotificationsEnabled = NotificationsEnabled,
            NotificationMinPriority = NotificationMinPriority,
            RunAtStartup = RunAtStartup
            , Theme = Theme,
            ApplicationNotifications = new(ApplicationNotifications),
            NotificationOptions = NotificationOptions.Clone()
        };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(stored, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(path + ".tmp", path, true);
    }
    internal bool ResetApplicationRulesForConnection(string previousServer, string previousToken)
    {
        bool changed = previousServer.TrimEnd('/') != ServerUrl.TrimEnd('/') || previousToken != ClientToken;
        if (changed) ApplicationNotifications.Clear();
        return changed;
    }
    private static string Protect(string value) => string.IsNullOrEmpty(value) ? "" : Convert.ToBase64String(ProtectedData.Protect(System.Text.Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
    private static string Unprotect(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        try { return System.Text.Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser)); }
        catch { return ""; }
    }
}

