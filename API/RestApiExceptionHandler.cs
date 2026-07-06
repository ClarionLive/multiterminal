using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using MultiTerminal.MCPServer.Services;
using MultiTerminal.Models;
using MultiTerminal.Services;

namespace MultiTerminal.API
{
    /// <summary>
    /// Global exception handler for the :5050 REST surface (Eval P2, task c522764d, item 1).
    /// Logs unhandled controller/middleware exceptions to <see cref="DebugLogService"/>, then
    /// returns <c>false</c> so the framework's registered <c>IProblemDetailsService</c> writes a
    /// consistent RFC 7807 ProblemDetails 500 body instead of a bare stack-trace 500.
    /// Wired via <c>AddProblemDetails()</c> + <c>AddExceptionHandler&lt;RestApiExceptionHandler&gt;()</c>
    /// and invoked by <c>app.UseExceptionHandler()</c> (first in the pipeline).
    /// </summary>
    internal sealed class RestApiExceptionHandler : IExceptionHandler
    {
        private readonly MessageBroker _broker;

        public RestApiExceptionHandler(MessageBroker broker)
        {
            _broker = broker;
        }

        public ValueTask<bool> TryHandleAsync(
            HttpContext httpContext,
            Exception exception,
            CancellationToken cancellationToken)
        {
            try
            {
                // DebugLogService is wired onto the broker by MainForm; guard against a null
                // handle (e.g. very early startup) so logging never masks or replaces the error.
                _broker?.DebugLogService?.Log(
                    "RestApi",
                    DebugLogLevel.Error,
                    $"Unhandled exception on {httpContext.Request.Method} {httpContext.Request.Path}: {exception}");
            }
            catch
            {
                // Logging must never throw out of the exception handler.
            }

            // Return false: defer response generation to the ProblemDetails service (RFC 7807 500).
            return ValueTask.FromResult(false);
        }
    }
}
