namespace Convai.Infrastructure.Networking.Transport
{
    /// <summary>
    ///     Options for establishing a transport connection.
    /// </summary>
    public class TransportConnectOptions
    {
        /// <summary>Automatically subscribe to published tracks.</summary>
        public bool AutoSubscribe { get; set; } = true;

        /// <summary>Enable adaptive streaming for video.</summary>
        public bool AdaptiveStream { get; set; } = true;

        /// <summary>Enable dynamic broadcast for efficient bandwidth.</summary>
        public bool Dynacast { get; set; } = true;

        /// <summary>Connection timeout in seconds.</summary>
        public int TimeoutSeconds { get; set; } = 30;

        /// <summary>
        ///     Overrides the native single-peer-connection topology. Null keeps the transport's
        ///     default. Set <c>false</c> only while diagnosing a negotiation or reconnect problem;
        ///     WebGL ignores this value.
        /// </summary>
        public bool? SinglePeerConnection { get; set; }
    }
}
