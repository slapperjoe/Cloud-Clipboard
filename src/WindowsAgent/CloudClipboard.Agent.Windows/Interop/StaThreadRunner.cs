using System;
using System.Threading;

namespace CloudClipboard.Agent.Windows.Interop;

public static class StaThreadRunner
{
    public static void Run(Action action)
    {
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                exception = ex;
            }
        })
        {
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (exception is not null)
        {
            throw exception;
        }
    }

    public static T Run<T>(Func<T> func)
    {
        T result = default!;
        Run(() =>
        {
            result = func();
        });
        return result;
    }
}
