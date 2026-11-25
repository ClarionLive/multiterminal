using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MultiTerminal.API.Controllers;
using MultiTerminal.API.Gateway;
using MultiTerminal.Services;
using Xunit;

namespace MultiTerminal.Tests
{
    /// <summary>
    /// Fixes made on top of PR #103 in its review (task ba0f0ee0): the phone gateway never mounts the open
    /// route, one launch per project at a time, and a time-limited UI-thread call that never runs late. Not
    /// covered: that MainForm's open callback uses the gate and the call (wiring no test executes).
    /// </summary>
    public class ProjectOpenReviewTests
    {
        /// <summary>
        /// The open route raises windows and launches sessions on the desk, so it must never be mounted on the
        /// phone gateway. Pins the gateway's own list (MultiRemoteGatewayHost.MountedControllers, the array the
        /// host hands to GatewayControllerFeatureProvider), not source text. Falsified: adding
        /// typeof(ProjectOpenController) to the list turns the first assertion red.
        /// </summary>
        [Fact]
        public void The_phone_gateway_does_not_mount_the_open_controller()
        {
            Assert.DoesNotContain(typeof(ProjectOpenController), MultiRemoteGatewayHost.MountedControllers);
            Assert.Contains(typeof(ProjectContextController), MultiRemoteGatewayHost.MountedControllers);
        }

        /// <summary>
        /// A second open of a project whose launch is still under way must not start another (two session
        /// dialogs and StartTerminal twice on one Home tab). Falsified: making TryBegin always true turns the
        /// second assertion red.
        /// </summary>
        [Fact]
        public void One_launch_per_project_at_a_time()
        {
            var gate = new ProjectOpenGate();
            Assert.True(gate.TryBegin("p1"));
            Assert.False(gate.TryBegin("p1"));
            Assert.True(gate.TryBegin("p2"));
            Assert.True(gate.IsLaunching("p1"));

            gate.End("p1");
            Assert.False(gate.IsLaunching("p1"));
            Assert.True(gate.TryBegin("p1"));
            Assert.False(gate.TryBegin(null));
            Assert.False(gate.TryBegin(""));
        }

        [Fact]
        public async Task A_ui_call_that_runs_returns_its_value()
        {
            var (ran, value) = await UiThreadCall.RunAsync(work => work(), () => 42, TimeSpan.FromSeconds(5));
            Assert.True(ran);
            Assert.Equal(42, value);
        }

        /// <summary>
        /// A UI thread that does not get to the work in time answers "not run", and the work, delivered late,
        /// does nothing: the REST caller was told "busy", so the open must not happen after all. Falsified:
        /// removing the abandoned check in the posted delegate turns the last assertion red.
        /// </summary>
        [Fact]
        public async Task A_ui_call_past_its_limit_is_abandoned_and_never_runs_late()
        {
            Action posted = null;
            int runs = 0;
            var (ran, _) = await UiThreadCall.RunAsync(work => posted = work, () => ++runs, TimeSpan.FromMilliseconds(50));

            Assert.False(ran);
            Assert.NotNull(posted);
            posted(); // the UI thread finally gets to it
            Assert.Equal(0, runs);
        }

        [Fact]
        public async Task A_ui_call_that_cannot_be_posted_is_not_run_and_an_exception_from_the_work_is_rethrown()
        {
            var (ran, _) = await UiThreadCall.RunAsync<int>(work => throw new InvalidOperationException("no handle"), () => 1, TimeSpan.FromSeconds(1));
            Assert.False(ran);

            await Assert.ThrowsAsync<ArgumentException>(() =>
                UiThreadCall.RunAsync<int>(work => work(), () => throw new ArgumentException("boom"), TimeSpan.FromSeconds(1)));
        }
    }
}
