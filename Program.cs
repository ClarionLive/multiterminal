using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using MultiTerminal.Services;
using MultiTerminal.Services.Startup;

namespace MultiTerminal
{
    /// <summary>
    /// Application entry point.
    /// </summary>
    static class Program
    {
        [DllImport("ntdll.dll", SetLastError = true)]
        private static extern int RtlGetVersion(ref OSVERSIONINFOEX versionInfo);

        [StructLayout(LayoutKind.Sequential)]
        private struct OSVERSIONINFOEX
        {
            public int dwOSVersionInfoSize;
            public int dwMajorVersion;
            public int dwMinorVersion;
            public int dwBuildNumber;
            public int dwPlatformId;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szCSDVersion;
            public short wServicePackMajor;
            public short wServicePackMinor;
            public short wSuiteMask;
            public byte wProductType;
            public byte wReserved;
        }

        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main()
        {
            // Check Windows version for ConPTY support using RtlGetVersion (accurate on all Windows versions)
            var osInfo = new OSVERSIONINFOEX { dwOSVersionInfoSize = Marshal.SizeOf(typeof(OSVERSIONINFOEX)) };
            RtlGetVersion(ref osInfo);

            // ConPTY requires Windows 10 build 17763 (version 1809) or later
            // Windows 11 is build 22000+
            if (osInfo.dwMajorVersion < 10 || (osInfo.dwMajorVersion == 10 && osInfo.dwBuildNumber < 17763))
            {
                MessageBox.Show(
                    $"MultiTerminal requires Windows 10 version 1809 or later.\n\n" +
                    $"Detected: Windows {osInfo.dwMajorVersion}.{osInfo.dwMinorVersion} Build {osInfo.dwBuildNumber}\n\n" +
                    "ConPTY (Pseudo Console) API is not available on this Windows version.",
                    "Unsupported Windows Version",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return;
            }

            // Single-instance guard (task 4fec40e2): acquire a per-session (Local\) mutex
            // BEFORE any UI. If another MultiTerminal already owns this session's slot,
            // bring its window forward and exit cleanly rather than starting a second host
            // that would only collide on :5050. Held for the whole process; released on exit.
            var singleInstance = SingleInstanceGuard.Acquire();
            if (!singleInstance.IsPrimary)
            {
                SingleInstanceGuard.ActivateExistingInstance();
                singleInstance.Dispose();
                MessageBox.Show(
                    "MultiTerminal is already running in this session.\n\n" +
                    "Switch to the existing window — this launch will now close.",
                    "MultiTerminal",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // Push WorktreeMode setting into the process env var BEFORE anything reads
            // WorktreeConfig.IsEnabled (it resolves once, on first static access — first
            // touch is MessageBroker construction inside MainForm). Externally-set env
            // var wins so a system override remains a kill switch.
            string existingWorktreeEnv = Environment.GetEnvironmentVariable(WorktreeConfig.ModeEnvVar);
            if (string.IsNullOrWhiteSpace(existingWorktreeEnv))
            {
                Environment.SetEnvironmentVariable(WorktreeConfig.ModeEnvVar, SettingsService.Default.GetWorktreeMode());
            }

            // NOTE: PlanSeeder.EnsureSeeded() removed (task df1f521f). It injected a hardcoded
            // DEMO plan ("Task-Centric Memory System", leader Bob, with Alice/Charlie/Diana
            // assignments) into any EMPTY plan DB, so every clean install started up with a
            // phantom "active plan" that the startup hook then surfaced as real context. Demo
            // scaffolding must not ship — a fresh install has no active plan, which is correct.

            // One-shot migration: relocate any pre-2026-05-14 worktrees from
            // the old sibling layout to the new child-of-repo layout. Idempotent
            // and self-recovering — failures (e.g. locked directories) are
            // retried on the next startup. See WorktreeLayoutMigrationService.
            WorktreeLayoutMigrationService.RunIfNeeded();

            // Show splash screen immediately
            // CA2000: SplashScreen lifetime spans async init; disposed in TryShowMainForm after gates close.
#pragma warning disable CA2000
            var splash = new SplashScreen();
#pragma warning restore CA2000
            splash.Show();
            Application.DoEvents();

            // Create main form (starts hidden via Opacity=0)
            // CA2000: MainForm ownership transferred to Application.Run (disposes on message-loop exit).
#pragma warning disable CA2000
            var mainForm = new MainForm();
#pragma warning restore CA2000

            // Track three startup gates: loading, animation, and dashboard WebView2
            bool animationDone = false;
            bool dashboardReady = false;

            void TryShowMainForm()
            {
                if (!animationDone || !dashboardReady) return;
                splash.Close();
                splash.Dispose();
                mainForm.Opacity = 1;
                mainForm.Activate();
            }

            // Wire splash screen events (animation starts automatically in splash constructor)
            mainForm.LoadingComplete += (s, e) =>
            {
                System.Diagnostics.Trace.WriteLine("[Program] LoadingComplete event fired, calling splash.SetLoadingComplete()...");
                splash.SetLoadingComplete();
                System.Diagnostics.Trace.WriteLine("[Program] splash.SetLoadingComplete() returned");
            };

            // Dashboard WebView2 is loaded — one of two gates for showing the form
            mainForm.DashboardContentReady += (s, e) =>
            {
                System.Diagnostics.Trace.WriteLine("[Program] DashboardContentReady event fired");
                dashboardReady = true;
                TryShowMainForm();
            };

            // Splash animation + loading complete — other gate for showing the form
            splash.AnimationComplete += (s, e) =>
            {
                System.Diagnostics.Trace.WriteLine("[Program] AnimationComplete event fired");
                animationDone = true;
                TryShowMainForm();
            };

            try
            {
                Application.Run(mainForm);
            }
            finally
            {
                // Release the single-instance slot on shutdown even if the message loop threw
                // (task 4fec40e2). The OS reclaims the named mutex on process exit regardless;
                // the explicit release makes the intent clear.
                singleInstance.Dispose();
            }
        }
    }
}
