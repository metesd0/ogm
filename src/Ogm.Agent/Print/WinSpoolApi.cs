using System.Runtime.InteropServices;

namespace Ogm.Agent.Print;

/// <summary>
/// Windows Spooler API (winspool.drv) P/Invoke tanimlari.
/// PowerShell calistirmak yerine dogrudan Windows API uzerinden
/// yazicilari sorgular, KeepPrintedJobs ayarlarini acar ve tamamlanan
/// isleri temizler.
/// </summary>
internal static class WinSpoolApi
{
    public const int PRINTER_ENUM_LOCAL = 0x00000002;
    public const int PRINTER_ENUM_CONNECTIONS = 0x00000004;
    public const uint PRINTER_ATTRIBUTE_KEEPPRINTEDJOBS = 0x00000100;

    public const int PRINTER_ALL_ACCESS = 0xF000C;
    public const int PRINTER_ACCESS_ADMINISTER = 0x00000004;

    public const int JOB_STATUS_PRINTED = 0x00000080;
    public const int JOB_STATUS_COMPLETE = 0x00001000;
    public const int JOB_CONTROL_DELETE = 5;

    [DllImport("winspool.drv", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern bool OpenPrinter(string pPrinterName, out IntPtr phPrinter, ref PRINTER_DEFAULTS pDefault);

    [DllImport("winspool.drv", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern bool OpenPrinter(string pPrinterName, out IntPtr phPrinter, IntPtr pDefault);

    [DllImport("winspool.drv", SetLastError = true)]
    public static extern bool ClosePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern bool GetPrinter(IntPtr hPrinter, int dwLevel, IntPtr pPrinter, int cbBuf, out int pcbNeeded);

    [DllImport("winspool.drv", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern bool SetPrinter(IntPtr hPrinter, int dwLevel, IntPtr pPrinter, int dwCommand);

    [DllImport("winspool.drv", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern bool EnumPrinters(int flags, string? name, int level, IntPtr pPrinterEnum, int cbBuf, out int pcbNeeded, out int pcReturned);

    [DllImport("winspool.drv", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern bool EnumJobs(IntPtr hPrinter, int firstJob, int noJobs, int level, IntPtr pJob, int cbBuf, out int pcbNeeded, out int pcReturned);

    [DllImport("winspool.drv", SetLastError = true)]
    public static extern bool SetJob(IntPtr hPrinter, int jobId, int level, IntPtr pJob, int command);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    public struct PRINTER_DEFAULTS
    {
        public IntPtr pDatatype;
        public IntPtr pDevMode;
        public int DesiredAccess;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    public struct PRINTER_INFO_2
    {
        public string? pServerName;
        public string? pPrinterName;
        public string? pShareName;
        public string? pPortName;
        public string? pDriverName;
        public string? pComment;
        public string? pLocation;
        public IntPtr pDevMode;
        public string? pSepFile;
        public string? pPrintProcessor;
        public string? pDatatype;
        public string? pParameters;
        public IntPtr pSecurityDescriptor;
        public uint Attributes;
        public uint Priority;
        public uint DefaultPriority;
        public uint StartTime;
        public uint UntilTime;
        public uint Status;
        public uint cJobs;
        public uint AveragePPM;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    public struct JOB_INFO_2
    {
        public int JobId;
        public string? pPrinterName;
        public string? pMachineName;
        public string? pUserName;
        public string? pDocument;
        public string? pNotifyName;
        public string? pDatatype;
        public string? pPrintProcessor;
        public string? pParameters;
        public string? pDriverName;
        public IntPtr pDevMode;
        public string? pStatus;
        public IntPtr pSecurityDescriptor;
        public uint Status;
        public uint Priority;
        public uint Position;
        public uint StartTime;
        public uint UntilTime;
        public uint TotalPages;
        public uint Size;
        public short wYear;
        public short wMonth;
        public short wDayOfWeek;
        public short wDay;
        public short wHour;
        public short wMinute;
        public short wSecond;
        public short wMilliseconds;
        public uint Time;
        public uint PagesPrinted;
    }
}
