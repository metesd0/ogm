using System.Runtime.Versioning;

namespace Ogm.Agent.Configuration;

/// <summary>
/// Ajan uygulamasinin yapilandirmasi. appsettings.json icindeki "Agent"
/// bolumunden veya ortam degiskenlerinden okunur.
/// </summary>
public sealed class AgentOptions
{
    public const string SectionName = "Agent";

    /// <summary>Sunucu uygulamasinin taban adresi. Ornek: http://192.168.1.10:5099</summary>
    public string ServerUrl { get; set; } = $"http://127.0.0.1:{Shared.OgmProtocol.DefaultPort}";

    /// <summary>Sunucu ile paylasilan API anahtari (opsiyonel ama onerilir).</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Yazici spool klasoru. Yazdirilan ham veri (.SPL) ve golge dosyalari (.SHD)
    /// burada olusur. Yazicilarda "yazdirilan belgeleri sakla" (Keep printed
    /// documents) secenegi acik olmalidir; aksi halde spooler isleri hizla siler.
    /// </summary>
    public string SpoolDirectory { get; set; } = @"C:\Windows\System32\spool\PRINTERS";

    /// <summary>
    /// Ajanin kalici veri klasoru (kimlik, kuyruk). Bos birakilirsa
    /// %ProgramData%\Ogm\Agent kullanilir.
    /// </summary>
    public string? DataDirectory { get; set; }

    /// <summary>Sunucuya heartbeat gonderme araligi (saniye).</summary>
    public int HeartbeatSeconds { get; set; } = 30;

    /// <summary>Basarisiz yuklemeleri yeniden deneme araligi (saniye).</summary>
    public int UploadRetrySeconds { get; set; } = 15;

    /// <summary>Tek turda denenecek en fazla is sayisi.</summary>
    public int MaxUploadBatch { get; set; } = 5;

    /// <summary>
    /// Bir is icin denenecek en fazla yukleme sayisi. Asilirsa is
    /// "failed" klasorune tasinir.
    /// </summary>
    public int MaxUploadAttempts { get; set; } = 50;

    /// <summary>Kabul edilen en buyuk tek is boyutu (bayt).</summary>
    public long MaxPayloadBytes { get; set; } = 256L * 1024 * 1024;

    /// <summary>Spool yakalama aktif mi.</summary>
    public bool CaptureEnabled { get; set; } = true;

    /// <summary>
    /// Bir .SPL dosyasinin yaziminin bittigine karar vermek icin beklenen
    /// boyut-sabitligi suresi (ms).
    /// </summary>
    public int FileStabilizeMilliseconds { get; set; } = 750;

    /// <summary>
    /// Sistemdeki yazicilarda "yazdirilan belgeleri sakla" (KeepPrintedJobs)
    /// ayarini otomatik olarak acip acmayacagi.
    /// </summary>
    public bool AutoEnableKeepPrintedJobs { get; set; } = true;

    /// <summary>
    /// Yazdirilmis islerin spooler'dan otomatik temizlenme ve yeni yazicilari
    /// denetleme araligi (dakika). 0 ise otomatik periyodik dongu kapatilir.
    /// </summary>
    public int PrinterMaintenanceMinutes { get; set; } = 5;

    /// <summary>Yapilandirilmis veri klasorunu dondurur ve olusturulmasini saglar.</summary>
    [SupportedOSPlatform("windows")]
    public string ResolveDataDirectory()
    {
        var root = !string.IsNullOrWhiteSpace(DataDirectory)
            ? DataDirectory!
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                Shared.OgmProtocol.AppName,
                "Agent");

        Directory.CreateDirectory(root);
        return root;
    }
}
