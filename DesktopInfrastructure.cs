using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace GotifyDesktop;

internal sealed class InstanceChannel : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    internal static string UserKey => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName)))[..20];
    internal static string DefaultName => "GotifyDesktop." + UserKey;
    internal InstanceChannel(string name, Action<long?> activate)
    {
        _ = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    using var pipe = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await pipe.WaitForConnectionAsync(_stop.Token);
                    using var reader = new StreamReader(pipe);
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(3));
                    var text = await reader.ReadLineAsync(deadline.Token);
                    if (text?.Length <= 100 && text.StartsWith("open:")) activate(long.TryParse(text[5..], out var id) && id > 0 ? id : null);
                }
                catch (OperationCanceledException) { }
                catch (IOException) { }
            }
        });
    }
    internal static async Task SendAsync(string name, long? id)
    {
        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(3000).ConfigureAwait(false);
        using var writer = new StreamWriter(pipe) { AutoFlush = true };
        await writer.WriteLineAsync("open:" + id).ConfigureAwait(false);
    }
    internal static long? ParseActivation(string[] args)
    {
        var index = Array.IndexOf(args, "--uri");
        if (index < 0 || index + 1 >= args.Length || !Uri.TryCreate(args[index + 1], UriKind.Absolute, out var uri) || uri.Scheme != "gotify-desktop" || uri.Host != "message") return null;
        foreach (var part in uri.Query.TrimStart('?').Split('&')) if (part.StartsWith("id=") && long.TryParse(part[3..], out var id) && id > 0) return id;
        return null;
    }
    public void Dispose() { _stop.Cancel(); }
}

internal static class UserError
{
    internal static string Describe(Exception ex, string tokenRole)
    {
        if (ex is HttpRequestException http)
        {
            if (http.StatusCode == HttpStatusCode.Unauthorized) return tokenRole + " 无效或已被撤销（401），请重新填写。";
            if (http.StatusCode == HttpStatusCode.Forbidden) return tokenRole + " 权限不足（403）。";
            if (http.StatusCode == HttpStatusCode.NotFound) return "服务器接口不存在（404），请检查服务器地址及路径。";
            if (http.StatusCode is not null) return $"服务器返回 HTTP {(int)http.StatusCode}，请稍后重试。";
            if (http.InnerException is System.Security.Authentication.AuthenticationException) return "HTTPS 证书校验失败，请检查服务器证书。";
            if (http.InnerException is SocketException socket && socket.SocketErrorCode == SocketError.HostNotFound) return "无法解析服务器域名，请检查地址和 DNS。";
        }
        if (ex is OperationCanceledException || ex is TimeoutException) return "请求超时，请检查网络或服务器负载。";
        if (ex is JsonException || ex is InvalidDataException) return "服务器响应格式不正确，请检查服务器地址。";
        return "无法连接服务器，请检查网络、地址和服务器运行状态。";
    }
}
