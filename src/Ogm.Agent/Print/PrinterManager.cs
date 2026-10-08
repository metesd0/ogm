using System.Diagnostics;
using Ogm.Agent.Configuration;

namespace Ogm.Agent.Print;

/// <summary>
/// Windows yazici kuyrugu ve ayarlarini yonetir.
///
/// Gorevleri:
/// 1) Sistemdeki tum yazicilarda "Yazdirilan belgeleri sakla" (KeepPrintedJobs)
///    ayarini kullanici mudahalesine gerek kalmadan otomatik olarak acar.
/// 2) Yeni eklenen yazicilari periyodik olarak tespit edip bu ayari onlar icin de acar.
/// 3) Yazdirilmasi tamamlanmis ("Printed" durumundaki) isleri spooler kuyrugundan
///    temizleyerek diskin sismesini engeller.
/// </summary>
public sealed class PrinterManager
{
    private readonly AgentOptions _options;
    private readonly ILogger<PrinterManager> _logger;

    public PrinterManager(AgentOptions options, ILogger<PrinterManager> logger)
    {
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Sistemdeki tum yazicilari kontrol eder; KeepPrintedJobs ayari false olanlari
    /// otomatik olarak true yapar.
    /// </summary>
    public async Task EnsureKeepPrintedJobsAsync(CancellationToken ct = default)
    {
        if (!_options.AutoEnableKeepPrintedJobs)
            return;

        try
        {
            // Yalnizca KeepPrintedJobs = false olan yazicilari bul ve guncelle
            const string script = "Get-Printer | Where-Object { -not $_.KeepPrintedJobs } | " +
                                  "ForEach-Object { Set-Printer -Name $_.Name -KeepPrintedJobs $true; Write-Output $_.Name }";

            var output = await RunPowerShellAsync(script, ct).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(output))
            {
                var printerNames = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var name in printerNames)
                {
                    _logger.LogInformation("Yazicida 'Yazdirilan belgeleri sakla' otomatik aktif edildi: {Printer}", name.Trim());
                }
            }
            else
            {
                _logger.LogDebug("Tum yazicilarda KeepPrintedJobs ayari zaten acik.");
            }
        }
        catch (OperationCanceledException)
        {
            // Iptal edildi
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Yazicilarin KeepPrintedJobs ayari denetlenirken hata olustu.");
        }
    }

    /// <summary>
    /// Yazdirilmasi tamamlanan (Printed durumundaki) isleri spooler kuyrugundan siler.
    /// Boylece diskte eski spool dosyalari (.SPL / .SHD) birikmez.
    /// </summary>
    public async Task CleanupPrintedJobsAsync(CancellationToken ct = default)
    {
        try
        {
            const string script = "Get-Printer | ForEach-Object { " +
                                  "Get-PrintJob -PrinterName $_.Name -ErrorAction SilentlyContinue | " +
                                  "Where-Object { $_.JobStatus -match 'Printed' } | " +
                                  "Remove-PrintJob -ErrorAction SilentlyContinue }";

            await RunPowerShellAsync(script, ct).ConfigureAwait(false);
            _logger.LogDebug("Tamamlanmis yazdirma isleri spooler kuyrugundan temizlendi.");

            // Ek olarak spool klasorunde kalmis cok eski (2 saatten eski) gecici dosyalari temizle
            CleanupOrphanSpoolFiles();
        }
        catch (OperationCanceledException)
        {
            // Iptal edildi
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Spooler kuyrugu temizlenirken hata olustu.");
        }
    }

    /// <summary>
    /// Periyodik bakim dongusu: Yeni yazicilari kontrol eder ve tamamlanan isleri temizler.
    /// </summary>
    public async Task MaintenanceLoopAsync(CancellationToken ct)
    {
        if (_options.PrinterMaintenanceMinutes <= 0)
            return;

        var interval = TimeSpan.FromMinutes(Math.Max(1, _options.PrinterMaintenanceMinutes));

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            await EnsureKeepPrintedJobsAsync(ct).ConfigureAwait(false);
            await CleanupPrintedJobsAsync(ct).ConfigureAwait(false);
        }
    }

    private void CleanupOrphanSpoolFiles()
    {
        try
        {
            if (!Directory.Exists(_options.SpoolDirectory))
                return;

            var threshold = DateTime.UtcNow.AddHours(-2);
            var spoolDir = new DirectoryInfo(_options.SpoolDirectory);

            foreach (var file in spoolDir.EnumerateFiles("*.*"))
            {
                var ext = file.Extension;
                if (!ext.Equals(".SPL", StringComparison.OrdinalIgnoreCase) &&
                    !ext.Equals(".SHD", StringComparison.OrdinalIgnoreCase) &&
                    !ext.Equals(".TMP", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (file.LastWriteTimeUtc < threshold)
                {
                    try
                    {
                        file.Delete();
                        _logger.LogDebug("Eski spool dosyasi temizlendi: {FileName}", file.Name);
                    }
                    catch
                    {
                        // Dosya kilitli veya kullanimda ise atla
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Eski spool dosyalari taranirken hata.");
        }
    }

    private static async Task<string> RunPowerShellAsync(string command, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{command}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);

        return await stdoutTask.ConfigureAwait(false);
    }
}
