using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace MultiTerminal.Terminal
{
    /// <summary>
    /// Caches a shared CoreWebView2Environment for all terminal instances.
    /// This reduces memory overhead by sharing the browser process across multiple WebView2 controls.
    /// </summary>
    public static class WebView2EnvironmentCache
    {
        private static CoreWebView2Environment _environment;
        private static readonly object _lock = new object();
        private static Task<CoreWebView2Environment> _initTask;

        /// <summary>
        /// Gets the shared WebView2 environment, creating it if necessary.
        /// </summary>
        public static async Task<CoreWebView2Environment> GetEnvironmentAsync()
        {
            if (_environment != null)
                return _environment;

            // Use a lock to ensure only one initialization attempt
            lock (_lock)
            {
                if (_initTask == null)
                {
                    _initTask = CreateEnvironmentAsync();
                }
            }

            return await _initTask;
        }

        private static async Task<CoreWebView2Environment> CreateEnvironmentAsync()
        {
            // Create a dedicated folder for WebView2 user data
            string userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MultiTerminal", "WebView2Data");

            try
            {
                if (!Directory.Exists(userDataFolder))
                    Directory.CreateDirectory(userDataFolder);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MultiTerminal] Failed to create WebView2 data folder: {ex.Message}");
                // Fall back to temp folder
                userDataFolder = Path.Combine(Path.GetTempPath(), "MultiTerminal", "WebView2Data");
                Directory.CreateDirectory(userDataFolder);
            }

            // Create the environment with default options
            _environment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,  // Use installed WebView2 runtime
                userDataFolder: userDataFolder,
                options: new CoreWebView2EnvironmentOptions());

            return _environment;
        }

        /// <summary>
        /// Resets the cached environment (for testing or cleanup).
        /// </summary>
        public static void Reset()
        {
            lock (_lock)
            {
                _environment = null;
                _initTask = null;
            }
        }
    }
}
