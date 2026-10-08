using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http.Features;
using Ogm.Server.Configuration;
using Ogm.Server.Storage;
using Ogm.Shared;

namespace Ogm.Server.Api;

/// <summary>
/// Sunucunun HTTP uç noktalarini tanimlar.
///
/// Ajanlari hedefleyen uç noktalar (register/heartbeat/jobs) API anahtari ile
/// korunur. Paneli besleyen uç noktalar (state/events/jobs) DashboardPassword
/// tanimlanmissa kimlik dogrulama ile korunur; bos ise LAN'da serbest calisir.
/// </summary>
public static class OgmApi
{
    private const string DashboardAuthHeader = "X-Ogm-Dashboard-Auth";
    private const string DashboardCookieName = "ogm_dash_token";

    public static void MapOgmApi(this WebApplication app)
    {
        // ---- Kimlik Dogrulama (Web Paneli) --------------------------------
        app.MapGet("/api/auth/status", (HttpContext ctx, ServerOptions options) =>
        {
            var required = !string.IsNullOrWhiteSpace(options.DashboardPassword);
            var authorized = IsDashboardAuthorized(ctx, options);
            return Results.Ok(new AuthStatusResponse(required, authorized));
        });

        app.MapPost("/api/auth/login", (HttpContext ctx, LoginRequest req, ServerOptions options) =>
        {
            if (string.IsNullOrWhiteSpace(options.DashboardPassword))
            {
                return Results.Ok(new LoginResponse(true, "open", "Sifre korumasi kapali."));
            }

            if (!VerifyDashboardPassword(req.Password, options.DashboardPassword))
            {
                return Results.Json(new LoginResponse(false, null, "Hatali sifre."), statusCode: StatusCodes.Status401Unauthorized);
            }

            var token = ComputeDashboardToken(options.DashboardPassword);
            ctx.Response.Cookies.Append(DashboardCookieName, token, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                MaxAge = TimeSpan.FromDays(7)
            });

            return Results.Ok(new LoginResponse(true, token, "Giris basarili."));
        });

        app.MapPost("/api/auth/logout", (HttpContext ctx) =>
        {
            ctx.Response.Cookies.Delete(DashboardCookieName);
            return Results.Ok();
        });

        // ---- Ajan kapisi: kayit -------------------------------------------
        app.MapPost(OgmProtocol.Routes.Register,
            (HttpContext ctx, RegisterRequest request, ServerStore store, ServerOptions options) =>
            {
                if (!IsAuthorized(ctx, options))
                    return Results.Unauthorized();

                var ip = ctx.Connection.RemoteIpAddress?.ToString();
                store.RegisterOrUpdate(request, ip);

                var response = new RegisterResponse(
                    Accepted: true,
                    ProtocolVersion: OgmProtocol.Version,
                    HeartbeatSeconds: options.HeartbeatSeconds,
                    ServerTimeUtc: DateTimeOffset.UtcNow,
                    Message: "kayit kabul edildi");

                return Results.Ok(response);
            });

        // ---- Ajan kapisi: heartbeat ---------------------------------------
        app.MapPost(OgmProtocol.Routes.Heartbeat,
            (HttpContext ctx, HeartbeatRequest heartbeat, ServerStore store, ServerOptions options) =>
            {
                if (!IsAuthorized(ctx, options))
                    return Results.Unauthorized();

                store.ApplyHeartbeat(heartbeat, ctx.Connection.RemoteIpAddress?.ToString());
                return Results.Ok();
            });

        // ---- Ajan kapisi: is yukleme (multipart/form-data) ---------------
        app.MapPost(OgmProtocol.Routes.UploadJob,
                async (HttpContext ctx, ServerStore store, ServerOptions options) =>
                {
                    if (!IsAuthorized(ctx, options))
                        return Results.Unauthorized();

                    if (!ctx.Request.HasFormContentType)
                        return Results.BadRequest(new JobAck(string.Empty, false, "multipart/form-data bekleniyor"));

                    var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);

                    var metaJson = form[OgmProtocol.FormMetaField].ToString();
                    if (string.IsNullOrWhiteSpace(metaJson))
                        return Results.BadRequest(new JobAck(string.Empty, false, "meta alani eksik"));

                    var meta = OgmJson.Deserialize<PrintJobMeta>(metaJson);
                    if (meta is null)
                        return Results.BadRequest(new JobAck(string.Empty, false, "meta cozumlenemedi"));

                    var file = form.Files[OgmProtocol.FormPayloadField];
                    if (file is null)
                        return Results.BadRequest(new JobAck(meta.JobId, false, "payload alani eksik"));

                    // Boyut siniri: diski/belleği tikayan dev dosyalari erken reddet.
                    if (options.MaxJobBytes > 0 && file.Length > options.MaxJobBytes)
                    {
                        return Results.Json(
                            new JobAck(meta.JobId, false,
                                $"Is cok buyuk: {file.Length} bayt (sinir {options.MaxJobBytes} bayt)"),
                            statusCode: StatusCodes.Status413PayloadTooLarge);
                    }

                    // Icerigi belleğe tamamen yuklemeden dogrudan diske akit.
                    await using var payloadStream = file.OpenReadStream();
                    await store.AddJobAsync(meta, payloadStream, ctx.RequestAborted);

                    return Results.Ok(new JobAck(meta.JobId, true, "kaydedildi"));
                })
            .DisableAntiforgery();

        // ---- Panel: durum anlik goruntusu ---------------------------------
        app.MapGet(OgmProtocol.Routes.State, (HttpContext ctx, ServerStore store, ServerOptions options) =>
        {
            if (!IsDashboardAuthorized(ctx, options))
                return Results.Unauthorized();

            return Results.Ok(store.GetState());
        });

        // ---- Panel: canli akis (Server-Sent Events) -----------------------
        app.MapGet(OgmProtocol.Routes.Events,
            async (HttpContext ctx, EventHub hub, ServerStore store, ServerOptions options, CancellationToken ct) =>
            {
                if (!IsDashboardAuthorized(ctx, options))
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }

                ctx.Response.Headers.ContentType = "text/event-stream; charset=utf-8";
                ctx.Response.Headers.CacheControl = "no-cache";
                ctx.Response.Headers.Connection = "keep-alive";
                ctx.Response.Headers["X-Accel-Buffering"] = "no";

                var (subscriptionId, reader) = hub.Subscribe();

                try
                {
                    // Baglanti kurulur kurulmaz tam durum gonderilir.
                    await WriteEventAsync(ctx, new ServerEvent(ServerEventKind.Snapshot, DateTimeOffset.UtcNow, store.GetState()), ct);

                    await foreach (var serverEvent in reader.ReadAllAsync(ct))
                        await WriteEventAsync(ctx, serverEvent, ct);
                }
                catch (OperationCanceledException)
                {
                    // Istemci baglantiyi kapatti.
                }
                finally
                {
                    hub.Unsubscribe(subscriptionId);
                }
            });

        // ---- Panel: isin ham verisini indir -------------------------------
        app.MapGet("/api/jobs/{jobId}/payload", (string jobId, HttpContext ctx, ServerStore store, ServerOptions options) =>
        {
            if (!IsDashboardAuthorized(ctx, options))
                return Results.Unauthorized();

            var path = store.GetPayloadPath(jobId);
            return path is null
                ? Results.NotFound()
                : Results.File(path, "application/octet-stream", jobId + ".spl");
        });

        // ---- Panel: isin PDF hali (tarayicida dogrudan goruntuleme) -------
        app.MapGet("/api/jobs/{jobId}/pdf", (string jobId, HttpContext ctx, ServerStore store, ServerOptions options) =>
        {
            if (!IsDashboardAuthorized(ctx, options))
                return Results.Unauthorized();

            var pdfPath = store.GetPdfPath(jobId);
            if (pdfPath is null || !File.Exists(pdfPath))
                return Results.NotFound();

            return Results.File(pdfPath, "application/pdf", enableRangeProcessing: true);
        });

        // ---- Panel: isin PDF olarak indirilmesi --------------------------
        app.MapGet("/api/jobs/{jobId}/download-pdf", (string jobId, HttpContext ctx, ServerStore store, ServerOptions options) =>
        {
            if (!IsDashboardAuthorized(ctx, options))
                return Results.Unauthorized();

            var pdfPath = store.GetPdfPath(jobId);
            if (pdfPath is null || !File.Exists(pdfPath))
                return Results.NotFound();

            var summary = store.GetState().Jobs.FirstOrDefault(j => j.JobId == jobId);
            var safeName = !string.IsNullOrWhiteSpace(summary?.DocumentName)
                ? Path.GetFileNameWithoutExtension(summary.DocumentName)
                : jobId;

            return Results.File(pdfPath, "application/pdf", safeName + ".pdf");
        });

        // ---- Panel: isin onizleme, metin ve DLP ozeti --------------------
        app.MapGet("/api/jobs/{jobId}/preview", (string jobId, HttpContext ctx, ServerStore store, ServerOptions options) =>
        {
            if (!IsDashboardAuthorized(ctx, options))
                return Results.Unauthorized();

            var path = store.GetPayloadPath(jobId);
            if (path is null || !File.Exists(path))
                return Results.NotFound();

            try
            {
                var preview = GeneratePayloadPreview(path, jobId);
                return Results.Ok(preview);
            }
            catch (Exception ex)
            {
                return Results.Problem($"Önizleme oluşturulamadı: {ex.Message}");
            }
        });

        // ---- Panel: sunucu sistem bilgisi ve ag adresleri -----------------
        app.MapGet("/api/server/info", (HttpContext ctx, ServerOptions options) =>
        {
            var localIps = GetLocalIpAddresses();
            var process = System.Diagnostics.Process.GetCurrentProcess();
            var uptime = DateTimeOffset.UtcNow - process.StartTime.ToUniversalTime();

            return Results.Ok(new
            {
                version = "1.0.0",
                serverTimeUtc = DateTimeOffset.UtcNow,
                uptimeSeconds = (long)uptime.TotalSeconds,
                localIps,
                heartbeatSeconds = options.HeartbeatSeconds,
                onlineThresholdSeconds = options.OnlineThresholdSeconds,
                maxJobsRetained = options.MaxJobsRetained,
                authRequired = !string.IsNullOrWhiteSpace(options.DashboardPassword)
            });
        });
    }

    private static JobPreview GeneratePayloadPreview(string path, string jobId)
    {
        var fi = new FileInfo(path);
        var totalBytes = fi.Length;
        var maxRead = (int)Math.Min(totalBytes, 64 * 1024);
        var buffer = new byte[maxRead];

        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            var read = fs.Read(buffer, 0, maxRead);
            if (read < maxRead)
                Array.Resize(ref buffer, read);
        }

        var format = DetectFormat(buffer);
        var extracted = PayloadTextExtractor.ExtractPrintableStrings(buffer);
        var hexDump = GenerateHexDump(buffer, Math.Min(buffer.Length, 512));
        var dlpAlerts = DlpAnalyzer.Analyze(extracted);

        return new JobPreview(jobId, totalBytes, format, extracted, hexDump, dlpAlerts.Count > 0 ? dlpAlerts : null);
    }

    private static string DetectFormat(byte[] data)
    {
        if (data.Length >= 4)
        {
            if (data.Length >= 8 && data[0] == 0x89 && data[1] == 0x50 &&
                data[2] == 0x4E && data[3] == 0x47)
                return "PNG Görüntü";

            if (data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
                return "JPEG Görüntü";

            if (data.Length >= 6 && data[0] == 0x47 && data[1] == 0x49 &&
                data[2] == 0x46 && data[3] == 0x38)
                return "GIF Görüntü";

            if (data[0] == 0x42 && data[1] == 0x4D)
                return "BMP Görüntü";

            if (data[0] == 0x50 && data[1] == 0x4B && data[2] == 0x03 && data[3] == 0x04)
                return "XPS / OpenXPS (Paket)";

            if (data[0] == 0x25 && data[1] == 0x50 && data[2] == 0x44 && data[3] == 0x46)
                return "PDF Dokümanı";

            if (data[0] == 0x01 && data[1] == 0x00 && data[2] == 0x00 && data[3] == 0x00)
            {
                if (data.Length >= 44 && Encoding.ASCII.GetString(data, 40, 4) == " EMF")
                    return "Windows EMF (Gelişmiş Meta Dosyası)";
                return "Windows EMF / GDI Çıktısı";
            }
        }

        var ascii = Encoding.ASCII.GetString(data, 0, Math.Min(data.Length, 256));
        if (ascii.StartsWith("%PDF-", StringComparison.OrdinalIgnoreCase))
            return "PDF Dokümanı";
        if (ascii.Contains("%!PS", StringComparison.OrdinalIgnoreCase))
            return "PostScript (PS)";
        if (ascii.Contains("@PJL", StringComparison.OrdinalIgnoreCase) || ascii.Contains("\x1B%-12345X", StringComparison.OrdinalIgnoreCase))
            return "HP PCL / PJL Yazıcı Komutu";
        if (ascii.Contains("^XA", StringComparison.OrdinalIgnoreCase))
            return "Zebra ZPL Barkod / Etiket";
        if (data.Length > 2 && data[0] == 0x1B && data[1] == 0x40)
            return "ESC/POS Fiş / Termal Çıktı";

        return "Ham Yazıcı Verisi (RAW / SPL)";
    }

    private static string GenerateHexDump(byte[] data, int length)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < length; i += 16)
        {
            sb.AppendFormat("{0:X8}  ", i);
            int chunkSize = Math.Min(16, length - i);

            for (int j = 0; j < 16; j++)
            {
                if (j < chunkSize)
                    sb.AppendFormat("{0:X2} ", data[i + j]);
                else
                    sb.Append("   ");
                if (j == 7) sb.Append(' ');
            }

            sb.Append(" |");
            for (int j = 0; j < chunkSize; j++)
            {
                byte b = data[i + j];
                sb.Append(b >= 32 && b <= 126 ? (char)b : '.');
            }
            sb.Append("|\n");
        }
        return sb.ToString();
    }

    private static List<string> GetLocalIpAddresses()
    {
        var ips = new List<string>();
        try
        {
            foreach (var iface in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (iface.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up ||
                    iface.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                    continue;

                foreach (var addr in iface.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    {
                        var s = addr.Address.ToString();
                        if (!s.StartsWith("169.254.") && !ips.Contains(s))
                            ips.Add(s);
                    }
                }
            }
        }
        catch { }
        return ips;
    }

    private static async Task WriteEventAsync(HttpContext ctx, ServerEvent serverEvent, CancellationToken ct)
    {
        var json = OgmJson.Serialize(serverEvent);
        await ctx.Response.WriteAsync($"data: {json}\n\n", ct);
        await ctx.Response.Body.FlushAsync(ct);
    }

    private static bool IsAuthorized(HttpContext ctx, ServerOptions options)
    {
        if (string.IsNullOrEmpty(options.ApiKey))
            return true;

        if (!ctx.Request.Headers.TryGetValue(OgmProtocol.ApiKeyHeader, out var provided))
            return false;

        var providedBytes = Encoding.UTF8.GetBytes(provided.ToString());
        var expectedBytes = Encoding.UTF8.GetBytes(options.ApiKey);
        return CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes);
    }

    private static bool IsDashboardAuthorized(HttpContext ctx, ServerOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.DashboardPassword))
            return true;

        var expectedToken = ComputeDashboardToken(options.DashboardPassword);

        // 1. Header kontrolu
        if (ctx.Request.Headers.TryGetValue(DashboardAuthHeader, out var authHeader))
        {
            var val = authHeader.ToString();
            if (val.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                val = val[7..].Trim();
            if (val == expectedToken || VerifyDashboardPassword(val, options.DashboardPassword))
                return true;
        }

        // 2. Cookie kontrolu
        if (ctx.Request.Cookies.TryGetValue(DashboardCookieName, out var cookieVal))
        {
            if (cookieVal == expectedToken)
                return true;
        }

        // 3. Query string kontrolu (SSE EventSource icin)
        if (ctx.Request.Query.TryGetValue("token", out var queryToken))
        {
            var val = queryToken.ToString();
            if (val == expectedToken || VerifyDashboardPassword(val, options.DashboardPassword))
                return true;
        }

        return false;
    }

    private static string ComputeDashboardToken(string password)
    {
        var input = Encoding.UTF8.GetBytes(password + "-ogm-dash-salt");
        var hash = SHA256.HashData(input);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool VerifyDashboardPassword(string provided, string expected)
    {
        var provBytes = Encoding.UTF8.GetBytes(provided);
        var expBytes = Encoding.UTF8.GetBytes(expected);
        return CryptographicOperations.FixedTimeEquals(provBytes, expBytes);
    }
}
