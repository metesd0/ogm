using Syncfusion.Pdf;
using Syncfusion.XPS;

namespace Ogm.Server.Storage;

/// <summary>
/// Gelen yazdirma verilerini (.SPL) PDF formatina donusturen gelismis donusturucu.
///
/// Desteklenen formatlar:
/// 1) Dogrudan PDF (%PDF-)
/// 2) XPS / OpenXPS (PK.. zip basligi)
/// 3) Windows EMF / WMF (Enhanced Metafile)
/// 4) Raster Gorseller (PNG, JPEG, BMP, GIF)
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

    /// <summary>Veri Windows EMF (Enhanced Metafile) mi?</summary>
    public static bool IsEmf(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 44 && data[0] == 0x01 && data[1] == 0x00 && data[2] == 0x00 && data[3] == 0x00)
        {
            // Offset 40'ta ' EMF' (0x20, 0x45, 0x4D, 0x46) basligi kontrolu
            return data[40] == 0x20 && data[41] == 0x45 && data[42] == 0x4D && data[43] == 0x46;
        }
        return false;
    }

    /// <summary>Veri yaygin raster gorsel formati mi (PNG, JPEG, BMP, GIF)?</summary>
    public static bool IsRasterImage(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 4)
        {
            if (data.Length >= 8 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47) return true; // PNG
            if (data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF) return true; // JPEG
            if (data[0] == 0x42 && data[1] == 0x4D) return true; // BMP
            if (data.Length >= 6 && data[0] == 0x47 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x38) return true; // GIF
        }
        return false;
    }

    /// <summary>
    /// Verilen ham veri basliklarina gore PDF'e donusturulebilir mi?
    /// </summary>
    public static bool CanConvert(ReadOnlySpan<byte> header)
        => IsPdf(header) || IsXps(header) || IsEmf(header) || IsRasterImage(header);

    /// <summary>
    /// Dosyanin PDF'e donusturulup donusturulemeyecegini yalnizca ilk 64 bayti
    /// okuyarak belirler (buyuk dosyayi belleğe yuklemez).
    /// </summary>
    public static bool CanConvert(string sourcePath)
    {
        try
        {
            if (!File.Exists(sourcePath))
                return false;

            Span<byte> header = stackalloc byte[64];
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
    /// Verilen dosyanin PDF formatina donusturulmesini dener.
    /// Basarili olursa true dondurur.
    /// </summary>
    public static bool TryConvertToPdf(string sourceSplPath, string targetPdfPath, ILogger? logger = null)
    {
        try
        {
            if (!File.Exists(sourceSplPath))
                return false;

            var header = new byte[64];
            using (var fsCheck = new FileStream(sourceSplPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var read = fsCheck.Read(header, 0, header.Length);
                if (read < 4)
                    return false;
                if (read < header.Length)
                    Array.Resize(ref header, read);
            }

            var span = header.AsSpan();

            // 1. Zaten dogrudan PDF ise kopyala
            if (IsPdf(span))
            {
                File.Copy(sourceSplPath, targetPdfPath, overwrite: true);
                logger?.LogInformation("Kaynak dosya dogrudan PDF, kopyalandi: {Target}", targetPdfPath);
                return true;
            }

            // 2. XPS / OpenXPS ise Syncfusion XPS converter ile donustur
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

            // 3. Windows EMF ise yuksek cozunurluklu render ile PDF olustur
            if (OperatingSystem.IsWindows() && IsEmf(span))
            {
                return ConvertEmfToPdf(sourceSplPath, targetPdfPath, logger);
            }

            // 4. Raster gorseller (PNG/JPEG/BMP/GIF) ise PDF olustur
            if (IsRasterImage(span))
            {
                return ConvertRasterImageToPdf(sourceSplPath, targetPdfPath, logger);
            }

            return false;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "PDF donusturme basarisiz oldu: {Source}", sourceSplPath);
            return false;
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool ConvertEmfToPdf(string sourceSplPath, string targetPdfPath, ILogger? logger)
    {
        try
        {
            using var metafile = new System.Drawing.Imaging.Metafile(sourceSplPath);
            int width = metafile.Width > 0 ? metafile.Width : 800;
            int height = metafile.Height > 0 ? metafile.Height : 1100;

            // Vektorel netlik icin olcek belirle (maksimum boyut 2400px civari)
            float maxDim = Math.Max(width, height);
            float scale = Math.Clamp(2400f / maxDim, 1.2f, 4f);
            int bmpW = Math.Max(100, (int)(width * scale));
            int bmpH = Math.Max(100, (int)(height * scale));

            using var bitmap = new System.Drawing.Bitmap(bmpW, bmpH);
            using (var g = System.Drawing.Graphics.FromImage(bitmap))
            {
                g.Clear(System.Drawing.Color.White);
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.DrawImage(metafile, 0, 0, bmpW, bmpH);
            }

            using var ms = new MemoryStream();
            bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            ms.Position = 0;

            using var pdfDoc = new PdfDocument();
            using var pdfBitmap = new Syncfusion.Pdf.Graphics.PdfBitmap(ms);

            var page = pdfDoc.Pages.Add();
            page.Graphics.DrawImage(pdfBitmap, 0, 0, page.Graphics.ClientSize.Width, page.Graphics.ClientSize.Height);

            using var outFs = new FileStream(targetPdfPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            pdfDoc.Save(outFs);

            logger?.LogInformation("EMF ciktisi basariyla PDF'e donusturuldu: {Target} ({Size} bayt)",
                targetPdfPath, new FileInfo(targetPdfPath).Length);
            return true;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "EMF to PDF donusturme hatasi: {Source}", sourceSplPath);
            return false;
        }
    }

    private static bool ConvertRasterImageToPdf(string sourceSplPath, string targetPdfPath, ILogger? logger)
    {
        try
        {
            using var fs = new FileStream(sourceSplPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var pdfDoc = new PdfDocument();
            using var pdfBitmap = new Syncfusion.Pdf.Graphics.PdfBitmap(fs);

            var page = pdfDoc.Pages.Add();
            page.Graphics.DrawImage(pdfBitmap, 0, 0, page.Graphics.ClientSize.Width, page.Graphics.ClientSize.Height);

            using var outFs = new FileStream(targetPdfPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            pdfDoc.Save(outFs);

            logger?.LogInformation("Gorsel ciktisi basariyla PDF'e donusturuldu: {Target}", targetPdfPath);
            return true;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Gorsel to PDF donusturme hatasi: {Source}", sourceSplPath);
            return false;
        }
    }
}
