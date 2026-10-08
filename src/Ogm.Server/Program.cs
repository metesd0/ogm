using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.FileProviders;
using Ogm.Server.Api;
using Ogm.Server.Configuration;
using Ogm.Server.Storage;
using Ogm.Shared;

// Icerik koku, uygulamanin calistigi dizinden bagimsiz olarak exe klasoru
// olsun; boylece wwwroot ve appsettings.json (tek dosya yayinda da) exe'nin
// yanindan okunur. Bu deger yalnizca builder olusturulurken verilebilir;
// sonradan UseContentRoot cagrisi (calisma dizini farkliysa) hata verir.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

// Yazdirilan ham veriler buyuk olabildigi icin istek govdesi sinirini kaldir.
builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.Limits.MaxRequestBodySize = null;
});

builder.Services.Configure<FormOptions>(form =>
{
    form.MultipartBodyLengthLimit = long.MaxValue;
    form.ValueLengthLimit = int.MaxValue;
    form.MultipartHeadersLengthLimit = int.MaxValue;
});

// Enum'lar tel uzerinde isim olarak tasinsin (ajan ile uyum icin).
builder.Services.ConfigureHttpJsonOptions(json =>
{
    json.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

var serverOptions = builder.Configuration
    .GetSection(ServerOptions.SectionName)
    .Get<ServerOptions>() ?? new ServerOptions();

builder.Services.AddSingleton(serverOptions);
builder.Services.AddSingleton<ServerStore>();
builder.Services.AddSingleton<EventHub>();

var app = builder.Build();

// Panel statik dosyalari (wwwroot altindaki index.html, app.js, styles.css).
// Statik web varliklari bildirimine (manifest) bagimli olmadan, dogrudan exe
// yanindaki wwwroot klasoru sunulur. Boylece hem derleme ciktisindan hem de
// single-file yayindan calistirildiginda ayni sekilde davranir.
var webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
if (Directory.Exists(webRoot))
{
    var fileProvider = new PhysicalFileProvider(webRoot);
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = fileProvider });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = fileProvider });
}
else
{
    app.Logger.LogWarning("Panel klasoru bulunamadi: {WebRoot}", webRoot);
}

// Depodaki her degisikligi canli akisa (SSE) bagla. Olaylar artimsaldir:
// yalnizca degisen varlik (ajan veya is) gonderilir; tam durum yalnizca
// baglanti kurulurken anlik goruntu olarak itilir.
var store = app.Services.GetRequiredService<ServerStore>();
var hub = app.Services.GetRequiredService<EventHub>();
store.Changed += serverEvent => hub.Publish(serverEvent);

app.MapGet("/health", () => Results.Ok(new { status = "ok", utc = DateTimeOffset.UtcNow }));

app.MapOgmApi();

app.Run();
