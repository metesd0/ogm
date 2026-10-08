using Ogm.Agent.Configuration;
using Ogm.Shared;

namespace Ogm.Agent.Queue;

/// <summary>Kuyruktaki tek bir isi temsil eder.</summary>
public sealed record OutboxEntry(
    string JobId,
    PrintJobMeta Meta,
    string Directory,
    string MetaPath,
    string PayloadPath,
    int Attempts);

/// <summary>
/// Dosya tabanli, dayanikli (persistent) giden kutusu (outbox).
///
/// Dizin yapisi (veri klasoru altinda):
///   outbox/<jobId>/meta.json   - is meta verisi
///   outbox/<jobId>/payload.bin - yazdirilan ham veri (.SPL)
///   sent/<jobId>/...            - sunucuya basariyla gonderilenler
///   failed/<jobId>/...          - kalici olarak basarisiz olanlar
///
/// Sunucu ile baglanti kurulamazsa isler outbox'ta kalir ve baglanti
/// saglandiginda sirayla gonderilir. meta.json en son yazildigi icin yarim
/// kalan kayitlar guvenle atlanir. Yazmalar atomik yapilir.
///
/// Sayaçlar (pending/sent/failed) her cagrida diski taramaz; bellekte
/// tutulur ve yalnizca acilista bir kez sayilir.
/// </summary>
public sealed class OutboxQueue
{
    private const string MetaFileName = "meta.json";
    private const string PayloadFileName = "payload.bin";
    private const string AttemptsFileName = "attempts.txt";
    private const string ErrorFileName = "error.txt";

    private readonly ILogger<OutboxQueue> _logger;
    private readonly object _gate = new();

    private int _pendingCount;
    private int _sentCount;
    private int _failedCount;

    public OutboxQueue(AgentOptions options, ILogger<OutboxQueue> logger)
    {
        _logger = logger;

        var root = options.ResolveDataDirectory();
        OutboxDirectory = Path.Combine(root, "outbox");
        SentDirectory = Path.Combine(root, "sent");
        FailedDirectory = Path.Combine(root, "failed");

        Directory.CreateDirectory(OutboxDirectory);
        Directory.CreateDirectory(SentDirectory);
        Directory.CreateDirectory(FailedDirectory);

        // Eski sent ve failed kayitlarini temizle (diskte sonsuz buyumeyi engelle)
        PruneHistory(TimeSpan.FromDays(7), TimeSpan.FromDays(30));

        // Sayaçlar acilista bir kez hesaplanir; sonrasinda bellekten okunur.
        _pendingCount = CountDirectories(OutboxDirectory);
        _sentCount = CountDirectories(SentDirectory);
        _failedCount = CountDirectories(FailedDirectory);
    }

    public string OutboxDirectory { get; }

    public string SentDirectory { get; }

    public string FailedDirectory { get; }

    public int PendingCount => Volatile.Read(ref _pendingCount);

    public int SentCount => Volatile.Read(ref _sentCount);

    public int FailedCount => Volatile.Read(ref _failedCount);

    /// <summary>
    /// Kaynaktaki ham veriyi belleğe tamamen yuklemeden akis halinde outbox'a
    /// kopyalar (atomik), SHA-256 karmasini hesaplar ve meta kaydini en son
    /// yazar. Basarili olursa nihai meta verisini dondurur.
    /// </summary>
    public async Task<PrintJobMeta> EnqueueFromFileAsync(
        PrintJobMeta template,
        Stream source,
        CancellationToken ct)
    {
        var jobDirectory = Path.Combine(OutboxDirectory, template.JobId);
        var payloadPath = Path.Combine(jobDirectory, PayloadFileName);
        var metaPath = Path.Combine(jobDirectory, MetaFileName);

        try
        {
            Directory.CreateDirectory(jobDirectory);

            var (bytes, sha256) = await AtomicFile.CopyAndHashAsync(source, payloadPath, ct)
                .ConfigureAwait(false);

            if (bytes <= 0)
                throw new InvalidOperationException("Bos is icerigi kuyruga alinamaz.");

            var meta = template with
            {
                TotalBytes = bytes,
                Sha256 = sha256
            };

            // meta.json'i en son yaz: yarim kayitlar guvenle atlanir.
            await AtomicFile.WriteAllTextAsync(metaPath, OgmJson.Serialize(meta), ct)
                .ConfigureAwait(false);

            Interlocked.Increment(ref _pendingCount);

            _logger.LogInformation("Kuyruga alindi: {JobId} ({Bytes} bayt, yazici={Printer})",
                meta.JobId, bytes, meta.PrinterName);

            return meta;
        }
        catch
        {
            // Yarim kalan kaydi temizle; is bir sonraki denemede yeniden alinir.
            TryDeleteDirectory(jobDirectory);
            throw;
        }
    }

    /// <summary>Kuyruktan en eski isleri (en fazla <paramref name="max"/>) dondurur.</summary>
    public IReadOnlyList<OutboxEntry> TakeBatch(int max)
    {
        var entries = new List<OutboxEntry>();
        if (!Directory.Exists(OutboxDirectory))
            return entries;

        IEnumerable<string> directories;
        try
        {
            directories = Directory.EnumerateDirectories(OutboxDirectory)
                .OrderBy(path => new DirectoryInfo(path).CreationTimeUtc)
                .ThenBy(path => path, StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Outbox klasoru okunamadi.");
            return entries;
        }

        foreach (var directory in directories)
        {
            if (entries.Count >= max)
                break;

            var metaPath = Path.Combine(directory, MetaFileName);
            var payloadPath = Path.Combine(directory, PayloadFileName);

            if (!File.Exists(metaPath) || !File.Exists(payloadPath))
                continue;

            PrintJobMeta? meta;
            try
            {
                meta = OgmJson.Deserialize<PrintJobMeta>(File.ReadAllText(metaPath));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Bozuk meta.json atlandi: {Path}", metaPath);
                continue;
            }

            if (meta is null)
                continue;

            entries.Add(new OutboxEntry(
                meta.JobId,
                meta,
                directory,
                metaPath,
                payloadPath,
                ReadAttempts(directory)));
        }

        return entries;
    }

    /// <summary>Isi basarili olarak gonderildi olarak isaretler.</summary>
    public void MarkSent(string jobId)
    {
        lock (_gate)
        {
            var source = Path.Combine(OutboxDirectory, jobId);
            if (!Directory.Exists(source))
                return;

            var target = Path.Combine(SentDirectory, jobId);
            try
            {
                if (Directory.Exists(target))
                    Directory.Delete(target, recursive: true);

                Directory.Move(source, target);

                // Yer kazanmak icin icerigi sil, meta kaydini sakla.
                var payload = Path.Combine(target, PayloadFileName);
                if (File.Exists(payload))
                    File.Delete(payload);

                DecrementPending();
                Interlocked.Increment(ref _sentCount);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Is 'gonderildi' olarak tasinamadi: {JobId}", jobId);
            }
        }
    }

    /// <summary>Isi kalici olarak basarisiz olarak isaretler.</summary>
    public void MarkFailed(string jobId, string reason)
    {
        lock (_gate)
        {
            var source = Path.Combine(OutboxDirectory, jobId);
            if (!Directory.Exists(source))
                return;

            var target = Path.Combine(FailedDirectory, jobId);
            try
            {
                if (Directory.Exists(target))
                    Directory.Delete(target, recursive: true);

                Directory.Move(source, target);
                AtomicFile.WriteAllText(Path.Combine(target, ErrorFileName), reason);

                DecrementPending();
                Interlocked.Increment(ref _failedCount);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Is 'basarisiz' olarak tasinamadi: {JobId}", jobId);
            }
        }
    }

    /// <summary>Yukleme denemesi sayacini bir arttirir ve yeni degeri dondurur.</summary>
    public int IncrementAttempts(string jobDirectory)
    {
        lock (_gate)
        {
            var current = ReadAttempts(jobDirectory) + 1;
            try
            {
                AtomicFile.WriteAllText(Path.Combine(jobDirectory, AttemptsFileName), current.ToString());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Deneme sayaci yazilamadi: {Path}", jobDirectory);
            }

            return current;
        }
    }

    private void DecrementPending()
    {
        while (true)
        {
            var current = Volatile.Read(ref _pendingCount);
            if (current <= 0)
                return;

            if (Interlocked.CompareExchange(ref _pendingCount, current - 1, current) == current)
                return;
        }
    }

    private static int ReadAttempts(string jobDirectory)
    {
        try
        {
            var path = Path.Combine(jobDirectory, AttemptsFileName);
            if (!File.Exists(path))
                return 0;

            return int.TryParse(File.ReadAllText(path), out var value) ? value : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static int CountDirectories(string directory)
    {
        try
        {
            return Directory.Exists(directory)
                ? Directory.EnumerateDirectories(directory).Count()
                : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Eski gonderilmis (sent) ve basarisiz (failed) is kayitlarini silerek
    /// diskte sinirsiz dosya birikmesini engeller.
    /// </summary>
    public void PruneHistory(TimeSpan sentMaxAge, TimeSpan failedMaxAge)
    {
        lock (_gate)
        {
            PruneDirectory(SentDirectory, sentMaxAge, ref _sentCount);
            PruneDirectory(FailedDirectory, failedMaxAge, ref _failedCount);
        }
    }

    private void PruneDirectory(string root, TimeSpan maxAge, ref int counter)
    {
        try
        {
            if (!Directory.Exists(root))
                return;

            var threshold = DateTime.UtcNow - maxAge;
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                try
                {
                    var di = new DirectoryInfo(dir);
                    if (di.LastWriteTimeUtc < threshold)
                    {
                        Directory.Delete(dir, recursive: true);
                        Interlocked.Decrement(ref counter);
                    }
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Klasor temizligi sirasinda hata: {Path}", root);
        }
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch
        {
            // Temizlik en iyi caba ile yapilir.
        }
    }
}
