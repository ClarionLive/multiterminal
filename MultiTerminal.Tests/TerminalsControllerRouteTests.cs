using System.Linq;
using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using MultiTerminal.API.Controllers;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Regression guard for the terminal-controller merge (task 7ce19175 item 4). The three former
    /// terminal controllers were merged into <see cref="TerminalsController"/> under
    /// <c>api/terminals</c>; the WebSocket stream + stream-list moved to the plural path with the
    /// old singular <c>api/terminal/...</c> paths kept as DEPRECATED aliases for one release.
    /// These tests assert BOTH route templates are declared on each stream action, so a future edit
    /// that drops an alias fails here rather than silently 404-ing an un-migrated caller. (True
    /// end-to-end binding against a running host is a live-smoke item — see the ticket testResults.)
    /// </summary>
    public class TerminalsControllerRouteTests
    {
        private static string[] GetRouteTemplates(string methodName) =>
            typeof(TerminalsController)
                .GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance)
                .GetCustomAttributes<HttpGetAttribute>()
                .Select(a => a.Template)
                .ToArray();

        [Fact]
        public void Stream_declares_canonical_and_deprecated_alias_routes()
        {
            var templates = GetRouteTemplates(nameof(TerminalsController.Stream));
            Assert.Contains("{id}/stream", templates);              // canonical -> api/terminals/{id}/stream
            Assert.Contains("/api/terminal/{id}/stream", templates); // deprecated singular alias
        }

        [Fact]
        public void GetActiveStreams_declares_canonical_and_deprecated_alias_routes()
        {
            var templates = GetRouteTemplates(nameof(TerminalsController.GetActiveStreams));
            Assert.Contains("streams", templates);              // canonical -> api/terminals/streams
            Assert.Contains("/api/terminal/streams", templates); // deprecated singular alias
        }

        [Theory]
        [InlineData(nameof(TerminalsController.GetStats), "{name}/stats")]
        [InlineData(nameof(TerminalsController.Stream), "{id}/stream")]
        [InlineData(nameof(TerminalsController.GetActiveStreams), "streams")]
        public void Canonical_routes_are_under_api_terminals(string methodName, string expectedTemplate)
        {
            // Controller-level [Route("api/terminals")] + these relative templates = the plural family.
            var route = typeof(TerminalsController).GetCustomAttribute<RouteAttribute>();
            Assert.Equal("api/terminals", route.Template);
            Assert.Contains(expectedTemplate, GetRouteTemplates(methodName));
        }
    }
}
