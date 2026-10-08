using Syncfusion.Pdf;
using Syncfusion.XPS;

namespace Ogm.Server.Storage;

/// <summary>
/// Gelen yazdirma verilerini (.SPL) PDF formatina donusturen donusturucu.
///
/// Windows Spooler ciktilari tipik olarak:
/// 1) XPS / OpenXPS (PK.. zip basligi)
/// 2) Dogrudan PDF (%PDF- basligi)
/// 3) EMF / GDI veya RAW yazici komutlari
///
/// Onemli: Yalnizca gercekten PDF'e cevrilebilen girdiler (dogrudan PDF veya
/// XPS) donusturulur. Gorsel/EMF/RAW/PCL/ZPL gibi diger ciktilar PDF'e
/// cevrilmez; oldugu gibi saklanir ve ham veri olarak sunulur. Donusturme
/// tembel (lazy) calisir: yalnizca panel kullanicisi PDF goruntulemek
/// istediginde, talep uzerine yapilir.
/// </summary>
public static class JobConverter
{
    private static readonly byte[] PdfMagic = " %PDF-"u8.ToArray()[1..]; // 0x25, 0x50, 0x44, 0x46
    private static readonly byte[] ZipMagic = [0x50, 0x4B, 0x03, 0x04];

    /// <summary>Veri dogrudan PDF mi?</summary>
    public static bool IsPdf(ReadOnlySpan<byte> data)
        => data.Length >= 4 && data.StartsWith(PdfMagic);

    /// <summary>Veri XPS / OpenXPS (OPC zip) mi?</summary>
    public static bool IsXps(ReadOnlySpan<byte> data)
        => data.Length >= 4 && data.StartsWith(ZipMagic);

    /// <summary>
    /// Verilen ham veri basliklarina gore PDF'e donusturulebilir mi?
    /// Yalnizca dogrudan PDF veya XPS icin true doner.
    /// </summary>
    public static bool CanConvert(ReadOnlySpan<byte> header)
        => IsPdf(header) || IsXps(header);

    /// <summary>
    /// Dosyanin PDF'e donusturulup donusturulemeyecegini yalnizca ilk baytlari
    /// okuyarak belirler (buyuk dosyayi belleğe yuklemez).
    /// </summary>
    public static bool CanConvert(string sourcePath)
    {
        try
        {
            if (!File.Exists(sourcePath))
                return false;

            Span<byte> header = stackalloc byte[8];
            using var fs = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var read = fs.Read(header);
            return read >= 4 && CanConvert(header[..read]);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Verilen dosyanin PDF formatina donusturulmesini dener. Yalnizca
    /// dogrudan PDF veya XPS girdiler donusturulur; aksi halde false doner.
    /// Basarili olursa true dondurur.
    /// </summary>
    public static bool TryConvertToPdf(string sourceSplPath, string targetPdfPath, ILogger? logger = null)
    {
        try
        {
            if (!File.Exists(sourceSplPath))
                return false;

            var header = new byte[8];
            using (var fsCheck = new FileStream(sourceSplPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var read = fsCheck.Read(header, 0, header.Length);
                if (read < 4)
                    return false;
            }

            var span = header.AsSpan();

            // 1. Zaten dogrudan PDF ise dogrudan kopyala.
            if (IsPdf(span))
            {
                File.Copy(sourceSplPath, targetPdfPath, overwrite: true);
                logger?.LogInformation("Kaynak dosya dogrudan PDF, kopyalandi: {Target}", targetPdfPath);
                return true;
            }

            // 2. XPS / OpenXPS ise Syncfusion converter ile PDF yap.
            if (IsXps(span))
            {
                var converter = new XPSToPdfConverter();
                using var fs = new FileStream(sourceSplPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var doc = converter.Convert(fs);
                using var outFs = new FileStream(targetPdfPath, FileMode.Create, FileAccess.Write, FileShare.Read);
                doc.Save(outFs);

                logger?.LogInformation("XPS ciktisi basariyla PDF'e donusturuldu: {Target} ({Size} bayt)",
                    targetPdfPath, new FileInfo(targetPdfPath).Length);
                return true;
            }

            // 3. Diger tum formatlar (gorsel/EMF/RAW/PCL/ZPL) donusturulmez.
            return false;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "PDF donusturme basarisiz oldu: {Source}", sourceSplPath);
            return false;
        }
    }
}
