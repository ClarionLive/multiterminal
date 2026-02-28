using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using MultiTerminal.Services;

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

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // Ensure plan database is seeded with initial data
            PlanSeeder.EnsureSeeded();

            // Show splash screen immediately
            var splash = new SplashScreen();
            splash.Show();
            Application.DoEvents();

            // Create main form (starts hidden via Opacity=0)
            var mainForm = new MainForm();

            // Wire splash screen events (animation starts automatically in splash constructor)
            mainForm.LoadingComplete += (s, e) =>
            {
                System.Diagnostics.Trace.WriteLine("[Program] LoadingComplete event fired, calling splash.SetLoadingComplete()...");
                splash.SetLoadingComplete();
                System.Diagnostics.Trace.WriteLine("[Program] splash.SetLoadingComplete() returned");
            };

            // Show main form when animation completes (and loading is done)
            splash.AnimationComplete += (s, e) =>
            {
                splash.Close();
                splash.Dispose();
                mainForm.Opacity = 1;
                mainForm.Activate();
            };

            Application.Run(mainForm);
        }
    }
}
