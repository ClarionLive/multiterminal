using System;
using System.IO;
using Microsoft.Extensions.Configuration;

namespace MultiTerminal.API.Gateway
{
    /// <summary>
    /// Resolves the per-install data directory for the MultiRemote gateway's mutable
    /// state files — push-config.json (VAPID keys + push subscriptions) and
    /// notification-toggles.json (task ca6c5344, item [7]). The standalone MultiRemote
    /// kept these next to its project (CWD), which is unreliable for a WinForms host that
    /// can launch from anywhere and may sit under a read-only install dir. We default to
    /// %APPDATA%\MultiTerminal (writable, per-user, outside the repo so nothing leaks into
    /// git), overridable via MultiRemote:DataPath.
    ///
    /// MIGRATION: to preserve existing phone subscriptions + VAPID identity, copy the
    /// standalone's push-config.json into this directory before first run — otherwise the
    /// service generates fresh VAPID keys and every phone must re-subscribe.
    /// </summary>
    public static class GatewayPaths
    {
        public static string DataDir(IConfiguration config)
        {
            var configured = config?["MultiRemote:DataPath"];
            var dir = !string.IsNullOrWhiteSpace(configured)
                ? configured
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "MultiTerminal");

            try
            {
                Directory.CreateDirectory(dir);
            }
            catch
            {
                // Non-fatal: if the dir can't be created the push/toggle files just won't
                // persist; the gateway still serves everything else.
            }

            return dir;
        }
    }
}
