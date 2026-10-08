using System.Reflection;

namespace Ogm.Agent;

/// <summary>
/// Ajanin makineye ozgu kimligi. Kimlik, veri klasorunde saklanir ve
/// uygulama yeniden baslatildiginda korunur.
/// </summary>
public sealed record AgentIdentity(
    string AgentId,
    string MachineName,
    string UserName,
    string OsVersion,
    string AgentVersion)
{
    private const string AgentIdFileName = "agent-id.txt";

    /// <summary>Kimligi veri klasorunden okur; yoksa olusturup diske yazar.</summary>
    public static AgentIdentity LoadOrCreate(string dataDirectory, ILogger logger)
    {
        Directory.CreateDirectory(dataDirectory);
        var idPath = Path.Combine(dataDirectory, AgentIdFileName);

        string agentId;
        try
        {
            agentId = File.Exists(idPath) ? File.ReadAllText(idPath).Trim() : string.Empty;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Ajan kimligi okunamadi, yeni kimlik uretilecek.");
            agentId = string.Empty;
        }

        if (string.IsNullOrWhiteSpace(agentId))
        {
            agentId = Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(idPath, agentId);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Ajan kimligi diske yazilamadi; kimlik yalnizca bellekte kalacak.");
            }
        }

        return new AgentIdentity(
            AgentId: agentId,
            MachineName: Environment.MachineName,
            UserName: SafeUserName(),
            OsVersion: Environment.OSVersion.VersionString,
            AgentVersion: ResolveAgentVersion());
    }

    private static string SafeUserName()
    {
        try
        {
            return Environment.UserName;
        }
        catch
        {
            return "unknown";
        }
    }

    /// <summary>Calisan assembly'nin bilgilendirme surumunu dondurur.</summary>
    public static string ResolveAgentVersion()
    {
        var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();

        var informational = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            // "+<commit>" son ekini temizle.
            var plusIndex = informational.IndexOf('+');
            return plusIndex > 0 ? informational[..plusIndex] : informational;
        }

        return asm.GetName().Version?.ToString() ?? "0.0.0";
    }
}
