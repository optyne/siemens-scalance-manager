using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Text;
using System.Text.Json;

namespace Scalance.Protocols.Wbm;

public sealed class WbmClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public WbmClient(string host, string username, string password, int port = 443)
    {
        _baseUrl = $"https://{host}:{port}";

        var handler = new HttpClientHandler
        {
            CookieContainer = new CookieContainer(),
            UseCookies = true,
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        };
        _http = new HttpClient(handler) { BaseAddress = new Uri(_baseUrl) };
        Username = username;
        Password = password;
    }

    public string Username { get; }
    public string Password { get; }
    public bool IsLoggedIn { get; private set; }

    // TODO: verify actual login endpoint and payload format against a real S615 WBM
    public async Task LoginAsync(CancellationToken ct = default)
    {
        var payload = new { user = Username, password = Password };
        var response = await _http.PostAsJsonAsync("/api/login", payload, ct);
        response.EnsureSuccessStatusCode();
        IsLoggedIn = true;
    }

    public async Task<string> GetAsync(string path, CancellationToken ct = default)
    {
        var response = await _http.GetAsync(path, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    public async Task<string> PostAsync(string path, object jsonContent, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync(path, jsonContent, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    public void Dispose() => _http.Dispose();
}
