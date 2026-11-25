using System.IO;
using System.Text;

namespace VoxCable.Platform;

public static class CrashLogger
{
    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VoxCable",
        "logs");

    public static string LogFilePath => Path.Combine(LogDir, "voxcable.log");

    public static void LogError(string source, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            var sb = new StringBuilder();
            sb.AppendLine($"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] ERROR ({source})");
            if (ex != null)
            {
                sb.AppendLine(ex.ToString());
            }
            sb.AppendLine(new string('-', 60));
            File.AppendAllText(LogFilePath, sb.ToString());
        }
        catch
        {
            // Never throw from crash logger
        }
    }
}
