using NTwain.Properties;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Threading;

namespace NTwain.Internals
{
   sealed class InternalMessageLoopHook : MessageLoopHook
    {
        Dispatcher _dispatcher;
        WindowsHook _hook;

        internal override void Stop()
        {
            if (_dispatcher != null)
            {
                _dispatcher.InvokeShutdown();
            }
        }
        internal override void Start(IWinMessageFilter filter)
        {
            if (_dispatcher == null)
            {
                // using this hack so the new thread will start running before this function returns
                using (var hack = new WrappedManualResetEvent())
                {
                    var loopThread = new Thread(new ThreadStart(() =>
                    {
                        Debug.WriteLine("NTwain message loop is starting.");
                        _dispatcher = Dispatcher.CurrentDispatcher;
                        if (!Platform.IsOnMono)
                        {
                            _hook = new WindowsHook(filter);
                            Handle = _hook.Handle;
                        }
                        hack.Set();
                        Dispatcher.Run();
                        // if dispatcher shutsdown we'll get here so make everything uninitialized
                        _dispatcher = null;
                        if (_hook != null)
                        {
                            _hook.Dispose();
                            _hook = null;
                            Handle = IntPtr.Zero;
                        }
                    }));
                    loopThread.IsBackground = true;
                    loopThread.SetApartmentState(ApartmentState.STA);
                    loopThread.Start();
                    hack.Wait();
                }
            }
        }

        public override void BeginInvoke(Action action)
        {
            if (_dispatcher == null) { throw new InvalidOperationException(Resources.MsgLoopUnavailble); }

            _dispatcher.BeginInvoke(DispatcherPriority.Normal, action);
        }

        public override void Invoke(Action action)
        {
            if (_dispatcher == null) { throw new InvalidOperationException(Resources.MsgLoopUnavailble); }

            if (_dispatcher.CheckAccess())
            {
                action();
            }
            else if (Platform.IsOnMono)
            {
                using (var man = new WrappedManualResetEvent())
                {
                    _dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
                    {
                        try
                        {
                            action();
                        }
                        finally
                        {
                            man.Set();
                        }
                    }));
                    man.Wait();
                }
            }
            else
            {
                _dispatcher.Invoke(DispatcherPriority.Normal, action);
            }
        }
    }
}
