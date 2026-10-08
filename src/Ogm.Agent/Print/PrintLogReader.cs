using System.Diagnostics.Eventing.Reader;
using System.Xml;

namespace Ogm.Agent.Print;

/// <summary>
/// Windows PrintService Operational Event Log (Microsoft-Windows-PrintService/Operational)
/// uzerinden Event ID 307 kayitlarini sorgular.
///
/// Event 307 parametreleri:
///   Param1: Is Kimligi (Job ID)
///   Param2: Belge Adi (Document Name)
///   Param3: Kullanici (User Name)
///   Param5: Yazici Adi (Printer Name)
///   Param7: Bayt Boyutu
///   Param8: Toplam Sayfa Sayisi (Total Pages)
/// </summary>
public static class PrintLogReader
{
    private const string LogName = "Microsoft-Windows-PrintService/Operational";

    public static (int? TotalPages, string? DocumentName, string? UserName) TryGetRecentJobInfo(string? printerName, TimeSpan maxAge)
    {
        try
        {
            var ms = Math.Max(1000, (int)maxAge.TotalMilliseconds);
            var queryText = $"*[System[EventID=307 and TimeCreated[timediff(@SystemTime) <= {ms}]]]";
            var query = new EventLogQuery(LogName, PathType.LogName, queryText)
            {
                ReverseDirection = true
            };

            using var reader = new EventLogReader(query);
            for (var record = reader.ReadEvent(); record != null; record = reader.ReadEvent())
            {
                using (record)
                {
                    var xml = record.ToXml();
                    var doc = new XmlDocument();
                    doc.LoadXml(xml);

                    var nsmgr = new XmlNamespaceManager(doc.NameTable);
                    nsmgr.AddNamespace("e", "http://schemas.microsoft.com/win/2004/08/events/event");

                    var evtPrinter = doc.SelectSingleNode("//e:Data[@Name='Param5']", nsmgr)?.InnerText;
                    if (!string.IsNullOrEmpty(printerName) && !string.IsNullOrEmpty(evtPrinter) &&
                        !string.Equals(printerName, evtPrinter, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var docName = doc.SelectSingleNode("//e:Data[@Name='Param2']", nsmgr)?.InnerText;
                    var user = doc.SelectSingleNode("//e:Data[@Name='Param3']", nsmgr)?.InnerText;
                    var pagesStr = doc.SelectSingleNode("//e:Data[@Name='Param8']", nsmgr)?.InnerText;

                    int? pages = int.TryParse(pagesStr, out var p) && p > 0 ? p : null;

                    return (pages, docName, user);
                }
            }
        }
        catch
        {
            // Event log aktif degilse veya izin yoksa sessizce devam et
        }

        return (null, null, null);
    }
}
