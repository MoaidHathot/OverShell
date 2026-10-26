namespace OverShell.App.Input;

/// <summary>UI-thread clipboard operations stay ordered across OLE reentrancy and retries.</summary>
internal sealed class ClipboardWorkQueue
{
    public Task Completion { get; private set; } = Task.CompletedTask;

    public void Enqueue(Func<Task> action, Action<Exception> failed) => Completion = RunAsync(Completion, action, failed);

    private static async Task RunAsync(Task previous, Func<Task> action, Action<Exception> failed)
    {
        // Publish the entry before an OLE call can pump another UI message.
        await Task.Yield();
        try
        {
            await previous;
            await action();
        }
        catch (Exception e)
        {
            // A failed clipboard provider must not poison every later copy/paste.
            failed(e);
        }
    }
}
