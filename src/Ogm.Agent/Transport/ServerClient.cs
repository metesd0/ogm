using System.Net.Http.Headers;
using System.Text;
using Ogm.Agent.Configuration;
using Ogm.Shared;

namespace Ogm.Agent.Transport;

/// <summary>
/// Sunucu uygulamasiyla HTTP uzerinden konusan istemci. Kayit, heartbeat ve
/// is yukleme islemlerini yonetir. Tum metodlar ag hatasinda istisna firlatmak
/// yerine null/false dondurur; boylece ajan cevrimdisi (offline) calismaya
/// devam eder ve isleri kuyrukta tutar.
/// </summary>
public sealed class ServerClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly ILogger<ServerClient> _logger;

    public ServerClient(AgentOptions options, ILogger<ServerClient> logger)
    {
        _logger = logger;

        _http = new HttpClient
        {
            BaseAddress = new Uri(options.ServerUrl.TrimEnd('/') + "/", UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(120)
        };

        if (!string.IsNullOrWhiteSpace(options.ApiKey))
            _http.DefaultRequestHeaders.TryAddWithoutValidation(OgmProtocol.ApiKeyHeader, options.ApiKey);

        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"{OgmProtocol.AppName}-Agent/{1}");
    }

    /// <summary>Ajani sunucuya kaydeder.</summary>
    public async Task<RegisterResponse?> RegisterAsync(AgentIdentity identity, CancellationToken ct)
    {
        var request = new RegisterRequest(
            identity.AgentId,
            identity.MachineName,
            identity.UserName,
            identity.OsVersion,
            identity.AgentVersion,
            OgmProtocol.Version);

        try
        {
            using var content = new StringContent(OgmJson.Serialize(request), Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(OgmProtocol.Routes.Register, content, ct).ConfigureAwait(false);

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Kayit basarisiz: {Status} {Body}", (int)response.StatusCode, body);
                return null;
            }

            return OgmJson.Deserialize<RegisterResponse>(body);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Kayit istegi basarisiz (sunucu erisilemiyor olabilir).");
            return null;
        }
    }

    /// <summary>Periyodik durum bildirimi gonderir.</summary>
    public async Task<bool> SendHeartbeatAsync(HeartbeatRequest heartbeat, CancellationToken ct)
    {
        try
        {
            using var content = new StringContent(OgmJson.Serialize(heartbeat), Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(OgmProtocol.Routes.Heartbeat, content, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Heartbeat gonderilemedi (sunucu erisilemiyor olabilir).");
            return false;
        }
    }

    /// <summary>
    /// Bir isi (meta + ham veri) multipart/form-data olarak sunucuya yukler.
    /// Icerik akis (stream) halinde gonderilir; dosya belleğe tamamen yuklenmez.
    /// Basari durumunda sunucunun onayini, aksi halde null dondurur.
    /// </summary>
    public async Task<JobAck?> UploadJobAsync(
        PrintJobMeta meta,
        Stream payload,
        long payloadLength,
        CancellationToken ct)
    {
        try
        {
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(OgmJson.Serialize(meta), Encoding.UTF8, "application/json"),
                OgmProtocol.FormMetaField);

            var fileContent = new StreamContent(payload);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            fileContent.Headers.ContentLength = payloadLength;
            form.Add(fileContent, OgmProtocol.FormPayloadField, meta.JobId + ".spl");

            using var request = new HttpRequestMessage(HttpMethod.Post, OgmProtocol.Routes.UploadJob)
            {
                Content = form
            };
            request.Headers.TryAddWithoutValidation(OgmProtocol.AgentIdHeader, meta.AgentId);

            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Is yukleme basarisiz: {JobId} {Status}", meta.JobId, (int)response.StatusCode);
                return null;
            }

            return OgmJson.Deserialize<JobAck>(body)
                   ?? new JobAck(meta.JobId, true, "onay");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Is yukleme basarisiz: {JobId}", meta.JobId);
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}
