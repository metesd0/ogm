using Ogm.Agent.Configuration;
using Ogm.Agent.Print;
using Ogm.Agent.Queue;
using Ogm.Agent.Transport;
using Ogm.Shared;

namespace Ogm.Agent;

/// <summary>
/// Ajanin ana dongusu. Spool izleyicisini baslatir, sunucuya kaydolur ve iki
/// bagimsiz dongu calistirir:
///   1) Heartbeat dongusu  - periyodik durum bildirimi
///   2) Yukleme dongusu    - kuyruktaki isleri (baglanti varsa) sirayla gonderir
///
/// Sunucu erisilemezse hicbir is kaybolmaz; isler outbox kuyrugunda bekler ve
/// baglanti saglandiginda otomatik olarak gonderilir.
/// </summary>
public sealed class AgentWorker : BackgroundService
{
    private readonly AgentOptions _options;
    private readonly AgentIdentity _identity;
    private readonly OutboxQueue _queue;
    private readonly ServerClient _client;
    private readonly SpoolWatcher _watcher;
    private readonly PrinterManager _printerManager;
    private readonly ILogger<AgentWorker> _logger;

    public AgentWorker(
        AgentOptions options,
        AgentIdentity identity,
        OutboxQueue queue,
        ServerClient client,
        SpoolWatcher watcher,
        PrinterManager printerManager,
        ILogger<AgentWorker> logger)
    {
        _options = options;
        _identity = identity;
        _queue = queue;
        _client = client;
        _watcher = watcher;
        _printerManager = printerManager;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Ogm Ajan baslatildi. Kimlik={AgentId} Makine={Machine} Sunucu={Server}",
            _identity.AgentId, _identity.MachineName, _options.ServerUrl);

        // Sistemdeki tum yazicilarda KeepPrintedJobs ayarini otomatik ac
        await _printerManager.EnsureKeepPrintedJobsAsync(stoppingToken).ConfigureAwait(false);

        _watcher.Start();
        await TryRegisterAsync(stoppingToken).ConfigureAwait(false);

        var heartbeatLoop = HeartbeatLoopAsync(stoppingToken);
        var uploadLoop = UploadLoopAsync(stoppingToken);
        var maintenanceLoop = _printerManager.MaintenanceLoopAsync(stoppingToken);
        var pruneLoop = QueuePruneLoopAsync(stoppingToken);

        try
        {
            await Task.WhenAll(heartbeatLoop, uploadLoop, maintenanceLoop, pruneLoop).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Servis durduruluyor.
        }
        finally
        {
            _watcher.Dispose();
            _client.Dispose();
            _logger.LogInformation("Ogm Ajan durduruldu.");
        }
    }

    private async Task TryRegisterAsync(CancellationToken ct)
    {
        var response = await _client.RegisterAsync(_identity, ct).ConfigureAwait(false);
        if (response is null || !response.Accepted)
        {
            _logger.LogWarning(
                "Sunucuya kayit su an yapilamadi; cevrimdisi (offline) modda devam ediliyor. " +
                "Isler kuyrukta tutulacak ve baglanti saglandiginda gonderilecek.");
            return;
        }

        _logger.LogInformation("Sunucuya kayit basarili. Sunucu saati: {ServerTime}", response.ServerTimeUtc);
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(5, _options.HeartbeatSeconds));

        while (!ct.IsCancellationRequested)
        {
            var heartbeat = new HeartbeatRequest(
                AgentId: _identity.AgentId,
                PendingJobs: _queue.PendingCount,
                SentJobs: _queue.SentCount,
                FailedJobs: _queue.FailedCount,
                SpoolDirectory: _options.SpoolDirectory,
                CaptureEnabled: _options.CaptureEnabled,
                TimestampUtc: DateTimeOffset.UtcNow);

            if (await _client.SendHeartbeatAsync(heartbeat, ct).ConfigureAwait(false))
                _logger.LogDebug("Heartbeat gonderildi (bekleyen={Pending}).", _queue.PendingCount);

            if (!await DelayAsync(interval, ct).ConfigureAwait(false))
                break;
        }
    }

    private async Task UploadLoopAsync(CancellationToken ct)
    {
        var retryInterval = TimeSpan.FromSeconds(Math.Max(3, _options.UploadRetrySeconds));
        var batchSize = Math.Max(1, _options.MaxUploadBatch);

        while (!ct.IsCancellationRequested)
        {
            var batch = _queue.TakeBatch(batchSize);

            if (batch.Count == 0)
            {
                if (!await DelayAsync(retryInterval, ct).ConfigureAwait(false))
                    break;
                continue;
            }

            foreach (var entry in batch)
            {
                if (ct.IsCancellationRequested)
                    return;

                JobAck? ack;
                try
                {
                    // Icerigi belleğe tamamen yuklemeden akis halinde gonder.
                    await using var payload = new FileStream(
                        entry.PayloadPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        bufferSize: 1 << 16,
                        useAsync: true);

                    ack = await _client
                        .UploadJobAsync(entry.Meta, payload, payload.Length, ct)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Is icerigi okunamadi, kalici basarisiz: {JobId}", entry.JobId);
                    _queue.MarkFailed(entry.JobId, ex.Message);
                    continue;
                }

                if (ack is { Accepted: true })
                {
                    _queue.MarkSent(entry.JobId);
                    _logger.LogInformation("Is sunucuya gonderildi: {JobId} ({Bytes} bayt)",
                        entry.JobId, entry.Meta.TotalBytes);
                    continue;
                }

                var attempts = _queue.IncrementAttempts(entry.Directory);

                if (attempts >= _options.MaxUploadAttempts)
                {
                    _queue.MarkFailed(entry.JobId, $"Maksimum deneme sayisi asildi ({attempts}).");
                    _logger.LogWarning("Is kalici olarak basarisiz: {JobId}", entry.JobId);
                }
                else
                {
                    _logger.LogDebug("Is gonderilemedi (deneme {Attempts}), daha sonra tekrar denenecek: {JobId}",
                        attempts, entry.JobId);

                    // Baglanti sorunu varsa donguyu yavaslat.
                    if (!await DelayAsync(retryInterval, ct).ConfigureAwait(false))
                        return;
                }
            }
        }
    }

    private async Task QueuePruneLoopAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromHours(4);
        while (!ct.IsCancellationRequested)
        {
            if (!await DelayAsync(interval, ct).ConfigureAwait(false))
                break;

            try
            {
                _queue.PruneHistory(TimeSpan.FromDays(7), TimeSpan.FromDays(30));
                _logger.LogDebug("Kuyruk gecmisi temizlendi.");
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Kuyruk gecmisi temizlenirken hata.");
            }
        }
    }

    private static async Task<bool> DelayAsync(TimeSpan span, CancellationToken ct)
    {
        try
        {
            await Task.Delay(span, ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
