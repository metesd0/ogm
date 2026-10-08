using System.Diagnostics;
using System.Runtime.InteropServices;
using Ogm.Agent.Configuration;

namespace Ogm.Agent.Print;

/// <summary>
/// Windows yazici kuyrugu ve ayarlarini yonetir.
///
/// Gorevleri:
/// 1) Sistemdeki tum yazicilarda "Yazdirilan belgeleri sakla" (KeepPrintedJobs)
///    ayarini native Win32 Spooler API (winspool.drv) ile 0ms gecikmeyle ve
///    harici powershell sureci baslatmadan acar (EDR/Antivirus dostu).
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
    /// native Win32 API ile otomatik olarak true yapar.
    /// </summary>
    public async Task EnsureKeepPrintedJobsAsync(CancellationToken ct = default)
    {
        if (!_options.AutoEnableKeepPrintedJobs)
            return;

        try
        {
            var updated = EnsureKeepPrintedJobsNative();
            if (updated > 0)
            {
                _logger.LogInformation("{Count} yazicida 'Yazdirilan belgeleri sakla' basariyla aktif edildi (Native Win32).", updated);
                return;
            }
            _logger.LogDebug("Tum yazicilarda KeepPrintedJobs ayari zaten acik.");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Native Win32 ile KeepPrintedJobs ayari yapilamadi, PowerShell deneniyor.");
            await FallbackKeepPrintedJobsPowerShellAsync(ct).ConfigureAwait(false);
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
            var deleted = CleanupPrintedJobsNative();
            if (deleted > 0)
            {
                _logger.LogDebug("{Count} tamamlanmis yazdirma isi kuyruktan temizlendi (Native Win32).", deleted);
            }

            // Ek olarak spool klasorunde kalmis cok eski (2 saatten eski) gecici dosyalari temizle
            CleanupOrphanSpoolFiles();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Native Win32 kuyruk temizligi yapilamadi, PowerShell deneniyor.");
            await FallbackCleanupPowerShellAsync(ct).ConfigureAwait(false);
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

    private int EnsureKeepPrintedJobsNative()
    {
        int updatedCount = 0;
        int flags = WinSpoolApi.PRINTER_ENUM_LOCAL | WinSpoolApi.PRINTER_ENUM_CONNECTIONS;
        WinSpoolApi.EnumPrinters(flags, null, 2, IntPtr.Zero, 0, out int bytesNeeded, out _);
        if (bytesNeeded <= 0) return 0;

        IntPtr pPrinters = Marshal.AllocHGlobal(bytesNeeded);
        try
        {
            if (!WinSpoolApi.EnumPrinters(flags, null, 2, pPrinters, bytesNeeded, out _, out int count))
                return 0;

            int structSize = Marshal.SizeOf<WinSpoolApi.PRINTER_INFO_2>();
            for (int i = 0; i < count; i++)
            {
                IntPtr pCurrent = IntPtr.Add(pPrinters, i * structSize);
                var info = Marshal.PtrToStructure<WinSpoolApi.PRINTER_INFO_2>(pCurrent);
                if (string.IsNullOrEmpty(info.pPrinterName)) continue;

                if ((info.Attributes & WinSpoolApi.PRINTER_ATTRIBUTE_KEEPPRINTEDJOBS) == 0)
                {
                    var defaults = new WinSpoolApi.PRINTER_DEFAULTS
                    {
                        DesiredAccess = WinSpoolApi.PRINTER_ALL_ACCESS
                    };

                    if (WinSpoolApi.OpenPrinter(info.pPrinterName, out IntPtr hPrinter, ref defaults))
                    {
                        try
                        {
                            info.Attributes |= WinSpoolApi.PRINTER_ATTRIBUTE_KEEPPRINTEDJOBS;
                            Marshal.StructureToPtr(info, pCurrent, fDeleteOld: false);
                            if (WinSpoolApi.SetPrinter(hPrinter, 2, pCurrent, 0))
                            {
                                updatedCount++;
                                _logger.LogInformation("Yazicida KeepPrintedJobs aktif edildi: {Printer}", info.pPrinterName);
                            }
                        }
                        finally
                        {
                            WinSpoolApi.ClosePrinter(hPrinter);
                        }
                    }
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(pPrinters);
        }

        return updatedCount;
    }

    private int CleanupPrintedJobsNative()
    {
        int deletedCount = 0;
        int flags = WinSpoolApi.PRINTER_ENUM_LOCAL | WinSpoolApi.PRINTER_ENUM_CONNECTIONS;
        WinSpoolApi.EnumPrinters(flags, null, 2, IntPtr.Zero, 0, out int bytesNeeded, out _);
        if (bytesNeeded <= 0) return 0;

        IntPtr pPrinters = Marshal.AllocHGlobal(bytesNeeded);
        try
        {
            if (!WinSpoolApi.EnumPrinters(flags, null, 2, pPrinters, bytesNeeded, out _, out int count))
                return 0;

            int structSize = Marshal.SizeOf<WinSpoolApi.PRINTER_INFO_2>();
            for (int i = 0; i < count; i++)
            {
                IntPtr pCurrent = IntPtr.Add(pPrinters, i * structSize);
                var info = Marshal.PtrToStructure<WinSpoolApi.PRINTER_INFO_2>(pCurrent);
                if (string.IsNullOrEmpty(info.pPrinterName)) continue;

                var defaults = new WinSpoolApi.PRINTER_DEFAULTS
                {
                    DesiredAccess = WinSpoolApi.PRINTER_ALL_ACCESS
                };

                if (WinSpoolApi.OpenPrinter(info.pPrinterName, out IntPtr hPrinter, ref defaults))
                {
                    try
                    {
                        WinSpoolApi.EnumJobs(hPrinter, 0, 100, 2, IntPtr.Zero, 0, out int jobsBytesNeeded, out _);
                        if (jobsBytesNeeded > 0)
                        {
                            IntPtr pJobs = Marshal.AllocHGlobal(jobsBytesNeeded);
                            try
                            {
                                if (WinSpoolApi.EnumJobs(hPrinter, 0, 100, 2, pJobs, jobsBytesNeeded, out _, out int jobCount))
                                {
                                    int jobStructSize = Marshal.SizeOf<WinSpoolApi.JOB_INFO_2>();
                                    for (int j = 0; j < jobCount; j++)
                                    {
                                        IntPtr pJobCurrent = IntPtr.Add(pJobs, j * jobStructSize);
                                        var jobInfo = Marshal.PtrToStructure<WinSpoolApi.JOB_INFO_2>(pJobCurrent);

                                        bool isPrinted = (jobInfo.Status & (WinSpoolApi.JOB_STATUS_PRINTED | WinSpoolApi.JOB_STATUS_COMPLETE)) != 0
                                                         || (jobInfo.pStatus != null && jobInfo.pStatus.Contains("Printed", StringComparison.OrdinalIgnoreCase));

                                        if (isPrinted)
                                        {
                                            if (WinSpoolApi.SetJob(hPrinter, jobInfo.JobId, 0, IntPtr.Zero, WinSpoolApi.JOB_CONTROL_DELETE))
                                            {
                                                deletedCount++;
                                            }
                                        }
                                    }
                                }
                            }
                            finally
                            {
                                Marshal.FreeHGlobal(pJobs);
                            }
                        }
                    }
                    finally
                    {
                        WinSpoolApi.ClosePrinter(hPrinter);
                    }
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(pPrinters);
        }

        return deletedCount;
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

    private async Task FallbackKeepPrintedJobsPowerShellAsync(CancellationToken ct)
    {
        try
        {
            const string script = "Get-Printer | Where-Object { -not $_.KeepPrintedJobs } | " +
                                  "ForEach-Object { Set-Printer -Name $_.Name -KeepPrintedJobs $true }";
            await RunPowerShellAsync(script, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PowerShell KeepPrintedJobs yedek calistirmasi basarisiz.");
        }
    }

    private async Task FallbackCleanupPowerShellAsync(CancellationToken ct)
    {
        try
        {
            const string script = "Get-Printer | ForEach-Object { " +
                                  "Get-PrintJob -PrinterName $_.Name -ErrorAction SilentlyContinue | " +
                                  "Where-Object { $_.JobStatus -match 'Printed' } | " +
                                  "Remove-PrintJob -ErrorAction SilentlyContinue }";
            await RunPowerShellAsync(script, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PowerShell kuyruk temizleme yedek calistirmasi basarisiz.");
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
