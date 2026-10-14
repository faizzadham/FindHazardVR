using System;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Convai.Runtime.Core.Configuration;
using UnityEngine;
using UnityEngine.Networking;

namespace Convai.Sample.Runtime
{
    /// <summary>
    ///     Demonstrates resolving a fresh Convai auth token from a developer-owned server before each connection.
    /// </summary>
    /// <remarks>
    ///     The Convai API key belongs on the server and must never be assigned to this component. This localhost
    ///     demo intentionally sends no client credential and must not be exposed as a production endpoint.
    /// </remarks>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-1200)]
    public sealed class LocalServerAuthTokenProviderExample : MonoBehaviour, IConvaiAuthTokenProvider
    {
        private const string LocalDemoServerIdHeader = "X-Convai-Auth-Server";
        private const string LocalDemoServerId = "local-demo-v1";

        [Serializable]
        private sealed class ServerTokenResponse
        {
            public string apiAuthToken;
            public string expirationTime;
        }

        [Header("Developer-owned token server")]
        [SerializeField]
        [Tooltip("Use localhost HTTP only for local development. Use HTTPS for any remote endpoint.")]
        private string tokenEndpoint = "http://127.0.0.1:8787/v1/convai/token";

        [SerializeField]
        [Min(1)]
        private int requestTimeoutSeconds = 15;

        private bool _registered;

        /// <summary>Registers this instance before the earlier Convai manager lifecycle begins.</summary>
        private void Awake()
        {
            ConvaiAuthTokenProviderRegistry.Register(this);
            _registered = true;
        }

        /// <summary>Removes this provider if it is still the active SDK registration.</summary>
        private void OnDestroy()
        {
            if (_registered)
                ConvaiAuthTokenProviderRegistry.Unregister(this);
        }

        /// <inheritdoc />
        public async Task<AuthTokenResult> GetTokenAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!TryValidateEndpoint(tokenEndpoint, out string normalizedEndpoint, out string endpointError))
                return AuthTokenResult.Failed(endpointError);

            using var request = new UnityWebRequest(normalizedEndpoint, UnityWebRequest.kHttpVerbPOST);
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes("{}"));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.timeout = Mathf.Max(1, requestTimeoutSeconds);
            request.SetRequestHeader("Content-Type", "application/json");

            UnityWebRequestAsyncOperation operation;
            try
            {
                operation = request.SendWebRequest();
                while (!operation.isDone)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        request.Abort();
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    await Task.Yield();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                request.Abort();
                throw;
            }
            catch (Exception exception)
            {
                return AuthTokenResult.Failed(
                    $"The game-server token request failed ({exception.GetType().Name}).");
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (request.responseCode < 200 || request.responseCode >= 300)
            {
                string status = request.responseCode > 0
                    ? $"HTTP {request.responseCode}"
                    : "no HTTP response";
                bool isLocalDemoServer = string.Equals(
                    request.GetResponseHeader(LocalDemoServerIdHeader),
                    LocalDemoServerId,
                    StringComparison.Ordinal);

                if (request.responseCode == 501 &&
                    !isLocalDemoServer)
                {
                    return AuthTokenResult.Failed(
                        "The configured URL returned HTTP 501 but is not the Convai local auth demo server. " +
                        "Verify that the component endpoint is http://127.0.0.1:8787/v1/convai/token and " +
                        "start server.py rather than a static file server.");
                }

                return AuthTokenResult.Failed(
                    $"The game server rejected the Convai token request ({status}).");
            }

            ServerTokenResponse payload;
            try
            {
                payload = JsonUtility.FromJson<ServerTokenResponse>(request.downloadHandler.text);
            }
            catch (Exception exception)
            {
                return AuthTokenResult.Failed(
                    $"The game server returned invalid token JSON ({exception.GetType().Name}).");
            }

            string convaiToken = payload?.apiAuthToken?.Trim();
            if (string.IsNullOrEmpty(convaiToken))
                return AuthTokenResult.Failed("The game server returned an empty Convai auth token.");

            DateTimeOffset? expiresAtUtc = null;
            string expiration = payload.expirationTime?.Trim();
            if (!string.IsNullOrEmpty(expiration))
            {
                if (!DateTimeOffset.TryParse(
                        expiration,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AllowWhiteSpaces |
                        DateTimeStyles.AssumeUniversal |
                        DateTimeStyles.AdjustToUniversal,
                        out DateTimeOffset parsedExpiration))
                {
                    return AuthTokenResult.Failed(
                        "The game server returned an invalid Convai token expirationTime.");
                }

                expiresAtUtc = parsedExpiration;
            }

            return AuthTokenResult.Succeeded(convaiToken, expiresAtUtc);
        }

        private static bool TryValidateEndpoint(
            string endpoint,
            out string normalizedEndpoint,
            out string error)
        {
            normalizedEndpoint = endpoint?.Trim() ?? string.Empty;
            error = string.Empty;

            if (!Uri.TryCreate(normalizedEndpoint, UriKind.Absolute, out Uri uri) ||
                (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            {
                error = "Configure an absolute HTTP or HTTPS game-server token endpoint.";
                return false;
            }

            if (uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback)
            {
                error = "Remote game-server token endpoints must use HTTPS.";
                return false;
            }

            normalizedEndpoint = uri.AbsoluteUri;
            return true;
        }
    }
}
