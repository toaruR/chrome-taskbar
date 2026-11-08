namespace ChromeTaskbar.UiaWorker;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (!TryParseBrokerArguments(args, out var pipeName, out var epoch))
        {
            return 1;
        }

        // Physical-pixel UIA rectangles so they match GetWindowRect / PrintWindow for favicon capture.
        SetProcessDpiAwarenessContext(-4); // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
        using var server = new PipeServer(pipeName, new ChromeTaskbar.Contracts.WorkerEpoch(epoch));
        await server.RunAsync(CancellationToken.None).ConfigureAwait(false);
        return 0;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(nint value);

    internal static bool TryParseBrokerArguments(string[] args, out string pipeName, out long epoch)
    {
        pipeName = string.Empty;
        epoch = 0;

        if (args.Length != 4 || args[0] != "--pipe" || args[2] != "--epoch" ||
            string.IsNullOrWhiteSpace(args[1]) ||
            !long.TryParse(args[3], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out epoch) ||
            epoch <= 0)
        {
            return false;
        }

        pipeName = args[1];
        return true;
    }
}
