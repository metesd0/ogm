using System.Text;

namespace Ogm.Agent.Print;

/// <summary>
/// Bir yazdirma isinin spool golge (.SHD) dosyasindan cikarilan meta veri.
/// </summary>
public sealed record SpoolJobInfo(
    string? PrinterName,
    string? DocumentName,
    string? UserName,
    string? DataType);

/// <summary>
/// Windows spooler'in urettigi ikili .SHD (golge) dosyasindan meta veriyi cikarir.
/// UTF-16LE ve ASCII metinleri hizalamayi ve Turkce karakterleri gozeterek ayristirir.
/// </summary>
public static class SpoolShdParser
{
    private const int MinStringLength = 3;
    private const int MaxStrings = 32;
    private const int MaxFieldLength = 260;

    public static SpoolJobInfo Parse(ReadOnlySpan<byte> shd, string? fallbackDocumentName = null)
    {
        try
        {
            // Hem çift hem tek bayt hizalamasini dene, temiz okunabilir dizeleri al
            var stringsEven = ExtractStrings(shd, 0);
            var stringsOdd = ExtractStrings(shd, 1);
            var fields = stringsEven.Count >= stringsOdd.Count ? stringsEven : stringsOdd;

            string? document = null;
            string? printer = null;
            string? user = null;

            // Tipik SHD yapisi: Belge adi, Yazici adi, Kullanici adi sirasiyla gelir
            foreach (var s in fields)
            {
                var norm = Normalize(s);
                if (norm is null) continue;

                if (document is null)
                {
                    document = norm;
                }
                else if (printer is null)
                {
                    printer = norm;
                }
                else if (user is null)
                {
                    user = norm;
                    break;
                }
            }

            if (string.IsNullOrWhiteSpace(document) || !IsValidHumanString(document))
                document = Normalize(fallbackDocumentName);

            if (string.IsNullOrWhiteSpace(printer) || !IsValidHumanString(printer))
                printer = null;

            if (string.IsNullOrWhiteSpace(user) || !IsValidHumanString(user))
                user = null;

            return new SpoolJobInfo(printer, document, user, DataType: null);
        }
        catch
        {
            return new SpoolJobInfo(null, Normalize(fallbackDocumentName), null, null);
        }
    }

    private static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        if (trimmed.Length > MaxFieldLength)
            trimmed = trimmed[..MaxFieldLength];

        return trimmed;
    }

    private static bool IsValidHumanString(string s)
    {
        if (string.IsNullOrWhiteSpace(s) || s.Length < 2)
            return false;

        foreach (var c in s)
        {
            if (!IsPrintable(c))
                return false;
        }

        return true;
    }

    private static List<string> ExtractStrings(ReadOnlySpan<byte> data, int startOffset)
    {
        var results = new List<string>();
        var sb = new StringBuilder();

        for (int i = startOffset; i + 1 < data.Length; i += 2)
        {
            char c = (char)(data[i] | (data[i + 1] << 8));

            if (IsPrintable(c))
            {
                sb.Append(c);
                if (sb.Length > 1024)
                    sb.Clear();
            }
            else
            {
                if (sb.Length >= MinStringLength)
                {
                    var str = sb.ToString().Trim();
                    if (str.Length >= MinStringLength && IsValidHumanString(str))
                        results.Add(str);
                }

                sb.Clear();

                if (results.Count >= MaxStrings)
                    break;
            }
        }

        if (sb.Length >= MinStringLength)
        {
            var str = sb.ToString().Trim();
            if (str.Length >= MinStringLength && IsValidHumanString(str))
                results.Add(str);
        }

        return results;
    }

    private static bool IsPrintable(char c)
    {
        // ASCII standart karakterler (boşluk dahil)
        if (c >= ' ' && c <= '~')
            return true;

        // Türkçe ve Latin Genişletilmiş karakterler (ç, ğ, ı, ö, ş, ü, Ç, Ğ, İ, Ö, Ş, Ü vb.)
        if (c >= 0x00C0 && c <= 0x024F)
            return true;

        return false;
    }
}
