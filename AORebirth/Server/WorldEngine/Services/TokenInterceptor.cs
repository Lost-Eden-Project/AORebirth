namespace WorldEngine.Services
{
    using System;
    using System.Security.Cryptography;
    using System.Text;
    using System.Threading.Tasks;

    using AORebirth.World.Contracts;

    using Grpc.Core;
    using Grpc.Core.Interceptors;

    /// <summary>Every call must carry the shared token when one is configured (constant-time compare).</summary>
    public sealed class TokenInterceptor : Interceptor
    {
        readonly byte[] _token;

        public TokenInterceptor(WorldOptions options)
        {
            _token = Encoding.UTF8.GetBytes(options.Token);
        }

        void Check(ServerCallContext context)
        {
            if (_token.Length == 0) return;
            string? presented = context.RequestHeaders.GetValue(WorldChannel.TokenHeader);
            byte[] bytes = Encoding.UTF8.GetBytes(presented ?? string.Empty);
            if (!CryptographicOperations.FixedTimeEquals(bytes, _token))
                throw new RpcException(new Status(StatusCode.Unauthenticated, "invalid world token"));
        }

        public override Task<TResponse> UnaryServerHandler<TRequest, TResponse>(TRequest request,
            ServerCallContext context, UnaryServerMethod<TRequest, TResponse> continuation)
        {
            Check(context);
            return continuation(request, context);
        }

        public override Task DuplexStreamingServerHandler<TRequest, TResponse>(IAsyncStreamReader<TRequest> requestStream,
            IServerStreamWriter<TResponse> responseStream, ServerCallContext context,
            DuplexStreamingServerMethod<TRequest, TResponse> continuation)
        {
            Check(context);
            return continuation(requestStream, responseStream, context);
        }

        public override Task<TResponse> ClientStreamingServerHandler<TRequest, TResponse>(
            IAsyncStreamReader<TRequest> requestStream, ServerCallContext context,
            ClientStreamingServerMethod<TRequest, TResponse> continuation)
        {
            Check(context);
            return continuation(requestStream, context);
        }

        public override Task ServerStreamingServerHandler<TRequest, TResponse>(TRequest request,
            IServerStreamWriter<TResponse> responseStream, ServerCallContext context,
            ServerStreamingServerMethod<TRequest, TResponse> continuation)
        {
            Check(context);
            return continuation(request, responseStream, context);
        }
    }

    static class CallerSession
    {
        /// <summary>The registered instance making this call; refuses calls from an unknown or superseded process.</summary>
        public static State.ServiceInstance Require(State.WorldState state, ServerCallContext context)
            => state.FindSession(context.RequestHeaders.GetValue(WorldChannel.SessionHeader))
                ?? throw new RpcException(new Status(StatusCode.FailedPrecondition, "stale_session"));
    }
}
