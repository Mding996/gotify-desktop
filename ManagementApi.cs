using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace GotifyDesktop;

internal sealed record AccountCredentials(string UserName, string Password);
internal sealed class ManagedApplication
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public int DefaultPriority { get; set; }
    public bool Internal { get; set; }
    public string Image { get; set; } = "";
    public string? Token { get; set; }
}
internal sealed class ManagedClient
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Token { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastUsed { get; set; }
    public DateTimeOffset? ElevatedUntil { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public long ExpiresAfterInactivitySeconds { get; set; }
    public string CreatedDisplay => CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public string UsedDisplay => LastUsed?.ToLocalTime().ToString("MM-dd HH:mm") ?? "尚未使用";
    public string ExpiryDisplay => ExpiresAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "永不过期";
    public string ElevationDisplay => ElevatedUntil > DateTimeOffset.Now ? ElevatedUntil.Value.ToLocalTime().ToString("HH:mm:ss") : "普通权限";
}
internal sealed class ManagedUser
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int ClientId { get; set; }
    public DateTimeOffset? ElevatedUntil { get; set; }
}
internal sealed class ManagementApiException : Exception
{
    internal HttpStatusCode Status { get; }
    internal ManagementApiException(HttpStatusCode status) : base(status switch
    {
        HttpStatusCode.Unauthorized => "身份验证失败，请检查账号密码或 Client Token。",
        HttpStatusCode.Forbidden => "权限不足。敏感操作需要验证当前账号密码。",
        HttpStatusCode.NotFound => "项目不存在，或当前服务器不支持此操作，请刷新列表。",
        HttpStatusCode.BadRequest => "服务器未接受这些设置，请检查名称、优先级或有效期。",
        _ => $"服务器操作失败（HTTP {(int)status}）。"
    }) { Status = status; }
}

// A request carries either its current client token or short-lived Basic credentials.
// Redirects are disabled so credentials never follow a redirect to another origin.
internal sealed class ManagementApi : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _server, _token;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal ManagementApi(string server, string token, HttpMessageHandler? handler = null)
    {
        _server = NormalizeServer(server); _token = token;
        _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(25) };
    }
    internal static string NormalizeServer(string server)
    {
        if (!Uri.TryCreate(server.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("请输入有效的 HTTP 或 HTTPS 服务器地址，不能包含用户名密码或查询参数。");
        return uri.AbsoluteUri.TrimEnd('/');
    }
    private async Task<T> Request<T>(HttpMethod method, string path, object? body = null, AccountCredentials? credentials = null, CancellationToken ct = default, HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(method, _server + path);
        if (credentials != null) request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials.UserName + ":" + credentials.Password)));
        else if (!string.IsNullOrWhiteSpace(_token)) request.Headers.Add("X-Gotify-Key", _token);
        request.Content = content ?? (body == null ? null : JsonContent.Create(body, options: Json));
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new ManagementApiException(response.StatusCode);
        if (typeof(T) == typeof(bool)) return (T)(object)true;
        return await response.Content.ReadFromJsonAsync<T>(Json, ct).ConfigureAwait(false) ?? throw new InvalidDataException("服务器返回的数据为空。");
    }
    internal Task<ManagedUser> User(AccountCredentials? credentials = null) => Request<ManagedUser>(HttpMethod.Get, "/current/user", credentials: credentials);
    internal Task<List<ManagedApplication>> Applications() => Request<List<ManagedApplication>>(HttpMethod.Get, "/application");
    internal Task<List<ManagedClient>> Clients() => Request<List<ManagedClient>>(HttpMethod.Get, "/client");
    internal Task<ManagedApplication> SaveApplication(int? id, string name, string description, int priority) => Request<ManagedApplication>(id.HasValue ? HttpMethod.Put : HttpMethod.Post, id.HasValue ? $"/application/{id}" : "/application", new { name, description, defaultPriority = priority });
    internal Task<ManagedClient> SaveClient(int? id, string name, long seconds, AccountCredentials? credentials = null) => Request<ManagedClient>(id.HasValue ? HttpMethod.Put : HttpMethod.Post, id.HasValue ? $"/client/{id}" : "/client", new { name, expiresAfterInactivitySeconds = seconds }, credentials);
    internal async Task<string> RotateApplication(int id, AccountCredentials credentials)
    {
        var result = await Request<JsonElement>(HttpMethod.Put, $"/application/{id}/security", new { regenerateToken = true }, credentials);
        return result.GetProperty("regenerateToken").GetProperty("token").GetString() ?? throw new InvalidDataException("服务器未返回新 Token。");
    }
    internal Task<bool> DeleteApplication(int id, AccountCredentials credentials) => Request<bool>(HttpMethod.Delete, $"/application/{id}", credentials: credentials);
    internal Task<bool> DeleteClient(int id, AccountCredentials credentials) => Request<bool>(HttpMethod.Delete, $"/client/{id}", credentials: credentials);
    internal Task<bool> ElevateClient(int id, AccountCredentials credentials) => Request<bool>(HttpMethod.Post, $"/client/{id}/elevate", new { durationSeconds = 900 }, credentials);
    internal Task<bool> DeleteMessages(long? messageId = null, int? applicationId = null) => Request<bool>(HttpMethod.Delete, messageId.HasValue ? $"/message/{messageId}" : applicationId.HasValue ? $"/application/{applicationId}/message" : "/message");
    internal async Task<ManagedApplication> UploadIcon(int id, string path)
    {
        if (new FileInfo(path).Length > 5 * 1024 * 1024) throw new ArgumentException("请选择不超过 5 MB 的图标。");
        using var form = new MultipartFormDataContent();
        var bytes = new ByteArrayContent(await File.ReadAllBytesAsync(path));
        bytes.Headers.ContentType = new MediaTypeHeaderValue(Path.GetExtension(path).ToLowerInvariant() switch { ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", _ => "image/png" });
        form.Add(bytes, "file", Path.GetFileName(path));
        return await Request<ManagedApplication>(HttpMethod.Post, $"/application/{id}/image", content: form);
    }
    internal Task<bool> RemoveIcon(int id) => Request<bool>(HttpMethod.Delete, $"/application/{id}/image");
    public void Dispose() => _http.Dispose();
}
