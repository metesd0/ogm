using System.Text.RegularExpressions;

namespace Ogm.Shared;

/// <summary>
/// Yazdirilan dokumanlarin ayiklanan metinlerinde veri guvenligi (DLP - Data Loss Prevention)
/// taramasi yapar. TC Kimlik No, Kredi Karti, IBAN ve gizli anahtar kelimeleri tespit eder.
/// </summary>
public static class DlpAnalyzer
{
    private static readonly Regex TcRegex = new(@"\b[1-9]\d{10}\b", RegexOptions.Compiled);
    private static readonly Regex IbanRegex = new(@"\bTR\d{2}[\s]?\d{4}[\s]?\d{4}[\s]?\d{4}[\s]?\d{4}[\s]?\d{4}[\s]?\d{2}\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CardRegex = new(@"\b(?:\d[ -]?){13,19}\b", RegexOptions.Compiled);

    private static readonly string[] ConfidentialKeywords =
    [
        "GİZLİ", "ÇOK GİZLİ", "CONFIDENTIAL", "TOP SECRET", "MAAŞ", "BORDRO", "PAROLA", "ŞİFRE", "RESTRICTED", "KVKK"
    ];

    public static IReadOnlyList<string> Analyze(IEnumerable<string> texts)
    {
        var alerts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var text in texts)
        {
            if (string.IsNullOrWhiteSpace(text))
                continue;

            // 1. TC Kimlik Numarasi Denetimi
            foreach (Match match in TcRegex.Matches(text))
            {
                if (IsValidTcKimlik(match.Value))
                {
                    alerts.Add("TC Kimlik No");
                    break;
                }
            }

            // 2. IBAN Denetimi
            if (IbanRegex.IsMatch(text))
            {
                alerts.Add("IBAN Numarası");
            }

            // 3. Kredi Karti Denetimi (Luhn)
            foreach (Match match in CardRegex.Matches(text))
            {
                var cleanDigits = Regex.Replace(match.Value, @"[\s-]", "");
                if (cleanDigits.Length is >= 13 and <= 19 && IsValidLuhn(cleanDigits))
                {
                    alerts.Add("Kredi Kartı");
                    break;
                }
            }

            // 4. Gizli / Hassas Anahtar Kelime Denetimi
            var upper = text.ToUpperInvariant();
            foreach (var kw in ConfidentialKeywords)
            {
                if (upper.Contains(kw, StringComparison.OrdinalIgnoreCase))
                {
                    alerts.Add($"Hassas Belge ({kw})");
                    break;
                }
            }
        }

        return alerts.OrderBy(a => a, StringComparer.Ordinal).ToList();
    }

    private static bool IsValidTcKimlik(string tc)
    {
        if (tc.Length != 11 || tc[0] == '0')
            return false;

        Span<int> digits = stackalloc int[11];
        for (int i = 0; i < 11; i++)
        {
            if (tc[i] < '0' || tc[i] > '9')
                return false;
            digits[i] = tc[i] - '0';
        }

        int oddSum = digits[0] + digits[2] + digits[4] + digits[6] + digits[8];
        int evenSum = digits[1] + digits[3] + digits[5] + digits[7];

        int digit10 = (oddSum * 7 - evenSum) % 10;
        if (digit10 < 0) digit10 += 10;
        if (digits[9] != digit10)
            return false;

        int totalSum = 0;
        for (int i = 0; i < 10; i++)
            totalSum += digits[i];

        return digits[10] == (totalSum % 10);
    }

    private static bool IsValidLuhn(string digits)
    {
        int sum = 0;
        bool alternate = false;

        for (int i = digits.Length - 1; i >= 0; i--)
        {
            int n = digits[i] - '0';
            if (alternate)
            {
                n *= 2;
                if (n > 9)
                    n -= 9;
            }
            sum += n;
            alternate = !alternate;
        }

        return (sum % 10 == 0);
    }
}
