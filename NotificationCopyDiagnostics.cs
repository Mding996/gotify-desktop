using System.Collections;
using System.IO;
using System.Reflection;
using System.Text;
using System.Xml.Linq;

namespace GotifyDesktop;

internal static partial class Diagnostics
{
    private static void CheckNotificationCopy(MainWindow window, List<string> checks, string profile)
    {
        void Check(bool ok, string label) { if (!ok) throw new InvalidOperationException(label); checks.Add(label); }
        string directory = Path.Combine(profile, "notification-copy-tests-" + Guid.NewGuid().ToString("N"));
        var store = new NotificationCopyActions(directory);
        var first = new GotifyMessage { Id = 811, AppId = 7, Title = "106900000000001", Message = "【示例平台】验证码：001234，5分钟内有效。", AppName = "短信转发" };
        string uriA = store.Create(first)!;
        Check(NotificationCopyInvocation.TryKey(uriA, out var keyA) && !uriA.Contains("001234"), "Copy activation contains only an opaque snapshot key, never the code or a server token");
        var bytes = File.ReadAllBytes(Path.Combine(directory, keyA + ".action"));
        Check(!Encoding.UTF8.GetString(bytes).Contains("001234") && !Encoding.Unicode.GetString(bytes).Contains("001234"), "Notification copy snapshots protect codes with Windows DPAPI");
        var reloaded = new NotificationCopyActions(directory);
        Check(reloaded.Read(keyA) == "001234", "Notification copy records survive reload and preserve leading zeros");
        first.Message = "验证码：777777";
        Check(reloaded.Read(keyA) == "001234", "An existing notification keeps its immutable code even if another message or model changes");
        string uriB = store.Create(new() { Id = 812, AppId = 7, Message = "验证码：87654321" })!;
        NotificationCopyInvocation.TryKey(uriB, out var keyB);
        Check(keyA != keyB && store.Read(keyB) == "87654321" && store.Read(keyA) == "001234", "Rapid notifications have distinct actions bound to their own exact codes");
        string copied = "untouched";
        Check(NotificationCopyRuntime.Execute(uriA, reloaded, value => copied = value) == NotificationCopyResult.Success && copied == "001234", "The copy action writes only the code belonging to the clicked notification");
        Check(NotificationCopyRuntime.Execute(uriB, reloaded, _ => throw new System.ComponentModel.Win32Exception(5)) == NotificationCopyResult.ClipboardUnavailable, "Clipboard failure is reported without claiming success");
        copied = "untouched";
        Check(NotificationCopyRuntime.Execute(NotificationCopyInvocation.Prefix + "?key=" + Guid.NewGuid().ToString("N"), reloaded, value => copied = value) == NotificationCopyResult.MissingRecord && copied == "untouched", "A cleared notification record never copies a different or latest code");
        foreach (string invalid in new[] { "https://copy-code?key=" + keyA, NotificationCopyInvocation.Prefix + "?key=../config.json", NotificationCopyInvocation.Prefix + "?key=" + keyA + "&code=123456", "gotify-desktop://copy-code.evil?key=" + keyA, "gotify-desktop://x@copy-code?key=" + keyA, NotificationCopyInvocation.Prefix + "/other?key=" + keyA, NotificationCopyInvocation.Prefix + "?key=" + keyA + "#extra", NotificationCopyInvocation.Prefix + "?key=" + new string('0', 32) })
            Check(!NotificationCopyInvocation.TryKey(invalid, out _), "Copy activation rejects a malformed, foreign, path-traversing or extra-argument URI");
        Check(!App.ShouldOpenWindow(new[] { "--uri", uriA }) && !App.ShouldOpenWindow(new[] { "--uri", NotificationCopyInvocation.Prefix + "?key=bad" }), "Both valid and invalid copy invocations stay on the no-window path");
        Check(App.ShouldOpenWindow(new[] { "--uri", "gotify-desktop://message?id=811" }) && InstanceChannel.ParseActivation(new[] { "--uri", "gotify-desktop://message?id=811" }) == 811, "Notification body activation still opens the corresponding message normally");

        var copyMessage = new GotifyMessage { Id = 811, Message = "验证码：001234 https://example.invalid/login", AppName = "短信转发" };
        var xml = XElement.Parse(NativeToast.Payload(copyMessage, uriA));
        var action = xml.Descendants("action").Single();
        Check(action.Attribute("content")?.Value == "复制验证码" && action.Attribute("activationType")?.Value == "protocol" && action.Attribute("arguments")?.Value == uriA, "A code notification has exactly one copy button with the immutable action URI");
        Check(xml.Attribute("launch")?.Value == "gotify-desktop://message?id=811" && !xml.Descendants("action").Any(x => x.Attribute("content")?.Value == "查看消息"), "Single-button design preserves body-to-detail activation without adding a second view button");
        NativeToast.ValidatePlatform(xml.ToString()); Check(true, "Windows Runtime accepts the single-copy-button toast payload");
        foreach (string body in new[] { "验证码：123456，动态码：654321", "电话13800138000，订单号123456", "普通消息" })
        {
            var message = new GotifyMessage { Message = body };
            Check(store.Create(message) == null && !XElement.Parse(NativeToast.Payload(message, uriA)).Descendants("action").Any(), "Ambiguous codes and unrelated numbers do not expose a copy action");
        }
        var expired = store.Create(new() { Message = "验证码：111111" }, DateTimeOffset.UtcNow.Subtract(NotificationCopyActions.Retention).AddMinutes(-1))!;
        NotificationCopyInvocation.TryKey(expired, out var expiredKey);
        Check(store.Read(expiredKey) == null, "Expired local action snapshots fail without treating another notification as a replacement");
        File.WriteAllBytes(Path.Combine(directory, keyB + ".action"), new byte[] { 1, 2, 3, 4 });
        Check(store.Read(keyB) == null, "A corrupted encrypted snapshot is rejected safely");
        string substitution = Guid.NewGuid().ToString("N"); File.Copy(Path.Combine(directory, keyA + ".action"), Path.Combine(directory, substitution + ".action"));
        Check(store.Read(substitution) == null, "Encrypted snapshot identity is checked against its file key");
        var filesBefore = Directory.GetFiles(directory).Length;
        var nonCode = store.Create(new() { Message = "没有验证码" });
        Check(nonCode == null && Directory.GetFiles(directory).Length == filesBefore, "Ordinary notifications create no clipboard cache files");
        string capacityDirectory = Path.Combine(profile, "copy-capacity-" + Guid.NewGuid().ToString("N"));
        var capacity = new NotificationCopyActions(capacityDirectory);
        Directory.CreateDirectory(capacityDirectory);
        for (int i = 0; i < NotificationCopyActions.Capacity + 4; i++) File.WriteAllBytes(Path.Combine(capacityDirectory, Guid.NewGuid().ToString("N") + ".action"), new byte[] { 0 });
        File.WriteAllText(Path.Combine(capacityDirectory, "unrelated.txt"), "keep");
        string newest = capacity.Create(new() { Message = "验证码：123456" })!; NotificationCopyInvocation.TryKey(newest, out var newestKey);
        Check(Directory.GetFiles(capacityDirectory, "*.action").Length == NotificationCopyActions.Capacity && capacity.Read(newestKey) == "123456" && File.Exists(Path.Combine(capacityDirectory, "unrelated.txt")), "Action-cache pruning is bounded, keeps the newly created action and preserves unrelated files");

        var config = window.ManagementConfig; config.NotificationsEnabled = true; config.NotificationOptions = new() { AggregateBursts = true }; config.ApplicationNotifications.Clear();
        window.ResetFilters(); window.ClearLocalHistory(); window.Hide();
        var delivered = new List<GotifyMessage>(); window.NotificationSink = delivered.Add;
        var add = typeof(MainWindow).GetMethod("AddMessage", BindingFlags.Instance | BindingFlags.NonPublic)!;
        for (int i = 0; i < 3; i++) add.Invoke(window, new object[] { new WireMessage { id = 8000100 + i, appid = 7, title = "SMS", message = "验证码：" + (123450 + i), priority = 9 } });
        Check(delivered.Count == 3 && delivered.Select(x => x.VerificationCode).SequenceEqual(new[] { "123450", "123451", "123452" }), "Rapid verification messages bypass burst aggregation and each retains its own copyable code");
        window.FlushNotifications(DateTimeOffset.UtcNow.AddSeconds(11));
        Check(delivered.Count == 3, "Verification notifications do not later produce a duplicate burst summary");
        var messages = (IList)typeof(MainWindow).GetField("_messages", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var model = messages.Cast<GotifyMessage>().First(); bool unread = model.IsUnread;
        string modelUri = store.Create(model)!; NotificationCopyRuntime.Execute(modelUri, store, _ => { });
        Check(model.IsUnread == unread && window.ReaderSuspended, "Copying from a hidden-window notification does not mark it read or open the message center");
        config.ApplicationNotifications[7] = ApplicationNotificationMode.Disabled;
        add.Invoke(window, new object[] { new WireMessage { id = 8000200, appid = 7, message = "验证码：666666", priority = 9 } });
        config.ApplicationNotifications.Clear();
        add.Invoke(window, new object[] { new WireMessage { id = 8000201, appid = 7, message = "验证码：555555", priority = 5 } });
        Check(delivered.Count == 3, "Code recognition still respects per-app notification settings and the priority threshold");
        window.NotificationSink = null; config.NotificationsEnabled = false; config.NotificationOptions = new(); window.OpenFromActivation(null);
    }
}
