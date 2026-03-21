using Grpc.Net.Client;

namespace KPACS.Viewer.Services;

internal static class RenderServerGrpcClientFactory
{
    public static GrpcChannel CreateChannel(string serverUrl)
    {
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

        bool isHttps = Uri.TryCreate(serverUrl, UriKind.Absolute, out Uri? uri)
            && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

        var handler = new SocketsHttpHandler
        {
            EnableMultipleHttp2Connections = true,
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(10),
        };

        if (isHttps)
        {
            handler.SslOptions = new System.Net.Security.SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
            };
        }

        return GrpcChannel.ForAddress(serverUrl, new GrpcChannelOptions
        {
            MaxReceiveMessageSize = 64 * 1024 * 1024,
            MaxSendMessageSize = 64 * 1024 * 1024,
            HttpHandler = handler,
        });
    }
}
