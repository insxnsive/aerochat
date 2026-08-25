using System.Diagnostics;

namespace Aerochat.Connectivity.Auth;

public sealed class ShellBrowserLauncher : IBrowserLauncher
{
    private readonly Func<ProcessStartInfo, Process?> _start;

    public ShellBrowserLauncher() : this(Process.Start)
    {
    }

    public ShellBrowserLauncher(Func<ProcessStartInfo, Process?> start)
    {
        _start = start ?? throw new ArgumentNullException(nameof(start));
    }

    public void Open(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        using Process? process = _start(
            new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        if (process is null)
            throw new AuthException("The system browser could not be opened.");
    }
}
