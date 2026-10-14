#nullable enable
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Convai.RestAPI.Internal;
using Convai.RestAPI.Transport;
using Convai.Api.Transport;

namespace Convai.RestAPI.Services
{
    /// <summary>
    /// Service for animation-related API operations.
    /// </summary>
    public sealed class AnimationService : ConvaiServiceBase
    {
        private const string AnimationListEndpoint = "animations/list";
        private const string AnimationGetEndpoint = "animations/get";

        internal AnimationService(ConvaiRestClientOptions options, IConvaiHttpTransport transport)
            : base(options, transport)
        {
            // API2 currently returns storage URIs, not the signed media URLs this facade promises.
        }

        /// <summary>
        /// Gets a paginated list of animations.
        /// </summary>
        /// <param name="page">The page number (1-based).</param>
        /// <param name="status">The status filter (e.g., "active").</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The animation list response.</returns>
        public async Task<ServerAnimationListResponse> GetListAsync(
            int page = 1,
            string status = "active",
            CancellationToken cancellationToken = default)
        {
            var requestBody = new Dictionary<string, string>
            {
                { "status", status },
                { "generate_signed_urls", "true" },
                { "page", page.ToString() }
            };

            var additionalHeaders = new Dictionary<string, string>
            {
                { "Source", "convaiUI" }
            };

            return await PostAsync<ServerAnimationListResponse>(
                AnimationListEndpoint,
                requestBody,
                useBeta: false,
                additionalHeaders: additionalHeaders,
                cancellationToken: cancellationToken).ConfigureAwait(ApiAwait.ResumeOnCapturedContext);
        }

        /// <summary>
        /// Gets the details of a specific animation.
        /// </summary>
        /// <param name="animationId">The animation ID.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The animation data response.</returns>
        public async Task<ServerAnimationDataResponse> GetAsync(
            string animationId,
            CancellationToken cancellationToken = default)
        {
            var requestBody = new Dictionary<string, string>
            {
                { "animation_id", animationId },
                { "generate_upload_video_urls", "true" }
            };

            return await PostAsync<ServerAnimationDataResponse>(
                AnimationGetEndpoint,
                requestBody,
                useBeta: false,
                cancellationToken: cancellationToken).ConfigureAwait(ApiAwait.ResumeOnCapturedContext);
        }

    }
}
