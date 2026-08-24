using System.Threading;
using System.Windows.Threading;
using NUnit.Framework;

namespace Aerochat.VisualShell.Tests;

internal static class WpfTestHost
{
    private static readonly Dispatcher Dispatcher = StartDispatcher();

    private static Dispatcher StartDispatcher()
    {
        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "Aerochat WPF test host"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();

        return dispatcher!;
    }

    public static void Run(Action action)
    {
        Exception? failure = null;
        void InvokeAction()
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        }

        if (Dispatcher.CheckAccess())
            InvokeAction();
        else
            Dispatcher.Invoke(InvokeAction);

        if (failure is not null)
            throw new AssertionException(failure.ToString());
    }
}
