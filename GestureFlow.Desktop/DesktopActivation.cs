using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace GestureFlow.Desktop;

internal static class DesktopActivation
{
    private const byte ShowWindow = 1;

    public static string CurrentSessionPipeName
    {
        get
        {
            using var current = Process.GetCurrentProcess();
            return $"GestureFlow.Desktop.Activate.{current.SessionId}";
        }
    }

    public static async Task ListenAsync(string name, Action activate, CancellationToken cancellation)
    {
        var signal = new byte[1];
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                using var server = new NamedPipeServerStream(name, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(cancellation).ConfigureAwait(false);
                try
                {
                    if (await server.ReadAsync(signal, cancellation).ConfigureAwait(false) == 1 && signal[0] == ShowWindow)
                        activate();
                }
                catch (IOException)
                {
                    // A client that exits before sending a signal does not stop the listener.
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Desktop activation listener stopped: {0}", error.Message);
        }
    }

    public static void Request(string name)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            pipe.Connect(2500);
            if (GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var processId))
                AllowSetForegroundWindow(processId);

            pipe.WriteByte(ShowWindow);
        }
        catch (Exception error) when (error is IOException or TimeoutException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Could not request Desktop activation: {0}", error.Message);
        }
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
