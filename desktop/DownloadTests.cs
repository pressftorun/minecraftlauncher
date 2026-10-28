namespace CreateLauncher;

internal static class DownloadTests
{
    sealed class SlowStream(byte[] bytes, int delay) : MemoryStream(bytes)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellation = default)
        {
            await Task.Delay(delay, cancellation);
            return await base.ReadAsync(buffer[..Math.Min(buffer.Length, 1)], cancellation);
        }
    }

    public static async Task Run()
    {
        using var source = new SlowStream(new byte[10], 80);
        using var destination = new MemoryStream();
        var progress = new List<long>();
        await LauncherService.CopyDownload(source, destination, 10, (bytes, _) => progress.Add(bytes),
            inactivityTimeout: TimeSpan.FromMilliseconds(400));
        if (destination.Length != 10 || progress.Count < 3 || progress[0] != 0 || progress[^1] != 10)
            throw new Exception("Slow active download must finish with visible progress even when total duration exceeds the inactivity timeout");
        using var stalled = new SlowStream([1], 500);
        try
        {
            await LauncherService.CopyDownload(stalled, destination, 1, null, inactivityTimeout: TimeSpan.FromMilliseconds(50));
            throw new Exception("Stalled download was not stopped");
        }
        catch (TimeoutException) { }
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            await LauncherService.CopyDownload(source, destination, 10, null, cancelled.Token);
            throw new Exception("Cancelled download succeeded");
        }
        catch (OperationCanceledException) { }
        using var truncated = new MemoryStream([1]);
        try
        {
            await LauncherService.CopyDownload(truncated, destination, 2, null);
            throw new Exception("Truncated download succeeded");
        }
        catch (IOException) { }
        Console.WriteLine("PASS: slow active downloads, progress, stalled connections, cancellation and truncated files");
    }
}
