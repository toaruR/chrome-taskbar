using System.Diagnostics;
using System.IO;

namespace ChromeTaskbar.App;

internal static class UiaDiagnosticLog
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        string line = FormattableString.Invariant($"{DateTimeOffset.UtcNow:O} appPid={Environment.ProcessId} {message}");
        lock (Gate)
        {
            try
            {
                var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChromeTaskbar");
                Directory.CreateDirectory(directory);
                var file = new FileInfo(Path.Combine(directory, "uia.log"));
                if (file.Exists && file.Length >= MaxBytes)
                    File.Move(file.FullName, file.FullName + ".1", overwrite: true);
                File.AppendAllText(file.FullName, line + Environment.NewLine);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Trace.TraceWarning(line);
            }
        }
    }
}
