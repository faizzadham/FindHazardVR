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
    /// Service for character-scoped long-term memory APIs.
    /// </summary>
    public sealed class MemoryService : ConvaiServiceBase
    {
        private const string AddMemoryEndpoint = "memory/add";
        private const string ListMemoryEndpoint = "memory/list";
        private const string GetMemoryEndpoint = "memory/get";
        private const string DeleteMemoryEndpoint = "memory/delete";
        private const string DeleteAllMemoryEndpoint = "memory/delete-all";

        internal MemoryService(ConvaiRestClientOptions options, IConvaiHttpTransport transport)
            : base(options, transport)
        {
        }

        public async Task<AddMemoriesResponse> AddAsync(
            string characterId,
            string endUserId,
            IReadOnlyList<string> memories,
            IReadOnlyDictionary<string, object>? metadata = null,
            CancellationToken cancellationToken = default)
        {
            var requestBody = new Dictionary<string, object>
            {
                { "character_id", characterId },
                { "end_user_id", endUserId },
                { "memories", new List<string>(memories) }
            };

            if (metadata != null && metadata.Count > 0)
                requestBody["metadata"] = new Dictionary<string, object>(metadata);

            return await PostPlatformAsync<AddMemoriesResponse>(
                AddMemoryEndpoint,
                requestBody,
                cancellationToken: cancellationToken).ConfigureAwait(ApiAwait.ResumeOnCapturedContext);
        }

        public async Task<MemoryListResponse> ListAsync(
            string characterId,
            string endUserId,
            int page = 1,
            int pageSize = 50,
            CancellationToken cancellationToken = default)
        {
            var requestBody = new Dictionary<string, object>
            {
                { "character_id", characterId },
                { "end_user_id", endUserId },
                { "page", page },
                { "page_size", pageSize }
            };

            return await PostPlatformAsync<MemoryListResponse>(
                ListMemoryEndpoint,
                requestBody,
                cancellationToken: cancellationToken).ConfigureAwait(ApiAwait.ResumeOnCapturedContext);
        }

        public async Task<MemoryRecord> GetAsync(
            string characterId,
            string endUserId,
            string memoryId,
            CancellationToken cancellationToken = default)
        {
            var requestBody = new Dictionary<string, object>
            {
                { "character_id", characterId },
                { "end_user_id", endUserId },
                { "memory_id", memoryId }
            };

            return await PostPlatformAsync<MemoryRecord>(
                GetMemoryEndpoint,
                requestBody,
                cancellationToken: cancellationToken).ConfigureAwait(ApiAwait.ResumeOnCapturedContext);
        }

        public async Task<MemoryDeleteResponse> DeleteAsync(
            string characterId,
            string endUserId,
            string memoryId,
            CancellationToken cancellationToken = default)
        {
            var requestBody = new Dictionary<string, object>
            {
                { "character_id", characterId },
                { "end_user_id", endUserId },
                { "memory_id", memoryId }
            };

            return await PostPlatformAsync<MemoryDeleteResponse>(
                DeleteMemoryEndpoint,
                requestBody,
                cancellationToken: cancellationToken).ConfigureAwait(ApiAwait.ResumeOnCapturedContext);
        }

        public async Task<MemoryDeleteAllResponse> DeleteAllAsync(
            string characterId,
            string endUserId,
            CancellationToken cancellationToken = default)
        {
            var requestBody = new Dictionary<string, object>
            {
                { "character_id", characterId },
                { "end_user_id", endUserId }
            };

            return await PostPlatformAsync<MemoryDeleteAllResponse>(
                DeleteAllMemoryEndpoint,
                requestBody,
                cancellationToken: cancellationToken).ConfigureAwait(ApiAwait.ResumeOnCapturedContext);
        }
    }
}
