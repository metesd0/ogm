using System.Security.Cryptography;
using System.Text;

namespace Ogm.Shared;

/// <summary>
/// Diske guvenli (atomik) yazma yardimcilari.
///
/// Tum yazma islemleri once ayni klasordeki gecici bir dosyaya yapilir, ardindan
/// tek bir atomik tasima (<see cref="File.Move(string, string, bool)"/>) ile
/// hedefe alinir. Boylece uygulama yazma sirasinda cokse bile yarim/bozuk bir
/// hedef dosya olusmaz; hedef ya eski ya da yeni tam icerige sahip olur.
///
/// Bu sinif ayrica buyuk dosyalari belleğe tamamen yuklemeden kopyalayip ayni
/// anda SHA-256 hesaplayabilir (streaming).
/// </summary>
public static class AtomicFile
{
    private const int BufferSize = 1 << 16; // 64 KiB

    /// <summary>Baytlari atomik olarak dosyaya yazar (varsa uzerine yazar).</summary>
    public static void WriteAllBytes(string path, byte[] bytes)
    {
        var temp = CreateTempPath(path);
        try
        {
            EnsureDirectory(path);
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    /// <summary>Metni UTF-8 olarak atomik sekilde dosyaya yazar.</summary>
    public static void WriteAllText(string path, string text)
        => WriteAllBytes(path, Encoding.UTF8.GetBytes(text));

    /// <summary>Metni UTF-8 olarak atomik sekilde diske yazar (asenkron).</summary>
    public static Task WriteAllTextAsync(string path, string text, CancellationToken ct)
        => WriteAllBytesAsync(path, Encoding.UTF8.GetBytes(text), ct);

    /// <summary>Baytlari atomik sekilde diske yazar (asenkron).</summary>
    public static async Task WriteAllBytesAsync(string path, byte[] bytes, CancellationToken ct)
    {
        var temp = CreateTempPath(path);
        try
        {
            EnsureDirectory(path);
            await using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
            {
                await fs.WriteAsync(bytes.AsMemory(), ct).ConfigureAwait(false);
                await fs.FlushAsync(ct).ConfigureAwait(false);
            }

            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    /// <summary>
    /// Kaynagi belleğe tamamen yuklemeden hedefe kopyalar ve ayni anda
    /// SHA-256 karmasini hesaplar. Donen deger: (yazilan bayt sayisi, kucuk harf hex sha256).
    /// </summary>
    public static async Task<(long Bytes, string Sha256)> CopyAndHashAsync(
        Stream source,
        string destinationPath,
        CancellationToken ct)
    {
        var temp = CreateTempPath(destinationPath);
        long total = 0;
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        try
        {
            EnsureDirectory(destinationPath);

            await using (var dest = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
            {
                var buffer = new byte[BufferSize];
                int read;
                while ((read = await source.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
                {
                    await dest.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    hasher.AppendData(buffer, 0, read);
                    total += read;
                }

                await dest.FlushAsync(ct).ConfigureAwait(false);
            }

            File.Move(temp, destinationPath, overwrite: true);
            return (total, Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant());
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private static void EnsureDirectory(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
    }

    private static string CreateTempPath(string path)
        => path + ".tmp-" + Guid.NewGuid().ToString("N");

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Gecici dosya temizligi en iyi caba ile yapilir.
        }
    }
}
