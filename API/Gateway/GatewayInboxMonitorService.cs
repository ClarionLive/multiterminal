using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MultiTerminal.MCPServer.Services;

namespace MultiTerminal.API.Gateway
{
    /// <summary>
    /// Watches MT's inbox and pushes to the phone when unread count rises (task ca6c5344,
    /// item [7]). The standalone MultiRemote HTTP-polled :5050; folded in-process it reads
    /// <see cref="MessageBroker.GetInbox"/> + <see cref="MessageBroker.IsRemoteMode"/>
    /// DIRECTLY — no HTTP hop. First tick records a baseline (no push); thereafter a rise in
    /// unread triggers a push, but only when remote mode is on (no phone pings while the user
    /// is at the desk). The baseline still advances when suppressed so toggling remote mode on
    /// later doesn't retroactively fire.
    /// </summary>
    public class GatewayInboxMonitorService : BackgroundService
    {
        private readonly MessageBroker _broker;
        private readonly PushNotificationService _push;
        private readonly ILogger<GatewayInboxMonitorService> _logger;
        private readonly string _userId;
        private readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(60);

        private int _lastUnreadCount = -1; // -1 = not yet polled

        public GatewayInboxMonitorService(
            MessageBroker broker,
            PushNotificationService push,
            IConfiguration config,
            ILogger<GatewayInboxMonitorService> logger)
        {
            _broker = broker;
            _push = push;
            _logger = logger;
            // Neutral committed default (task 642c14e3, item 8) — no per-owner identity baked in.
            // The inbox is keyed by this id (GetInbox(_userId)); an existing owner whose inbox key
            // differs sets MultiRemote:InboxUserId in appsettings.Local.json to keep inbox-rise pushes.
            _userId = config.GetValue<string>("MultiRemote:InboxUserId") ?? "Owner";
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Let the app finish starting before the first read.
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
            catch (TaskCanceledException)
            {
                return;
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await CheckInbox();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Inbox monitor poll failed: {Error}", ex.Message);
                }

                try
                {
                    await Task.Delay(_pollInterval, stoppingToken);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }
        }

        private async Task CheckInbox()
        {
            var result = _broker.GetInbox(_userId, unreadOnly: true, limit: 1);

            // Transient DB error returns {Success=false, UnreadCount=0}; skip the tick and
            // leave the baseline untouched so a recovery read isn't misread as a rise
            // (pipeline Run-1 debugger finding — phantom push).
            if (!result.Success)
            {
                _logger.LogWarning("Inbox monitor: GetInbox failed ({Error}) — skipping tick", result.Error);
                return;
            }

            int unreadCount = result.UnreadCount;

            // First poll — record baseline, don't notify.
            if (_lastUnreadCount == -1)
            {
                _lastUnreadCount = unreadCount;
                _logger.LogInformation("Inbox monitor baseline: {Count} unread", unreadCount);
                return;
            }

            if (unreadCount > _lastUnreadCount)
            {
                var newCount = unreadCount - _lastUnreadCount;

                // remoteMode gate — no phone pushes when the user is at the desk.
                if (!_broker.IsRemoteMode)
                {
                    _logger.LogInformation("Inbox push suppressed: {New} new (remoteMode=false)", newCount);
                    _lastUnreadCount = unreadCount;
                    return;
                }

                var body = newCount == 1
                    ? "You have a new inbox message"
                    : $"You have {newCount} new inbox messages";

                // Branch on ACTUAL delivery, not subscription count — a send can fail for every
                // subscription (errors / 410-pruned) yet leave a non-zero count (pipeline Run-2
                // cross-model finding: don't log a false success).
                var pushResult = await _push.SendToAllWithResult("Inbox", body, "inbox");
                if (pushResult.Delivered)
                {
                    _logger.LogInformation("Inbox push sent: {New} new ({Total} total unread, {Ok} device(s))", newCount, unreadCount, pushResult.SuccessCount);
                }
                else
                {
                    _logger.LogWarning("Inbox: {New} new but push NOT delivered ({Detail})", newCount,
                        pushResult.Error ?? $"{pushResult.ErrorCount} send(s) failed, 0 delivered");
                }
            }

            _lastUnreadCount = unreadCount;
        }
    }
}
