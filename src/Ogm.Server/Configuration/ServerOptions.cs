namespace Ogm.Server.Configuration;

/// <summary>
/// Sunucu uygulamasinin yapilandirmasi. appsettings.json icindeki "Server"
/// bolumunden okunur.
/// </summary>
public sealed class ServerOptions
{
    public const string SectionName = "Server";

    /// <summary>
    /// Ajanlarin kullanmasi gereken paylasilan API anahtari. Bos birakilirsa
    /// kimlik dogrulama devre disi kalir (yalnizca gelistirme icin onerilir).
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Sunucunun veri klasoru (ajan kayitlari, is kayitlari, yuklenen dosyalar).
    /// Bos ise %ProgramData%\Ogm\Server kullanilir.
    /// </summary>
    public string? DataDirectory { get; set; }

    /// <summary>
    /// Bir ajanin "cevrimici" sayilmasi icin son gorulme suresi esigi (saniye).
    /// Heartbeat araliginin en az 2-3 kati olmalidir.
    /// </summary>
    public int OnlineThresholdSeconds { get; set; } = 90;

    /// <summary>Ajanlara onerilen heartbeat araligi (saniye).</summary>
    public int HeartbeatSeconds { get; set; } = 30;

    /// <summary>Panelde tutulacak en fazla is kaydi sayisi.</summary>
    public int MaxJobsRetained { get; set; } = 2000;

    /// <summary>
    /// Kabul edilen en buyuk tek is boyutu (bayt). Bundan buyuk yuklemeler
    /// reddedilir; boylece diski/belleği tikayan dev dosyalar engellenir.
    /// </summary>
    public long MaxJobBytes { get; set; } = 512L * 1024 * 1024;

    /// <summary>
    /// Gelen isler XPS/PDF ise bunlari hemen PDF'e cevirip sakla. Varsayilan
    /// olarak kapalidir: PDF yalnizca panel kullanicisi istediginde (tembel)
    /// uretilir. Gorsel/EMF/RAW gibi ciktilar hicbir zaman PDF'e cevrilmez.
    /// </summary>
    public bool AutoConvertToPdf { get; set; } = false;

    /// <summary>Yapilandirilmis veri klasorunu dondurur ve olusturulmasini saglar.</summary>
    public string ResolveDataDirectory()
    {
        var root = !string.IsNullOrWhiteSpace(DataDirectory)
            ? DataDirectory!
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                Shared.OgmProtocol.AppName,
                "Server");

        Directory.CreateDirectory(root);
        return root;
    }
}
