#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Convai.Api.Transport
{
    public interface IConvaiApiTransport : IDisposable
    {
        ConvaiApiTransportCapabilities Capabilities { get; }

        Task<ConvaiApiResponse> SendAsync(
            ConvaiApiRequest request,
            CancellationToken cancellationToken = default);

        Task SendSseAsync(
            ConvaiApiRequest request,
            Action<ConvaiApiSseEvent> onEvent,
            CancellationToken cancellationToken = default);
    }

    public static class ConvaiApiTransportFactory
    {
        public static IConvaiApiTransport Create(TimeSpan? defaultTimeout = null)
        {
            TimeSpan timeout = defaultTimeout ?? TimeSpan.FromSeconds(30);
#if UNITY_WEBGL && !UNITY_EDITOR
            return new UnityWebRequestApiTransport(timeout);
#else
            return new HttpClientApiTransport(timeout);
#endif
        }
    }
}
