using System.Collections.Concurrent;
using Ogm.Agent.Configuration;
using Ogm.Agent.Queue;
using Ogm.Shared;

namespace Ogm.Agent.Print;

/// <summary>
/// Yazici spool klasorunu izler. Spooler bir isi yazdirirken ayni taban adli
/// ikili bir dosya cifti olusturur:
///   <job>.SPL - yaziciya gonderilen ham veri (EMF/RAW)
///   <job>.SHD - isin golge (metadata) dosyasi
///
/// .SPL dosyasi olgunlasinca (boyutu sabitlenince) icerigi belleğe tamamen
/// yuklenmeden akis halinde kopyalanir, .SHD'den meta veri cikarilir ve is
/// dayanikli outbox kuyruguna eklenir. Boylece buyuk dosyalar bile sunucu
/// cokmeksizin islenir ve sunucu erisilemez olsa dahi is kaybolmaz.
/// </summary>
public sealed class SpoolWatcher : IDisposable
{
    private const int CopyBufferSize = 1 << 16; // 64 KiB

    private readonly AgentOptions _options;
    private readonly OutboxQueue _queue;
    private readonly AgentIdentity _identity;
    private readonly ILogger<SpoolWatcher> _logger;

    private readonly ConcurrentDictionary<string, byte> _inFlight = new(StringComparer.OrdinalIgnoreCase);

    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _cts;

    public SpoolWatcher(
        AgentOptions options,
        OutboxQueue queue,
        AgentIdentity identity,
        ILogger<SpoolWatcher> logger)
    {
        _options = options;
        _queue = queue;
        _identity = identity;
        _logger = logger;
    }

    public void Start()
    {
        if (!_options.CaptureEnabled)
        {
            _logger.LogInformation("Spool yakalama yapilandirma ile kapatilmis.");
            return;
        }

        if (!Directory.Exists(_options.SpoolDirectory))
        {
            _logger.LogError("Spool klasoru bulunamadi: {Directory}", _options.SpoolDirectory);
            return;
        }

        _cts = new CancellationTokenSource();

        _watcher = new FileSystemWatcher(_options.SpoolDirectory)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            IncludeSubdirectories = false,
            EnableRaisingEvents = true
        };

        _watcher.Created += OnSpoolChanged;
        _watcher.Changed += OnSpoolChanged;
        _watcher.Renamed += OnSpoolRenamed;
        _watcher.Error += (_, e) => _logger.LogWarning(e.GetException(), "Spool izleyici hatasi.");

        _logger.LogInformation("Spool klasoru izleniyor: {Directory}", _options.SpoolDirectory);

        // Uygulama yeniden baslatildiginda klasorde bekleyen .SPL dosyalarini da isle.
        foreach (var existing in Directory.EnumerateFiles(_options.SpoolDirectory, "*.SPL"))
            QueueCapture(existing);
    }

    private void OnSpoolChanged(object sender, FileSystemEventArgs e)
    {
        if (IsSpoolPayload(e.FullPath))
            QueueCapture(e.FullPath);
    }

    private void OnSpoolRenamed(object sender, RenamedEventArgs e)
    {
        // Spooler bazi durumlarda dosyayi gecici addan .SPL'e tasir.
        if (IsSpoolPayload(e.FullPath))
            QueueCapture(e.FullPath);
    }

    private static bool IsSpoolPayload(string path)
        => string.Equals(Path.GetExtension(path), ".SPL", StringComparison.OrdinalIgnoreCase);

    private void QueueCapture(string splPath)
    {
        if (!_inFlight.TryAdd(splPath, 0))
            return;

        var token = _cts?.Token ?? CancellationToken.None;
        _ = Task.Run(() => CaptureAsync(splPath, token), CancellationToken.None);
    }

    private async Task CaptureAsync(string splPath, CancellationToken ct)
    {
        try
        {
            if (!await WaitForStableAsync(splPath, ct))
            {
                _logger.LogWarning("Dosya olgunlasmadi, atlandi: {Path}", splPath);
                return;
            }

            long length;
            try
            {
                length = new FileInfo(splPath).Length;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Spool boyutu okunamadi: {Path}", splPath);
                return;
            }

            if (length <= 0)
            {
                _logger.LogWarning("Spool dosyasi bos, atlandi: {Path}", splPath);
                return;
            }

            if (length > _options.MaxPayloadBytes)
            {
                _logger.LogWarning("Is cok buyuk ({Bytes} bayt), atlandi: {Path}", length, splPath);
                return;
            }

            var baseName = Path.GetFileNameWithoutExtension(splPath);
            var info = ReadShadowInfo(splPath, baseName);

            // Meta veri iskeleti; boyut ve SHA-256 akis halinde kopyalama
            // sirasinda OutboxQueue tarafindan doldurulur.
            var template = new PrintJobMeta(
                JobId: Guid.NewGuid().ToString("N"),
                AgentId: _identity.AgentId,
                MachineName: _identity.MachineName,
                UserName: info.UserName ?? _identity.UserName,
                PrinterName: info.PrinterName ?? "Unknown",
                DocumentName: info.DocumentName ?? baseName,
                DataType: info.DataType ?? "RAW",
                TotalBytes: length,
                TotalPages: 0,
                Sha256: string.Empty,
                CapturedUtc: DateTimeOffset.UtcNow,
                SourceFileName: Path.GetFileName(splPath));

            await EnqueueWithRetryAsync(template, splPath, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Kapanis sirasinda normal.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Spool yakalama hatasi: {Path}", splPath);
        }
        finally
        {
            _inFlight.TryRemove(splPath, out _);
        }
    }

    /// <summary>
    /// Spooler dosyayi kilitli tutarken bile okunabilmesi icin paylasimli
    /// (FileShare.ReadWrite | Delete) sekilde, birkac denemeyle akitir.
    /// </summary>
    private async Task EnqueueWithRetryAsync(PrintJobMeta template, string splPath, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await using var source = new FileStream(
                    splPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    CopyBufferSize,
                    useAsync: true);

                await _queue.EnqueueFromFileAsync(template, source, ct).ConfigureAwait(false);
                return;
            }
            catch (IOException)
            {
                await Task.Delay(150, ct).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException)
            {
                await Task.Delay(150, ct).ConfigureAwait(false);
            }
        }

        _logger.LogWarning("Spool dosyasi okunamadi (kilitli olabilir), atlandi: {Path}", splPath);
    }

    private SpoolJobInfo ReadShadowInfo(string splPath, string fallbackDocumentName)
    {
        var shdPath = Path.ChangeExtension(splPath, ".SHD");
        if (!File.Exists(shdPath))
            return new SpoolJobInfo(null, fallbackDocumentName, null, null);

        try
        {
            using var fs = new FileStream(shdPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var ms = new MemoryStream();
            fs.CopyTo(ms);
            return SpoolShdParser.Parse(ms.GetBuffer().AsSpan(0, (int)ms.Length), fallbackDocumentName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SHD dosyasi okunamadi: {Path}", shdPath);
            return new SpoolJobInfo(null, fallbackDocumentName, null, null);
        }
    }

    /// <summary>Dosya boyutu belirli bir sure sabit kalana kadar bekler.</summary>
    private async Task<bool> WaitForStableAsync(string path, CancellationToken ct)
    {
        var requiredStableMs = Math.Max(200, _options.FileStabilizeMilliseconds);
        var deadline = DateTime.UtcNow.AddSeconds(30);

        long lastSize = -1;
        var stableSince = DateTime.UtcNow;

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            long size;
            try
            {
                size = new FileInfo(path).Length;
            }
            catch
            {
                return false;
            }

            if (size != lastSize)
            {
                lastSize = size;
                stableSince = DateTime.UtcNow;
            }
            else if (size > 0 && (DateTime.UtcNow - stableSince).TotalMilliseconds >= requiredStableMs)
            {
                return true;
            }

            await Task.Delay(100, ct).ConfigureAwait(false);
        }

        return lastSize > 0;
    }

    public void Dispose()
    {
        try
        {
            if (_watcher is not null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Created -= OnSpoolChanged;
                _watcher.Changed -= OnSpoolChanged;
                _watcher.Renamed -= OnSpoolRenamed;
                _watcher.Dispose();
                _watcher = null;
            }

            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }
        catch
        {
            // Kapanis hatalarini yut.
        }
    }
}
