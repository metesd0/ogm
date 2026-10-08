namespace Ogm.Shared;

/// <summary>
/// Ajan ve sunucu arasindaki tel protokolu (wire protocol) sabitleri.
/// Tum iletisim HTTP uzerinden yapilir; gercek zamanli panel akisi SSE
/// (Server-Sent Events) ile beslenir.
/// </summary>
public static class OgmProtocol
{
    /// <summary>Tel protokolunun surumu. Uyumsuz alanlar bu surume gore ele alinir.</summary>
    public const int Version = 1;

    /// <summary>Uygulama adi (klasor isimleri, servis adi vb. icin).</summary>
    public const string AppName = "Ogm";

    /// <summary>Varsayilan sunucu portu.</summary>
    public const int DefaultPort = 5099;

    /// <summary>Paylasilan API anahtarinin tasindigi HTTP basligi.</summary>
    public const string ApiKeyHeader = "X-Ogm-Api-Key";

    /// <summary>Ajan kimliginin tasindigi HTTP basligi.</summary>
    public const string AgentIdHeader = "X-Ogm-Agent-Id";

    /// <summary>multipart/form-data icinde meta verinin tasindigi alan adi.</summary>
    public const string FormMetaField = "meta";

    /// <summary>multipart/form-data icinde SPL iceriginin tasindigi alan adi.</summary>
    public const string FormPayloadField = "payload";

    /// <summary>Sunucu uc noktalari.</summary>
    public static class Routes
    {
        public const string Register = "/api/agents/register";
        public const string Heartbeat = "/api/agents/heartbeat";
        public const string UploadJob = "/api/jobs";
        public const string State = "/api/state";
        public const string Events = "/api/events";

        public static string JobPayload(string jobId) => $"/api/jobs/{jobId}/payload";
    }
}
