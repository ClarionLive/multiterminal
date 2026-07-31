using System;
using System.Linq;
using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using MultiTerminal.API.Controllers;
using MultiTerminal.Models;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Regression guard for task ea7d9cf9: MultiTerminal's HTTP surface must never hand out a stored
    /// credential. Three endpoints used to serialize a raw PAT — <c>GET /api/source-accounts/{id}/token</c>,
    /// <c>GET /api/owner-profile/github-token</c>, and the <c>token</c> field of
    /// <c>GET /api/projects/{projectId}/source-account</c> — all added speculatively by f1e586b "for push"
    /// with no consumer ever written. Every fetch wrote the secret into an agent's session transcript on
    /// disk, which is how a live token leaked and had to be rotated.
    /// </summary>
    /// <remarks>
    /// SCOPE, stated plainly so a green run is not over-read. These tests are STRUCTURAL: they prove the
    /// token-returning ROUTES and ACTIONS are gone, and that the two serialized models carry presence
    /// booleans rather than secrets. They do NOT invoke the surviving
    /// <c>GetProjectSourceAccount</c> and inspect its response body, because constructing its dependencies
    /// (<c>ProjectDatabase</c>, <c>SourceControlAccountService</c>) opens the user's REAL
    /// multiterminal.db and Windows Credential Manager — both take no injectable path
    /// (<c>MultiterminalDb.Open()</c> in each constructor), so a "unit" test of the success shape would
    /// read and migrate live user data. That response's member list is instead guarded at source level by
    /// scripts/verify-no-secret-serialization.mjs check (2), which fails on exactly the
    /// <c>Ok(new { ... token = ... })</c> shape that shipped. The two guards are complementary: this file
    /// checks the compiled surface, the census checks the source.
    /// </remarks>
    public class NoCredentialEndpointsTests
    {
        // Route templates on any HTTP-verb attribute declared on a controller's public actions.
        private static string[] RouteTemplatesOf(Type controller) =>
            controller
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .SelectMany(m => m.GetCustomAttributes<HttpMethodAttribute>())
                .Select(a => a.Template)
                .Where(t => t != null)
                .ToArray();

        [Theory]
        [InlineData(typeof(SourceControlAccountsController))]
        [InlineData(typeof(OwnerProfileController))]
        public void No_action_declares_a_token_returning_route(Type controller)
        {
            // Falsifies on reintroduction: restoring [HttpGet("{id}/token")] or [HttpGet("github-token")]
            // fails here. Matching on the SEGMENT rather than the exact template is deliberate — a
            // differently-spelled revival (e.g. "{id}/pat", "github-token-v2") should still trip it.
            foreach (var template in RouteTemplatesOf(controller))
            {
                Assert.False(
                    template.Contains("token", StringComparison.OrdinalIgnoreCase),
                    $"{controller.Name} declares route '{template}', which looks like a credential-returning "
                    + "endpoint. Task ea7d9cf9 removed those: have the service perform the authenticated "
                    + "operation and return its RESULT, never the credential.");
            }
        }

        [Theory]
        [InlineData(typeof(SourceControlAccountsController), "GetAccountToken")]
        [InlineData(typeof(OwnerProfileController), "GetGitHubToken")]
        public void The_removed_dispensing_actions_do_not_exist(Type controller, string removedAction)
        {
            Assert.Null(controller.GetMethod(removedAction, BindingFlags.Public | BindingFlags.Instance));
        }

        [Fact]
        public void Surviving_source_account_actions_are_exactly_the_metadata_pair()
        {
            // Tight on purpose: this is a credential-adjacent surface, so a NEW action appearing here
            // should be a deliberate, reviewed act rather than something that slips in unnoticed.
            var actions = typeof(SourceControlAccountsController)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.GetCustomAttributes<HttpMethodAttribute>().Any())
                .Select(m => m.Name)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(new[] { "GetProjectSourceAccount", "ListAccounts" }, actions);
        }

        [Theory]
        [InlineData(typeof(SourceControlAccount))]
        [InlineData(typeof(OwnerProfile))]
        public void Serialized_models_expose_no_raw_secret_property(Type model)
        {
            // GET /api/source-accounts returns whole SourceControlAccount objects, so a raw secret
            // property on the model would leak through a controller nobody edited — past the route and
            // action checks above. Reflection over the real type is an INDEPENDENT check from the
            // census's source-text scan of the same rule.
            string[] secretNames =
            {
                "Token", "Password", "Secret", "Pat", "ApiKey", "AccessToken", "PrivateKey", "ClientSecret",
            };

            foreach (var prop in model.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                Assert.False(
                    secretNames.Contains(prop.Name, StringComparer.OrdinalIgnoreCase),
                    $"{model.Name}.{prop.Name} looks like a raw credential on a model that is serialized "
                    + "whole by an endpoint. Keep the secret in Windows Credential Manager and expose only "
                    + "a presence boolean (HasToken / HasGitHubToken).");
            }
        }

        [Theory]
        [InlineData(typeof(SourceControlAccount), "HasToken")]
        [InlineData(typeof(OwnerProfile), "HasGitHubToken")]
        public void Presence_booleans_survive_so_callers_can_still_test_for_configuration(
            Type model, string presenceProperty)
        {
            // The complement of the test above: ea7d9cf9 removed the VALUE, not the ability to ask
            // whether a credential is configured. Deleting these would silently break the account
            // editor's "Test" affordance and /api/owner-profile's `hasGitHubToken`.
            var prop = model.GetProperty(presenceProperty, BindingFlags.Public | BindingFlags.Instance);
            Assert.NotNull(prop);
            Assert.Equal(typeof(bool), prop.PropertyType);
        }
    }
}
