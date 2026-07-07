using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace MultiTerminal.Services.Startup
{
    /// <summary>
    /// Enforces one MultiTerminal instance per interactive Windows session and, on a
    /// duplicate launch, brings the already-running window forward (task 4fec40e2).
    /// <para>
    /// The mutex name uses the <c>Local\</c> prefix deliberately: single-instance is scoped
    /// per session, so fast-user-switching / RDP can each run their own copy. That is the
    /// ratified decision for this ticket. (A cross-session second copy is NOT stopped by the
    /// mutex — it instead collides on the machine-wide :5050 loopback bind and is handled by
    /// the port-contention classifier, which is why both guards exist.)
    /// </para>
    /// <para>
    /// A crashed prior instance releases its mutex handle when its process dies, so the named
    /// object disappears and the next launch cleanly becomes primary — no stale-lock deadlock.
    /// </para>
    /// </summary>
    public sealed class SingleInstanceGuard : IDisposable
    {
        /// <summary>Per-session single-instance mutex name.</summary>
        public const string MutexName = @"Local\MultiTerminal.SingleInstance";

        private Mutex _mutex;
        private bool _disposed;

        /// <summary>True if THIS process acquired the single-instance slot (is the primary instance).</summary>
        public bool IsPrimary { get; private set; }

        private SingleInstanceGuard()
        {
        }

        /// <summary>
        /// Attempt to become the primary instance. On any failure to create the mutex
        /// (which would be unusual), fail OPEN — treat as primary so the app still launches
        /// rather than being wedged shut by a guard problem.
        /// </summary>
        public static SingleInstanceGuard Acquire()
        {
            var guard = new SingleInstanceGuard();
            try
            {
                guard._mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
                guard.IsPrimary = createdNew;
            }
            catch (Exception)
            {
                // Never let the single-instance guard prevent startup on its own error.
                guard.IsPrimary = true;
                guard._mutex = null;
            }

            return guard;
        }

        /// <summary>
        /// Best-effort: find the already-running MultiTerminal window and bring it to the
        /// foreground so a duplicate launch feels like "focus the app I already have open".
        /// Silent no-op if the other instance has no window yet or activation is blocked by
        /// the OS foreground-lock — the caller still shows its "already running" message.
        /// </summary>
        public static void ActivateExistingInstance()
        {
            try
            {
                int currentPid;
                using (var self = Process.GetCurrentProcess())
                {
                    currentPid = self.Id;
                }

                foreach (var proc in Process.GetProcessesByName("MultiTerminal"))
                {
                    try
                    {
                        if (proc.Id == currentPid)
                        {
                            continue;
                        }

                        IntPtr handle = proc.MainWindowHandle;
                        if (handle == IntPtr.Zero)
                        {
                            continue;
                        }

                        if (IsIconic(handle))
                        {
                            ShowWindow(handle, SW_RESTORE);
                        }

                        MultiTerminal.Terminal.NativeMethods.FocusWindow(handle);
                        return;
                    }
                    catch (Exception)
                    {
                        // Ignore per-process access issues and keep scanning.
                    }
                    finally
                    {
                        proc.Dispose();
                    }
                }
            }
            catch (Exception)
            {
                // Activation is a nicety, never a requirement.
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (_mutex != null)
            {
                try
                {
                    if (IsPrimary)
                    {
                        _mutex.ReleaseMutex();
                    }
                }
                catch (Exception)
                {
                    // Releasing a not-owned/abandoned mutex can throw; the handle close below still frees it.
                }

                _mutex.Dispose();
                _mutex = null;
            }
        }

        private const int SW_RESTORE = 9;

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsIconic(IntPtr hWnd);
    }
}
