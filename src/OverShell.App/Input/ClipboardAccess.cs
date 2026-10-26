using System.Runtime.InteropServices;

namespace OverShell.App.Input;

internal static class ClipboardAccess
{
    /// <summary>Retry contention without blocking the dispatcher. The caller's STA context is kept.</summary>
    public static async Task<T> RetryAsync<T>(Func<T> action)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return action(); }
            catch (ExternalException) when (attempt < 3)
            {
                await Task.Delay(25 * (attempt + 1));
            }
        }
    }
}
