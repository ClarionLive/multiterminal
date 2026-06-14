using System;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace MultiTerminal.API.Gateway
{
    /// <summary>
    /// Restricts the gateway host's controller discovery to an explicit whitelist
    /// (task ca6c5344, item [3]). The gateway shares MultiTerminal.dll with MT's :5050
    /// host, so a default <c>AddControllers()</c> would mount ALL ~25 controllers on
    /// :5100 behind the phone auth gate — broader surface than the PWA needs, and a
    /// route collision with the gateway's own ported endpoints (e.g. MT's
    /// NotificationsController vs. the gateway's /api/notifications push receiver).
    /// We mount only the controllers whose native routes the PWA actually calls and
    /// whose responses we want byte-identical to :5050 (Tasks, Projects, Team,
    /// remote-mode, digest); everything else is hand-mapped in-process.
    /// </summary>
    public class GatewayControllerFeatureProvider : ControllerFeatureProvider
    {
        private readonly HashSet<Type> _allowed;

        public GatewayControllerFeatureProvider(params Type[] allowed)
        {
            _allowed = new HashSet<Type>(allowed ?? Array.Empty<Type>());
        }

        protected override bool IsController(TypeInfo typeInfo)
        {
            return base.IsController(typeInfo) && _allowed.Contains(typeInfo.AsType());
        }
    }
}
