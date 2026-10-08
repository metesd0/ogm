using System.Collections.Concurrent;
using System.Threading.Channels;
using Ogm.Server.Configuration;
using Ogm.Shared;

namespace Ogm.Server.Storage;

/// <summary>
/// Sunucunun bellekteki durumunu tutar ve diske kalici hale getirir.
/// Ajan kayitlari ve is ozetleri JSON dosyalarinda saklanir; yuklenen ham
/// yazdirma verileri jobs klasorunde .spl uzantisiyla tutulur.
///
/// Dayaniklilik ve performans notlari:
/// * Yuklemeler akis (stream) halinde diske yazilir; buyuk dosyalar belleğe
///   tamamen yuklenmez.
/// * Tum dosya yazmalari atomik yapilir (once gecici dosya, sonra tasima).
/// * Diske yazma her heartbeat'te degil, arka planda toplu (coalesced) yapilir.
/// * <see cref="Changed"/> olayi tam durum yerine yalnizca degisen varligi tasir.
/// </summary>
public sealed class ServerStore : IDisposable
{
    private readonly ServerOptions _options;
    private readonly ILogger<ServerStore> _logger;

    private readonly object _gate = new();          // _jobs listesi korumasi
    private readonly object _persistGate = new();   // disk yazmalarini serilestirir
    private readonly object _convertGate = new();   // PDF donusturmeyi serilestirir

    private readonly ConcurrentDictionary<string, AgentInfo> _agents = new(StringComparer.Ordinal);
    private readonly LinkedList<JobSummary> _jobs = new();

    private readonly string _jobsDirectory;
    private readonly string _agentsFile;
    private readonly string _jobsFile;

    // Diske yazmayi arka planda toplayan sinyal kanali. Tek okur; boylece
    // es zamanli yazmalar serilesir ve heartbeat firtinalari tek yazmada birlesir.
    private readonly Channel<byte> _persistSignals =
        Channel.CreateUnbounded<byte>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly Task _persistTask;
    private int _agentsDirty;
    private int _jobsDirty;
    private bool _disposed;

    public ServerStore(ServerOptions options, ILogger<ServerStore> logger)
    {
        _options = options;
        _logger = logger;

        var root = options.ResolveDataDirectory();
        _jobsDirectory = Path.Combine(root, "jobs");
        _agentsFile = Path.Combine(root, "agents.json");
        _jobsFile = Path.Combine(root, "jobs.json");

        Directory.CreateDirectory(_jobsDirectory);

        LoadAgents();
        LoadJobs();

        _persistTask = Task.Run(PersistLoopAsync);
    }

    /// <summary>
    /// Durumda bir degisiklik oldugunda tetiklenir. Olay yalnizca degisen
    /// varligi (ajan veya is) tasir; tam durum yalnizca anlik goruntu
    /// (snapshot) olaylarinda gonderilir.
    /// </summary>
    public event Action<ServerEvent>? Changed;

    /// <summary>Ajani kaydeder veya mevcut kaydini gunceller.</summary>
    public AgentInfo RegisterOrUpdate(RegisterRequest request, string? ipAddress)
    {
        var now = DateTimeOffset.UtcNow;

        var info = _agents.AddOrUpdate(
            request.AgentId,
            _ => new AgentInfo(
                request.AgentId, request.MachineName, request.UserName, request.OsVersion,
                request.AgentVersion, now, now, ipAddress, 0, 0, 0, true),
            (_, existing) => existing with
            {
                MachineName = request.MachineName,
                UserName = request.UserName,
                OsVersion = request.OsVersion,
                AgentVersion = request.AgentVersion,
                LastSeenUtc = now,
                IpAddress = ipAddress ?? existing.IpAddress
            });

        MarkAgentsDirty();
        Raise(new ServerEvent(ServerEventKind.AgentRegistered, now, Agent: info));
        _logger.LogInformation("Ajan kaydedildi: {AgentId} ({Machine})", info.AgentId, info.MachineName);
        return info;
    }

    /// <summary>Heartbeat ile ajan durumunu gunceller.</summary>
    public void ApplyHeartbeat(HeartbeatRequest heartbeat, string? ipAddress)
    {
        var now = DateTimeOffset.UtcNow;

        var info = _agents.AddOrUpdate(
            heartbeat.AgentId,
            _ => new AgentInfo(
                heartbeat.AgentId, "unknown", "unknown", "unknown", "unknown",
                now, now, ipAddress, heartbeat.PendingJobs, heartbeat.SentJobs,
                heartbeat.FailedJobs, heartbeat.CaptureEnabled),
            (_, existing) => existing with
            {
                LastSeenUtc = now,
                IpAddress = ipAddress ?? existing.IpAddress,
                PendingJobs = heartbeat.PendingJobs,
                SentJobs = heartbeat.SentJobs,
                FailedJobs = heartbeat.FailedJobs,
                CaptureEnabled = heartbeat.CaptureEnabled
            });

        // Her heartbeat'te diske yazmak yerine toplu yazma icin isaretle.
        MarkAgentsDirty();
        Raise(new ServerEvent(ServerEventKind.AgentHeartbeat, now, Agent: info));
    }

    /// <summary>
    /// Yeni bir yazdirma isini kaydeder ve ham veriyi akis halinde diske yazar.
    /// Buyuk dosyalar belleğe tamamen yuklenmez; SHA-256 kopyalama sirasinda
    /// hesaplanir.
    /// </summary>
    public async Task<JobSummary> AddJobAsync(PrintJobMeta meta, Stream payload, CancellationToken ct)
    {
        Directory.CreateDirectory(_jobsDirectory);
        var payloadPath = Path.Combine(_jobsDirectory, meta.JobId + ".spl");

        // Akis halinde yaz + karmayi ayni gecisde hesapla (atomik yazma).
        var (written, sha256) = await AtomicFile.CopyAndHashAsync(payload, payloadPath, ct).ConfigureAwait(false);

        if (written <= 0)
        {
            TryDeletePayload(meta.JobId);
            throw new InvalidOperationException("Bos yukleme reddedildi.");
        }

        if (!string.Equals(meta.Sha256, sha256, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug(
                "SHA-256 uyusmazligi (istemci={Client}, sunucu={Server}) JobId={JobId}",
                meta.Sha256, sha256, meta.JobId);
        }

        // Yalnizca gercekten donusturulebilen (PDF/XPS) girdiler PDF sayilir.
        var hasPdf = JobConverter.CanConvert(payloadPath);

        // Otomatik donusturme yalnizca acikca etkinlestirildiyse yapilir.
        if (_options.AutoConvertToPdf && hasPdf)
        {
            var pdfPath = Path.Combine(_jobsDirectory, meta.JobId + ".pdf");
            lock (_convertGate)
            {
                hasPdf = File.Exists(pdfPath) || JobConverter.TryConvertToPdf(payloadPath, pdfPath, _logger);
            }
        }

        var dlpAlerts = PayloadTextExtractor.AnalyzeDlp(payloadPath);

        var now = DateTimeOffset.UtcNow;
        var summary = new JobSummary(
            JobId: meta.JobId,
            AgentId: meta.AgentId,
            MachineName: meta.MachineName,
            UserName: meta.UserName,
            PrinterName: meta.PrinterName,
            DocumentName: meta.DocumentName,
            DataType: meta.DataType,
            TotalBytes: written,
            TotalPages: meta.TotalPages,
            Sha256: sha256,
            ReceivedUtc: now,
            HasPdf: hasPdf,
            DlpAlerts: dlpAlerts.Count > 0 ? dlpAlerts : null);

        var removed = new List<string>();
        lock (_gate)
        {
            _jobs.AddLast(summary);

            while (_jobs.Count > _options.MaxJobsRetained)
            {
                var evicted = _jobs.First!.Value;
                _jobs.RemoveFirst();
                removed.Add(evicted.JobId);
            }
        }

        // Kilit disinda temizle (disk I/O).
        foreach (var evictedId in removed)
            TryDeletePayload(evictedId);

        MarkJobsDirty();
        Raise(new ServerEvent(ServerEventKind.JobReceived, now, Job: summary));
        _logger.LogInformation("Is alindi: {JobId} {Machine} {Printer} ({Bytes} bayt, HasPdf={HasPdf})",
            summary.JobId, summary.MachineName, summary.PrinterName, summary.TotalBytes, hasPdf);
        return summary;
    }

    /// <summary>Panelin gosterdigi tam durum anlik goruntusunu uretir.</summary>
    public ServerState GetState()
    {
        var agents = _agents.Values
            .OrderBy(a => a.MachineName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(a => a.AgentId, StringComparer.Ordinal)
            .ToList();

        List<JobSummary> jobs;
        lock (_gate)
        {
            jobs = _jobs.Reverse().ToList(); // en yeni ilk
        }

        return new ServerState(
            ServerTimeUtc: DateTimeOffset.UtcNow,
            OnlineThresholdSeconds: _options.OnlineThresholdSeconds,
            Agents: agents,
            Jobs: jobs);
    }

    /// <summary>Isin ham veri dosyasinin yolunu dondurur (yoksa null).</summary>
    public string? GetPayloadPath(string jobId)
    {
        var path = Path.Combine(_jobsDirectory, jobId + ".spl");
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Isin PDF dosyasinin yolunu dondurur. PDF zaten varsa onu, yoksa yalnizca
    /// gercekten donusturulebilen (PDF/XPS) girdiler icin talep uzerine uretip
    /// yolunu dondurur. Gorsel/EMF/RAW gibi girdiler icin null doner.
    /// </summary>
    public string? GetPdfPath(string jobId)
    {
        var pdfPath = Path.Combine(_jobsDirectory, jobId + ".pdf");
        if (File.Exists(pdfPath))
            return pdfPath;

        var splPath = Path.Combine(_jobsDirectory, jobId + ".spl");
        if (!File.Exists(splPath) || !JobConverter.CanConvert(splPath))
            return null;

        lock (_convertGate)
        {
            if (File.Exists(pdfPath))
                return pdfPath;

            return JobConverter.TryConvertToPdf(splPath, pdfPath, _logger) ? pdfPath : null;
        }
    }

    private void Raise(ServerEvent serverEvent)
    {
        var handler = Changed;
        if (handler is null)
            return;

        try
        {
            handler(serverEvent);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Panel olay yayini basarisiz oldu.");
        }
    }

    private void TryDeletePayload(string jobId)
    {
        try
        {
            var splPath = Path.Combine(_jobsDirectory, jobId + ".spl");
            if (File.Exists(splPath)) File.Delete(splPath);

            var pdfPath = Path.Combine(_jobsDirectory, jobId + ".pdf");
            if (File.Exists(pdfPath)) File.Delete(pdfPath);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Eski is dosyasi silinemedi: {JobId}", jobId);
        }
    }

    private void LoadAgents()
    {
        try
        {
            if (!File.Exists(_agentsFile))
                return;

            var list = OgmJson.Deserialize<List<AgentInfo>>(File.ReadAllText(_agentsFile));
            if (list is null)
                return;

            foreach (var agent in list)
                _agents[agent.AgentId] = agent;

            _logger.LogInformation("{Count} ajan kaydi yuklendi.", list.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ajan kayitlari yuklenemedi.");
        }
    }

    private void LoadJobs()
    {
        try
        {
            if (!File.Exists(_jobsFile))
                return;

            var list = OgmJson.Deserialize<List<JobSummary>>(File.ReadAllText(_jobsFile));
            if (list is null)
                return;

            lock (_gate)
            {
                foreach (var job in list)
                {
                    // Var olan PDF'i bildir; yoksa yalnizca donusturulebilir
                    // girdiler icin tembel uretime izin ver (HasPdf = true).
                    var pdfPath = Path.Combine(_jobsDirectory, job.JobId + ".pdf");
                    var hasPdf = File.Exists(pdfPath);
                    if (!hasPdf)
                    {
                        var splPath = Path.Combine(_jobsDirectory, job.JobId + ".spl");
                        hasPdf = File.Exists(splPath) && JobConverter.CanConvert(splPath);
                    }

                    var dlpAlerts = job.DlpAlerts;
                    if (dlpAlerts is null || dlpAlerts.Count == 0)
                    {
                        var splPath = Path.Combine(_jobsDirectory, job.JobId + ".spl");
                        var alerts = PayloadTextExtractor.AnalyzeDlp(splPath);
                        if (alerts.Count > 0) dlpAlerts = alerts;
                    }

                    _jobs.AddLast(job with { HasPdf = hasPdf, DlpAlerts = dlpAlerts });
                }
            }

            _logger.LogInformation("{Count} is kaydi yuklendi.", list.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Is kayitlari yuklenemedi.");
        }
    }

    // ---- Kalici yazma (debounced / coalesced) ------------------------------

    private void MarkAgentsDirty()
    {
        Interlocked.Exchange(ref _agentsDirty, 1);
        _persistSignals.Writer.TryWrite(0);
    }

    private void MarkJobsDirty()
    {
        Interlocked.Exchange(ref _jobsDirty, 1);
        _persistSignals.Writer.TryWrite(0);
    }

    private async Task PersistLoopAsync()
    {
        try
        {
            while (await _persistSignals.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                // Gelen sinyalleri bosalt.
                while (_persistSignals.Reader.TryRead(out _)) { }

                // Kisa bir gecikmeyle ani (burst) degisiklikleri birlestir.
                await Task.Delay(300).ConfigureAwait(false);
                while (_persistSignals.Reader.TryRead(out _)) { }

                FlushDirty();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Kalici yazma dongusu beklenmedik sekilde durdu.");
        }
    }

    private void FlushDirty()
    {
        if (Interlocked.Exchange(ref _agentsDirty, 0) == 1)
            PersistAgents();

        if (Interlocked.Exchange(ref _jobsDirty, 0) == 1)
            PersistJobs();
    }

    private void PersistAgents()
    {
        try
        {
            var snapshot = _agents.Values.ToList();
            lock (_persistGate)
            {
                AtomicFile.WriteAllText(_agentsFile, OgmJson.Serialize(snapshot));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ajan kayitlari diske yazilamadi.");
        }
    }

    private void PersistJobs()
    {
        List<JobSummary> snapshot;
        lock (_gate)
        {
            snapshot = _jobs.ToList();
        }

        try
        {
            lock (_persistGate)
            {
                AtomicFile.WriteAllText(_jobsFile, OgmJson.Serialize(snapshot));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Is kayitlari diske yazilamadi.");
        }
    }

    /// <summary>Tum is ve ajan verilerini sifirlar (temizler).</summary>
    public void ClearAllData()
    {
        lock (_gate)
        {
            _jobs.Clear();
            _agents.Clear();

            try
            {
                if (Directory.Exists(_jobsDirectory))
                {
                    foreach (var file in Directory.EnumerateFiles(_jobsDirectory))
                    {
                        try { File.Delete(file); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Jobs klasoru temizlenirken hata olustu.");
            }

            // Kullanici istegiyle yapilan islem; temizligi hemen diske yansit.
            Interlocked.Exchange(ref _agentsDirty, 0);
            Interlocked.Exchange(ref _jobsDirty, 0);
            PersistAgents();
            PersistJobs();
        }

        Raise(new ServerEvent(ServerEventKind.StateSnapshot, DateTimeOffset.UtcNow, State: GetState()));
        _logger.LogInformation("Tum sunucu verileri sifirlandi.");
    }

    /// <summary>
    /// Bekleyen kalici yazmalari tamamlar ve arka plan dongusunu durdurur.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _persistSignals.Writer.TryComplete();

        try
        {
            _persistTask.Wait(TimeSpan.FromSeconds(3));
        }
        catch
        {
            // Kapanis sirasinda beklenmeyen hatalar onemsiz.
        }

        // Kapanista son durumu garantiye al.
        PersistAgents();
        PersistJobs();
    }
}
