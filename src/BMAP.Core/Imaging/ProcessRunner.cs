using System.Diagnostics;

namespace BMAP.Imaging;

internal static class ProcessRunner
{
    // Drain both pipes while waiting. Reading stderr synchronously before WaitForExit made its timeout unreachable.
    internal static async Task<(int ExitCode, string Output, string Error)> RunAsync(ProcessStartInfo start,
        TimeSpan timeout, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardOutput = start.RedirectStandardError = true;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("外部プログラムを起動できませんでした。");
        Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try { await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { } // Already exited.
            await process.WaitForExitAsync().ConfigureAwait(false);
            await Task.WhenAll(output, error).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException("外部プログラムが制限時間内に応答しませんでした。");
        }
        return (process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
    }
}
