using System.Diagnostics;
using System.IO;
using ChromeTaskbar.Contracts;

namespace ChromeTaskbar.UiaWorker;

internal readonly record struct WorkerRequestDiagnostic(int ChromePid, WorkerEpoch Epoch, ulong RequestId)
{
    internal T Measure<T>(string stage, Func<T> run)
    {
        Write(stage, "start");
        long start = Stopwatch.GetTimestamp();
        try { return run(); }
        finally { Write(stage, FormattableString.Invariant($"end elapsedMs={Stopwatch.GetElapsedTime(start).TotalMilliseconds:F1}")); }
    }

    private void Write(string stage, string transition)
    {
        try
        {
            Console.Error.WriteLine(FormattableString.Invariant(
                $"{Protocol.WorkerDiagnosticPrefix}{DateTimeOffset.UtcNow:O} workerPid={Environment.ProcessId} epoch={Epoch.Value} requestId={RequestId} chromePid={ChromePid} {stage} {transition}"));
        }
        catch (IOException) { } // diagnostics are best effort during pipe closure
    }
}
