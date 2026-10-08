using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.ServiceProcess;
using Ogm.Agent.Configuration;

namespace Ogm.Agent.Installer;

/// <summary>
/// Ajan uygulamasinin tek bir exe cift-tiklamasiyla kurulmasini,
/// yonetilmesini veya kaldirilmasini saglayan entegre yukleyici.
/// </summary>
public static class AgentInstaller
{
    public const string ServiceName = "OgmPrintAgent";
    public const string DisplayName = "Ogm Print Agent";
    public const string Description = "Yazici islerini yakalayip merkezi sunucuya gonderir.";

    public static void HandleInteractive(string[] args, AgentOptions options)
    {
        // 1. Yonetici (Administrator) yetkisi kontrolu
        if (!IsAdministrator())
        {
            if (RelaunchElevated(args))
                return;

            NativeGui.Error(
                "Ogm Ajan kurulumu icin Yonetici (Administrator) yetkisi gereklidir.\n" +
                "Lutfen uygulamaya sag tiklayip 'Yonetici olarak calistir' secin.",
                "Yetki Hatasi");
            return;
        }

        // 2. Servis zaten kurulu mu kontrol et
        var existingService = GetService();

        if (existingService != null)
        {
            var statusStr = existingService.Status == ServiceControllerStatus.Running
                ? "Calisiyor"
                : existingService.Status.ToString();

            var result = NativeGui.QuestionYesNoCancel(
                $"Ogm Ajan servisi bu bilgisayarda zaten kurulu.\n\n" +
                $"Durum: {statusStr}\n\n" +
                $"• Servisi YENIDEN BASLATMAK icin [Evet]\n" +
                $"• Servisi BILGISAYARDAN KALDIRMAK icin [Hayir]\n" +
                $"• Cikmak icin [Iptal] secin.",
                "Ogm Ajan Yonetimi");

            if (result == NativeGui.IDYES)
            {
                RestartService();
                NativeGui.Info("Ogm Ajan servisi basariyla yeniden baslatildi.", "Bilgi");
            }
            else if (result == NativeGui.IDNO)
            {
                UninstallService();
                NativeGui.Info("Ogm Ajan servisi bilgisayardan basariyla kaldirildi.", "Bilgi");
            }

            return;
        }

        // 3. Servis kurulu degilse kurulumu gerceklestir
        try
        {
            var installDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Ogm", "Agent");

            Directory.CreateDirectory(installDir);

            var currentExe = Environment.ProcessPath;
            var targetExe = Path.Combine(installDir, "Ogm.Agent.exe");

            // Exe'yi Program Files altina kopyala (USB'den veya Downloads'tan calistirildiysa dosya orada guvende kalsin)
            if (!string.IsNullOrEmpty(currentExe) &&
                !string.Equals(currentExe, targetExe, StringComparison.OrdinalIgnoreCase) &&
                File.Exists(currentExe))
            {
                File.Copy(currentExe, targetExe, overwrite: true);
            }
            else
            {
                targetExe = currentExe ?? targetExe;
            }

            // appsettings.json varsa onu da Program Files altina tasi
            var sourceConfig = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            var targetConfig = Path.Combine(installDir, "appsettings.json");

            if (File.Exists(sourceConfig) && !string.Equals(sourceConfig, targetConfig, StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(sourceConfig, targetConfig, overwrite: true);
            }

            // Windows Servisini olustur
            RunCmd("sc.exe", $"create \"{ServiceName}\" binPath= \"\\\"{targetExe}\\\"\" start= auto DisplayName= \"{DisplayName}\"");
            RunCmd("sc.exe", $"description \"{ServiceName}\" \"{Description}\"");
            RunCmd("sc.exe", $"failure \"{ServiceName}\" reset= 86400 actions= restart/5000/restart/5000/restart/5000");

            // Windows PrintService Operasyonel logunu aktif et (Event ID 307 icin)
            RunCmd("wevtutil.exe", "set-log \"Microsoft-Windows-PrintService/Operational\" /enabled:true");

            // Sistemdeki yazicilarda KeepPrintedJobs ayarini otomatik ac
            RunPowerShell("Get-Printer | Where-Object { -not $_.KeepPrintedJobs } | Set-Printer -KeepPrintedJobs $true");

            // Servisi baslat
            RunCmd("sc.exe", $"start \"{ServiceName}\"");

            NativeGui.Info(
                "Ogm Yazici Takip Ajani basariyla kuruldu ve arka planda baslatildi!\n\n" +
                "• Bilgisayar her acildiginda otomatik ve gorunmez olarak calisacaktir.\n" +
                "• Yazici ciktilari otomatik takip edilip sunucuya aktarilacaktir.\n\n" +
                $"Kurulum Klasoru: {installDir}\n" +
                $"Hedef Sunucu: {options.ServerUrl}",
                "Kurulum Basarili");
        }
        catch (Exception ex)
        {
            NativeGui.Error($"Kurulum sirasinda bir hata olustu:\n\n{ex.Message}", "Kurulum Hatasi");
        }
    }

    public static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static bool RelaunchElevated(string[] args)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
            return false;

        var startInfo = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = string.Join(" ", args),
            UseShellExecute = true,
            Verb = "runas"
        };

        try
        {
            Process.Start(startInfo);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static ServiceController? GetService()
    {
        try
        {
            return ServiceController.GetServices()
                .FirstOrDefault(s => string.Equals(s.ServiceName, ServiceName, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return null;
        }
    }

    private static void RestartService()
    {
        RunCmd("sc.exe", $"stop \"{ServiceName}\"");
        Thread.Sleep(2000);
        RunCmd("sc.exe", $"start \"{ServiceName}\"");
    }

    private static void UninstallService()
    {
        RunCmd("sc.exe", $"stop \"{ServiceName}\"");
        Thread.Sleep(1500);
        RunCmd("sc.exe", $"delete \"{ServiceName}\"");
    }

    private static void RunCmd(string exe, string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        using var process = Process.Start(psi);
        process?.WaitForExit(10000);
    }

    private static void RunPowerShell(string command)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{command}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        using var process = Process.Start(psi);
        process?.WaitForExit(15000);
    }
}

internal static class NativeGui
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

    public const uint MB_OK = 0x00000000;
    public const uint MB_YESNOCANCEL = 0x00000003;
    public const uint MB_ICONINFORMATION = 0x00000040;
    public const uint MB_ICONERROR = 0x00000010;
    public const uint MB_ICONQUESTION = 0x00000020;

    public const int IDOK = 1;
    public const int IDCANCEL = 2;
    public const int IDYES = 6;
    public const int IDNO = 7;

    public static void Info(string text, string title = "Ogm Ajan")
        => MessageBox(IntPtr.Zero, text, title, MB_OK | MB_ICONINFORMATION);

    public static void Error(string text, string title = "Ogm Ajan - Hata")
        => MessageBox(IntPtr.Zero, text, title, MB_OK | MB_ICONERROR);

    public static int QuestionYesNoCancel(string text, string title = "Ogm Ajan")
        => MessageBox(IntPtr.Zero, text, title, MB_YESNOCANCEL | MB_ICONQUESTION);
}
