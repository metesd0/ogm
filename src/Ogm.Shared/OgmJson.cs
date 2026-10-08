using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ogm.Shared;

/// <summary>
/// Proje genelinde kullanilan JSON serilestirme ayarlari ve yardimci metodlar.
/// Tum tel protokolu bu ayarlarla uyumlu olmalidir.
/// </summary>
public static class OgmJson
{
    /// <summary>Paylasilan serilestirme ayarlari (camelCase, web varsayilanlari).</summary>
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = false,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        // Enum'lar tel uzerinde sayisal degil, isim olarak tasinir (surum uyumu kolaylasir).
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    public static T? Deserialize<T>(ReadOnlySpan<byte> utf8Json)
        => JsonSerializer.Deserialize<T>(utf8Json, Options);

    public static ValueTask<T?> DeserializeAsync<T>(Stream stream, CancellationToken ct)
        => JsonSerializer.DeserializeAsync<T>(stream, Options, ct);
}
