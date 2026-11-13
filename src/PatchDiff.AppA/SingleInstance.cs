using System;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace PatchDiff;

/// <summary>
/// Lets a second launch hand over to the copy that is already running instead of
/// starting a rival process. The duplicate writes one line to a named pipe and exits;
/// the running instance raises its window and says what happened.
/// </summary>
internal static class SingleInstance
{
    const string PipeName = "PatchDiff.instance.pipe";

    /// <summary>Starts the listener. <paramref name="onShowRequest"/> runs on a background
    /// thread and must marshal to the UI itself.</summary>
    public static void StartServer(Action onShowRequest)
    {
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(
                        PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync().ConfigureAwait(false);
                    using var reader = new StreamReader(server);
                    var request = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (string.Equals(request, "show", StringComparison.Ordinal))
                        onShowRequest();
                }
                catch (Exception)
                {
                    // The pipe disappears while the app is shutting down; stop quietly.
                    if (!Environment.HasShutdownStarted)
                        await Task.Delay(500).ConfigureAwait(false);
                    else
                        return;
                }
            }
        });
    }

    /// <summary>
    /// Asks the running instance to show itself. Returns false when nobody answered, in
    /// which case the caller falls back to a plain notice.
    /// </summary>
    public static bool TrySignalExisting()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(2000);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.WriteLine("show");
            return true;
        }
        catch
        {
            return false;
        }
    }
}
