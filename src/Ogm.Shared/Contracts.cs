namespace Ogm.Shared;

/// <summary>
/// Ajanin sunucuya gonderdigi kimlik kaydi.
/// </summary>
public sealed record RegisterRequest(
    string AgentId,
    string MachineName,
    string UserName,
    string OsVersion,
    string AgentVersion,
    int ProtocolVersion);

/// <summary>
/// Sunucunun kayit istegine cevabi. Sunucu, ajanin kullanmasi gereken
/// heartbeat araligini ve API sozlesme surumunu geri bildirir.
/// </summary>
public sealed record RegisterResponse(
    bool Accepted,
    int ProtocolVersion,
    int HeartbeatSeconds,
    DateTimeOffset ServerTimeUtc,
    string? Message);

/// <summary>
/// Ajanin periyodik olarak gonderdigi saglik/durum bildirimi.
/// </summary>
public sealed record HeartbeatRequest(
    string AgentId,
    int PendingJobs,
    int SentJobs,
    int FailedJobs,
    string SpoolDirectory,
    bool CaptureEnabled,
    DateTimeOffset TimestampUtc);

/// <summary>
/// Bir yazdirma isinin meta verisi. Icerik (SPL) ayri bir dosya olarak
/// multipart istegi icinde gonderilir.
/// </summary>
public sealed record PrintJobMeta(
    string JobId,
    string AgentId,
    string MachineName,
    string UserName,
    string PrinterName,
    string DocumentName,
    string DataType,
    long TotalBytes,
    int TotalPages,
    string Sha256,
    DateTimeOffset CapturedUtc,
    string? SourceFileName);

/// <summary>
/// Sunucunun yukleme sonucu dondurdugu onay mesaji.
/// </summary>
public sealed record JobAck(
    string JobId,
    bool Accepted,
    string? Message);

/// <summary>
/// Panelde gosterilen ajan ozeti.
/// </summary>
public sealed record AgentInfo(
    string AgentId,
    string MachineName,
    string UserName,
    string OsVersion,
    string AgentVersion,
    DateTimeOffset FirstSeenUtc,
    DateTimeOffset LastSeenUtc,
    string? IpAddress,
    int PendingJobs,
    int SentJobs,
    int FailedJobs,
    bool CaptureEnabled)
{
    public bool IsOnline(DateTimeOffset now, TimeSpan threshold)
        => now - LastSeenUtc <= threshold;
}

/// <summary>
/// Panelde gosterilen is ozeti (payload olmadan).
/// </summary>
public sealed record JobSummary(
    string JobId,
    string AgentId,
    string MachineName,
    string UserName,
    string PrinterName,
    string DocumentName,
    string DataType,
    long TotalBytes,
    int TotalPages,
    string Sha256,
    DateTimeOffset ReceivedUtc,
    bool HasPdf = false);

/// <summary>
/// Panelin tam durum anlik goruntusu.
/// </summary>
public sealed record ServerState(
    DateTimeOffset ServerTimeUtc,
    int OnlineThresholdSeconds,
    IReadOnlyList<AgentInfo> Agents,
    IReadOnlyList<JobSummary> Jobs);

/// <summary>
/// SSE (Server-Sent Events) ile panele itilen olay tipleri.
/// </summary>
public enum ServerEventKind
{
    /// <summary>Baglanti kuruldugunda gonderilen tam durum anlik goruntusu.</summary>
    Snapshot,

    /// <summary>Tek bir ajan kaydedildi/guncellendi.</summary>
    AgentRegistered,

    /// <summary>Tek bir ajandan heartbeat alindi.</summary>
    AgentHeartbeat,

    /// <summary>Tek bir yeni is alindi.</summary>
    JobReceived,

    /// <summary>Veriler sifirlandi; tam durum yeniden gonderilir.</summary>
    StateSnapshot
}

/// <summary>
/// SSE ile gonderilen olay zarfi.
///
/// Artimsal (incremental) tasarima gore her olayda tum durum gonderilmez:
/// yalnizca <see cref="Kind"/> ile ilgili alan doldurulur. Tam durum
/// (<see cref="State"/>) yalnizca <see cref="ServerEventKind.Snapshot"/> ve
/// <see cref="ServerEventKind.StateSnapshot"/> olaylarinda tasinir.
/// </summary>
public sealed record ServerEvent(
    ServerEventKind Kind,
    DateTimeOffset TimestampUtc,
    ServerState? State = null,
    AgentInfo? Agent = null,
    JobSummary? Job = null);

/// <summary>
/// Panelde gosterilen is onizleme ve ham veri inceleme ozeti.
/// </summary>
public sealed record JobPreview(
    string JobId,
    long TotalBytes,
    string DetectedFormat,
    IReadOnlyList<string> ExtractedStrings,
    string HexDump);
