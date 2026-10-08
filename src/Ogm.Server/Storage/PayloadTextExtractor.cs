using System.Text;
using Ogm.Shared;

namespace Ogm.Server.Storage;

public static class PayloadTextExtractor
{
    public static IReadOnlyList<string> AnalyzeDlp(string payloadPath)
    {
        try
        {
            if (!File.Exists(payloadPath))
                return [];

            var maxRead = 64 * 1024;
            var buffer = new byte[maxRead];
            int read;
            using (var fs = new FileStream(payloadPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                read = fs.Read(buffer, 0, maxRead);
            }

            if (read <= 0)
                return [];

            if (read < buffer.Length)
                Array.Resize(ref buffer, read);

            var strings = ExtractPrintableStrings(buffer);
            return DlpAnalyzer.Analyze(strings);
        }
        catch
        {
            return [];
        }
    }

    public static IReadOnlyList<string> ExtractPrintableStrings(byte[] data)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1) UTF-16LE tara (Windows EMF / Unicode dizeler)
        var utf16Chars = new StringBuilder();
        for (int i = 0; i < data.Length - 1; i += 2)
        {
            byte b1 = data[i];
            byte b2 = data[i + 1];
            if (b2 == 0 && b1 >= 32 && b1 <= 126)
            {
                utf16Chars.Append((char)b1);
            }
            else
            {
                if (utf16Chars.Length >= 4)
                {
                    var s = utf16Chars.ToString().Trim();
                    if (s.Length >= 4 && !seen.Contains(s) && !IsBinaryJunk(s))
                    {
                        seen.Add(s);
                        result.Add(s);
                        if (result.Count >= 30) break;
                    }
                }
                utf16Chars.Clear();
            }
        }

        // 2) ASCII / 8-bit tara
        if (result.Count < 30)
        {
            var asciiChars = new StringBuilder();
            for (int i = 0; i < data.Length; i++)
            {
                byte b = data[i];
                if (b >= 32 && b <= 126)
                {
                    asciiChars.Append((char)b);
                }
                else
                {
                    if (asciiChars.Length >= 4)
                    {
                        var s = asciiChars.ToString().Trim();
                        if (s.Length >= 4 && !seen.Contains(s) && !IsBinaryJunk(s))
                        {
                            seen.Add(s);
                            result.Add(s);
                            if (result.Count >= 40) break;
                        }
                    }
                    asciiChars.Clear();
                }
            }
        }

        return result;
    }

    public static bool IsBinaryJunk(string s)
    {
        int lettersOrDigits = 0;
        foreach (var c in s)
        {
            if (char.IsLetterOrDigit(c) || c == ' ' || c == '.' || c == '-' || c == '_')
                lettersOrDigits++;
        }
        return lettersOrDigits < (s.Length * 0.7);
    }
}
