namespace MultiTerminal.API.Gateway
{
    /// <summary>
    /// In-process bridge that lets MT's own controllers (which don't load the gateway's
    /// appsettings) reach the gateway's runtime config (task ca6c5344, pipeline Run-1
    /// cross-model HIGH). The gateway host publishes these on startup; MT's
    /// NotificationsController reads them when forwarding runtime notifications to the
    /// in-process phone receiver, so:
    ///   • the forward targets the CONFIGURED port (not a hardcoded :5100), and
    ///   • when a NotificationSecret is set, the forward carries the matching X-MT-Secret
    ///     header — otherwise enabling the secret would silently 403 every push.
    /// Fields are volatile (set once at gateway start, read on each forward from request
    /// threads). Defaults keep pre-fold behaviour when the gateway never started.
    /// </summary>
    public static class GatewayRuntimeConfig
    {
        private static volatile string _notificationSecret = "";
        private static volatile int _port = 5100;

        /// <summary>Shared secret the notification receiver requires (empty = unauthenticated).</summary>
        public static string NotificationSecret
        {
            get => _notificationSecret;
            set => _notificationSecret = value ?? "";
        }

        /// <summary>The gateway's resolved listen port (MultiRemote:Port).</summary>
        public static int Port
        {
            get => _port;
            set => _port = value;
        }
    }
}
