using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace MultiTerminal.Services
{
    /// <summary>
    /// Outcome of validating a source control account's credentials.
    /// </summary>
    public class SourceControlValidationResult
    {
        public bool Success { get; set; }

        /// <summary>
        /// The authenticated login/username reported by the provider on success.
        /// </summary>
        public string Login { get; set; }

        /// <summary>
        /// A friendly, user-facing error message on failure.
        /// </summary>
        public string Error { get; set; }

        public static SourceControlValidationResult Ok(string login) =>
            new SourceControlValidationResult { Success = true, Login = login };

        public static SourceControlValidationResult Fail(string error) =>
            new SourceControlValidationResult { Success = false, Error = error };
    }

    /// <summary>
    /// Validates source control account credentials by calling the provider's
    /// authenticated-user endpoint. Supports GitHub (Bearer token) and Bitbucket
    /// (Basic auth with app password). Returns the authenticated login on success
    /// or a friendly error message on failure.
    /// </summary>
    public static class SourceControlValidator
    {
        private static readonly HttpClient _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };

        /// <summary>
        /// Tests the given credentials against the provider. Provider is "github" or "bitbucket".
        /// </summary>
        public static async Task<SourceControlValidationResult> TestAsync(string provider, string username, string token)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return SourceControlValidationResult.Fail("Token is required.");
            }

            var normalized = (provider ?? "github").Trim().ToLowerInvariant();

            try
            {
                return normalized switch
                {
                    "bitbucket" => await TestBitbucketAsync(username, token).ConfigureAwait(false),
                    "github" => await TestGitHubAsync(token).ConfigureAwait(false),
                    _ => SourceControlValidationResult.Fail($"Unknown provider '{provider}'.")
                };
            }
            catch (TaskCanceledException)
            {
                return SourceControlValidationResult.Fail("Request timed out. Check your network connection.");
            }
            catch (HttpRequestException ex)
            {
                return SourceControlValidationResult.Fail($"Network error: {ex.Message}");
            }
        }

        private static async Task<SourceControlValidationResult> TestGitHubAsync(string token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.UserAgent.ParseAdd("MultiTerminal");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");

            using var response = await _http.SendAsync(request).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return SourceControlValidationResult.Fail("Invalid credentials.");
            }
            if (!response.IsSuccessStatusCode)
            {
                return SourceControlValidationResult.Fail($"GitHub returned {(int)response.StatusCode} {response.ReasonPhrase}.");
            }

            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var login = ExtractStringProperty(body, "login");
            return SourceControlValidationResult.Ok(login);
        }

        private static async Task<SourceControlValidationResult> TestBitbucketAsync(string username, string token)
        {
            if (string.IsNullOrWhiteSpace(username))
            {
                return SourceControlValidationResult.Fail("Username is required for Bitbucket.");
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.bitbucket.org/2.0/user");
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{token}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
            request.Headers.UserAgent.ParseAdd("MultiTerminal");

            using var response = await _http.SendAsync(request).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return SourceControlValidationResult.Fail("Invalid credentials.");
            }
            if (!response.IsSuccessStatusCode)
            {
                return SourceControlValidationResult.Fail($"Bitbucket returned {(int)response.StatusCode} {response.ReasonPhrase}.");
            }

            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            // Bitbucket reports "username" and "display_name"; prefer username, fall back to display_name.
            var login = ExtractStringProperty(body, "username") ?? ExtractStringProperty(body, "display_name");
            return SourceControlValidationResult.Ok(login);
        }

        private static string ExtractStringProperty(string json, string propertyName)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty(propertyName, out var prop)
                    && prop.ValueKind == JsonValueKind.String)
                {
                    return prop.GetString();
                }
            }
            catch (JsonException)
            {
                // Malformed body — treat as no login available.
            }

            return null;
        }
    }
}
