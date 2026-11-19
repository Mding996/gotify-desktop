using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GotifyDesktop;

internal sealed class VaultToken
{
    public Guid Key { get; set; } = Guid.NewGuid();
    public string Server { get; set; } = "";
    public int? AccountId { get; set; }
    public string Kind { get; set; } = "Client";
    public int? ObjectId { get; set; }
    public string Name { get; set; } = "";
    public DateTimeOffset SavedAt { get; set; } = DateTimeOffset.Now;
    public string ProtectedValue { get; set; } = "";
    public bool Revoked { get; set; }
    [JsonIgnore] public string State => Revoked ? "已撤销 / 已轮换" : "本机已保存";
    [JsonIgnore] public string SavedDisplay => SavedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    [JsonIgnore] public string KindDisplay => Kind == "Client" ? "接收客户端" : "发送应用";
    internal string Reveal() => Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(ProtectedValue), null, DataProtectionScope.CurrentUser));
}

internal sealed class TokenVault
{
    private readonly string _path;
    private List<VaultToken> _tokens;
    internal IReadOnlyList<VaultToken> Tokens => _tokens;
    internal TokenVault(string path)
    {
        _path = path;
        // Do not silently replace an unreadable vault with an empty file.
        _tokens = File.Exists(path) ? JsonSerializer.Deserialize<List<VaultToken>>(File.ReadAllText(path)) ?? throw new InvalidDataException("Token 保险箱格式不正确。") : new();
    }
    internal VaultToken Store(string server, int? account, string kind, int? id, string name, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Token 不能为空。");
        server = ManagementApi.NormalizeServer(server);
        var duplicate = _tokens.FirstOrDefault(x => x.Server == server && x.Kind == kind && x.Reveal() == value);
        if (duplicate != null)
        {
            if ((!account.HasValue || duplicate.AccountId == account) && (!id.HasValue || duplicate.ObjectId == id)) return duplicate;
            var copy = JsonSerializer.Deserialize<List<VaultToken>>(JsonSerializer.Serialize(_tokens))!;
            var enriched = copy.Single(x => x.Key == duplicate.Key);
            enriched.AccountId = account ?? enriched.AccountId; enriched.ObjectId = id ?? enriched.ObjectId; enriched.Name = name;
            Commit(copy); return enriched;
        }
        var token = new VaultToken { Server = server, AccountId = account, Kind = kind, ObjectId = id, Name = name, ProtectedValue = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser)) };
        Commit(_tokens.Append(token).ToList());
        return token;
    }
    internal void Revoke(string server, int account, string kind, int id, string? except = null)
    {
        // Work on a copy so a failed disk write also preserves the in-memory state.
        var copy = JsonSerializer.Deserialize<List<VaultToken>>(JsonSerializer.Serialize(_tokens))!;
        foreach (var token in copy.Where(x => x.Server == server && x.AccountId == account && x.Kind == kind && x.ObjectId == id))
            if (except == null || token.Reveal() != except) token.Revoked = true;
        Commit(copy);
    }
    internal void Remove(Guid key) => Commit(_tokens.Where(x => x.Key != key).ToList());
    private void Commit(List<VaultToken> tokens)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(tokens, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(_path + ".tmp", _path, true); _tokens = tokens;
    }
}
