using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using MultiTerminal.API.Controllers;
using Xunit.Sdk;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Keeps the hand-authored REST reference synchronized with the compiled MVC route metadata.
    /// Response examples are deliberately outside this contract.
    /// </summary>
    public class ApiReferenceRouteDriftTests
    {
        private static readonly Regex DocumentedEndpointPattern = new(
            "<div\\s+class=\"endpoint-block\"[^>]*>\\s*" +
            "<span\\s+class=\"method\\s+[^\"]+\">(?<method>[A-Z]+)</span>\\s*" +
            "<code>(?<route>/[^<]*)</code>",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly IReadOnlyDictionary<Endpoint, string> DocumentedOnlyExceptions =
            new Dictionary<Endpoint, string>
            {
                [new("GET", "/")] = "Minimal-API health route declared in MultiTerminalRestServer, outside API/Controllers.",
                [new("GET", "/health")] = "Minimal-API health route declared in MultiTerminalRestServer, outside API/Controllers.",
            };

        private static readonly IReadOnlyDictionary<Endpoint, string> ControllerOnlyExceptions =
            new Dictionary<Endpoint, string>
            {
                [new("GET", "/api/permission-relay/test/status")] = "DEBUG-only integration harness; absent from shipped Release builds.",
                [new("POST", "/api/permission-relay/test/toggle")] = "DEBUG-only integration harness; absent from shipped Release builds.",
                [new("POST", "/api/permission-relay/test/elicitation")] = "DEBUG-only integration harness; absent from shipped Release builds.",
                [new("POST", "/api/permission-relay/test/choice")] = "DEBUG-only integration harness; absent from shipped Release builds.",
                [new("POST", "/api/permission-relay/test/plan-approval")] = "DEBUG-only integration harness; absent from shipped Release builds.",
                [new("POST", "/api/permission-relay/test/notification")] = "DEBUG-only integration harness; absent from shipped Release builds.",
            };

        [Fact]
        public void Controller_routes_and_documented_routes_match_in_both_directions()
        {
            var actual = DiscoverControllerEndpoints();
            var documented = ReadDocumentedEndpoints();

            var undocumented = actual
                .Except(documented)
                .Where(endpoint => !ControllerOnlyExceptions.ContainsKey(endpoint))
                .Order()
                .ToArray();
            var nonexistent = documented
                .Except(actual)
                .Where(endpoint => !DocumentedOnlyExceptions.ContainsKey(endpoint))
                .Order()
                .ToArray();

            if (undocumented.Length == 0 && nonexistent.Length == 0)
            {
                return;
            }

            throw new XunitException(
                "REST API route drift detected. Keep API/Controllers and docs/html/api-reference.html " +
                "synchronized (HTTP methods and routes only)." + Environment.NewLine +
                FormatDifference("Controller routes missing from documentation", undocumented) +
                FormatDifference("Documented routes missing from controllers", nonexistent));
        }

        private static HashSet<Endpoint> DiscoverControllerEndpoints()
        {
            var endpoints = new HashSet<Endpoint>();
            var controllerTypes = typeof(HealthController).Assembly
                .GetTypes()
                .Where(type =>
                    !type.IsAbstract &&
                    type.Namespace == typeof(HealthController).Namespace &&
                    typeof(ControllerBase).IsAssignableFrom(type));

            foreach (var controllerType in controllerTypes)
            {
                var controllerTemplates = controllerType
                    .GetCustomAttributes(inherit: true)
                    .OfType<IRouteTemplateProvider>()
                    .Select(attribute => attribute.Template)
                    .Where(template => template is not null)
                    .Cast<string>()
                    .DefaultIfEmpty(string.Empty);

                foreach (var method in controllerType.GetMethods(BindingFlags.Instance | BindingFlags.Public))
                {
                    var routeAttributes = method
                        .GetCustomAttributes(inherit: true)
                        .OfType<HttpMethodAttribute>()
                        .ToArray();

                    foreach (var routeAttribute in routeAttributes)
                    {
                        foreach (var controllerTemplate in controllerTemplates)
                        {
                            var route = CombineTemplates(controllerTemplate, routeAttribute.Template);
                            foreach (var httpMethod in routeAttribute.HttpMethods)
                            {
                                endpoints.Add(new Endpoint(httpMethod, route));
                            }
                        }
                    }
                }
            }

            return endpoints;
        }

        private static HashSet<Endpoint> ReadDocumentedEndpoints()
        {
            var referencePath = FindRepositoryFile("docs", "html", "api-reference.html");
            var html = File.ReadAllText(referencePath);

            return DocumentedEndpointPattern
                .Matches(html)
                .Select(match => new Endpoint(
                    match.Groups["method"].Value,
                    WebUtility.HtmlDecode(match.Groups["route"].Value)))
                .ToHashSet();
        }

        private static string CombineTemplates(string controllerTemplate, string actionTemplate)
        {
            if (!string.IsNullOrEmpty(actionTemplate) &&
                (actionTemplate.StartsWith('/') || actionTemplate.StartsWith("~/", StringComparison.Ordinal)))
            {
                return NormalizeRoute(actionTemplate.TrimStart('~'));
            }

            return NormalizeRoute($"/{controllerTemplate.Trim('/')}/{actionTemplate?.Trim('/')}");
        }

        private static string NormalizeRoute(string route) =>
            "/" + route.Trim().Trim('/');

        private static string FindRepositoryFile(params string[] relativeParts)
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                var candidate = Path.Combine(new[] { directory.FullName }.Concat(relativeParts).ToArray());
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }

            throw new FileNotFoundException(
                $"Could not find {Path.Combine(relativeParts)} above {AppContext.BaseDirectory}.");
        }

        private static string FormatDifference(string heading, IReadOnlyCollection<Endpoint> endpoints) =>
            endpoints.Count == 0
                ? string.Empty
                : Environment.NewLine + heading + ":" + Environment.NewLine +
                  string.Join(Environment.NewLine, endpoints.Select(endpoint => $"  {endpoint}")) +
                  Environment.NewLine;

        private sealed record Endpoint(string Method, string Route) : IComparable<Endpoint>
        {
            public int CompareTo(Endpoint other)
            {
                var routeComparison = string.Compare(Route, other.Route, StringComparison.Ordinal);
                return routeComparison != 0
                    ? routeComparison
                    : string.Compare(Method, other.Method, StringComparison.Ordinal);
            }

            public override string ToString() => $"{Method} {Route}";
        }
    }
}
