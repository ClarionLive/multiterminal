using System;
using System.Reflection;
using Microsoft.AspNetCore.Cors;
using MultiTerminal.API;
using MultiTerminal.API.Controllers;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Structural proof of the least-privilege CORS scoping (task f9697aac, pipeline Run 1 LOW +
    /// Alice negative test 2): the panel origin can read ONLY TaskReportsController's report GETs, and
    /// the loopback-gated secret controllers expose NO browser CORS surface at all.
    /// </summary>
    /// <remarks>
    /// CORS ACAO is emitted per the policy that applies to the matched endpoint. The report GETs opt
    /// into the scoped <see cref="RestCorsOriginPolicy.PanelReadPolicyName"/> policy (which trusts the
    /// panel origin); every other controller — having no <c>[EnableCors]</c> — falls to the default
    /// deny-all policy, so no browser origin (including the panel's) receives ACAO from them. This
    /// reflection check is the falsifiable proof that a panel-origin fetch of, say,
    /// <c>OwnerProfileController.GetProfile</c> gets no ACAO and cannot be read cross-origin.
    /// </remarks>
    /// <remarks>
    /// These controllers are named "secret controllers" for historical reasons: they no longer serve
    /// any credential (task ea7d9cf9 removed the token-returning endpoints outright — the example
    /// above used to be <c>GetGitHubToken</c>). The CORS scoping assertion is still worth keeping:
    /// deny-all is the correct default for local-only config surfaces, and this test is what stops a
    /// future <c>[EnableCors]</c> from silently widening one.
    /// </remarks>
    public class CorsScopingTests
    {
        [Theory]
        [InlineData(nameof(TaskReportsController.GetReports))]
        [InlineData(nameof(TaskReportsController.GetReport))]
        public void Report_GETs_opt_into_the_scoped_panel_read_policy(string methodName)
        {
            var method = typeof(TaskReportsController).GetMethod(methodName);
            Assert.NotNull(method);
            var attr = method.GetCustomAttribute<EnableCorsAttribute>();
            Assert.NotNull(attr);
            Assert.Equal(RestCorsOriginPolicy.PanelReadPolicyName, attr.PolicyName);
        }

        [Theory]
        [InlineData(typeof(OwnerProfileController))]
        [InlineData(typeof(SourceControlAccountsController))]
        [InlineData(typeof(MultiConnectController))]
        public void Secret_controllers_carry_no_EnableCors_so_they_deny_all_browser_origins(Type controller)
        {
            // No [EnableCors] on the class...
            Assert.Null(controller.GetCustomAttribute<EnableCorsAttribute>());

            // ...nor on any declared action — so they use the deny-all default policy and expose no
            // ACAO to the panel origin (or any browser origin). This is the least-privilege guarantee:
            // the PAT/token/config GETs have no browser consumer and grant no browser read.
            foreach (var method in controller.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                Assert.Null(method.GetCustomAttribute<EnableCorsAttribute>());
            }
        }
    }
}
