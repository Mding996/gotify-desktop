using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GotifyDesktop;

internal static class NotificationCopyInvocation
{
    internal const string Prefix = "gotify-desktop://copy-code";
    internal static bool IsCopyRequest(string[] args)
    {
        for (int i = 0; i + 1 < args.Length; i++)
            if (args[i] == "--uri" && args[i + 1].StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
    internal static bool TryKey(string? text, out string key)
    {
        key = "";
        if (text == null || text.Length > 160 || !Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || uri.Scheme != "gotify-desktop" || uri.Host != "copy-code" || uri.UserInfo.Length != 0 || uri.Port != -1
            || uri.AbsolutePath is not ("" or "/") || uri.Fragment.Length != 0 || uri.Query.Length != 37
            || !uri.Query.StartsWith("?key=", StringComparison.Ordinal)
            || !Guid.TryParseExact(uri.Query[5..], "N", out var id) || id == Guid.Empty) return false;
        key = id.ToString("N"); return true;
    }
}

internal sealed class NotificationCopyActions
{
    internal const int Capacity = 512;
    internal static readonly TimeSpan Retention = TimeSpan.FromDays(7);
    internal static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GotifyDesktop", "notification-actions");
    private readonly string _directory;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("GotifyDesktop.NotificationCopy.v1");
    private sealed record Snapshot(string Key, string Code, long MessageId, int AppId, DateTimeOffset CreatedUtc);
    internal NotificationCopyActions(string directory) => _directory = Path.GetFullPath(directory);
    internal static bool IsCode(string? code) => code != null && code.Length is >= 4 and <= 8 && code.All(x => x is >= '0' and <= '9');
    internal string? Create(GotifyMessage message, DateTimeOffset? now = null)
    {
        string code = message.VerificationCode;
        if (!IsCode(code)) return null;
        Directory.CreateDirectory(_directory);
        var snapshot = new Snapshot(Guid.NewGuid().ToString("N"), code, message.Id, message.AppId, now ?? DateTimeOffset.UtcNow);
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(snapshot);
        byte[] encrypted;
        try { encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        string path = Path.Combine(_directory, snapshot.Key + ".action");
        string temporary = path + ".tmp";
        try { File.WriteAllBytes(temporary, encrypted); File.Move(temporary, path); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        Prune(now ?? DateTimeOffset.UtcNow, snapshot.Key);
        return NotificationCopyInvocation.Prefix + "?key=" + snapshot.Key;
    }
    internal string? Read(string key, DateTimeOffset? now = null)
    {
        if (!Guid.TryParseExact(key, "N", out var id) || id == Guid.Empty) return null;
        string path = Path.Combine(_directory, id.ToString("N") + ".action");
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length is <= 0 or > 4096) return null;
            byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
            Snapshot? snapshot;
            try { snapshot = JsonSerializer.Deserialize<Snapshot>(plain); }
            finally { CryptographicOperations.ZeroMemory(plain); }
            var time = now ?? DateTimeOffset.UtcNow;
            return snapshot != null && snapshot.Key == id.ToString("N") && IsCode(snapshot.Code)
                && snapshot.CreatedUtc <= time.AddMinutes(5) && time - snapshot.CreatedUtc <= Retention ? snapshot.Code : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException or ArgumentException) { return null; }
    }
    private void Prune(DateTimeOffset now, string newestKey)
    {
        try
        {
            var files = new DirectoryInfo(_directory).EnumerateFiles("*.action")
                .Where(x => Guid.TryParseExact(Path.GetFileNameWithoutExtension(x.Name), "N", out _))
                .OrderByDescending(x => Path.GetFileNameWithoutExtension(x.Name) == newestKey).ThenByDescending(x => x.LastWriteTimeUtc).ToArray();
            for (int i = 0; i < files.Length; i++)
                if (i >= Capacity || now.UtcDateTime - files[i].LastWriteTimeUtc > Retention)
                    try { files[i].Delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

internal enum NotificationCopyResult { Success, InvalidRequest, MissingRecord, ClipboardUnavailable }
internal static class NotificationCopyRuntime
{
    internal static NotificationCopyResult Execute(string? uri, NotificationCopyActions actions, Action<string> copy)
    {
        if (!NotificationCopyInvocation.TryKey(uri, out var key)) return NotificationCopyResult.InvalidRequest;
        string? code = actions.Read(key);
        if (code == null) return NotificationCopyResult.MissingRecord;
        try { copy(code); return NotificationCopyResult.Success; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or System.Runtime.InteropServices.ExternalException or ThreadStateException or InvalidOperationException or OutOfMemoryException)
        { return NotificationCopyResult.ClipboardUnavailable; }
    }
    internal static int Handle(string[] args)
    {
        var result = args.Length == 2 && args[0] == "--uri"
            ? Execute(args[1], new(NotificationCopyActions.DefaultDirectory), NativeClipboard.WriteCode) : NotificationCopyResult.InvalidRequest;
        if (result != NotificationCopyResult.Success)
        {
            try
            {
                NativeToast.SetProcessIdentity();
                new NativeToast().Show(new GotifyMessage { Title = "验证码复制失败", AppName = "Gotify Desktop",
                    Message = result == NotificationCopyResult.ClipboardUnavailable ? "剪贴板正忙，请稍后再试，或从消息列表复制验证码。" : "这条通知的复制记录已清理或无法读取，请从消息列表重新复制。" });
            }
            catch (Exception) { /* Never open a window or reveal a token/code on the failure path. */ }
        }
        return result == NotificationCopyResult.Success ? 0 : 1;
    }
}
