namespace AORebirth.Modules.Network
{
    using System;
    using System.Buffers;
    using System.Net;

    /// <summary>Creates one <see cref="IConnectionFilter"/> per accepted zone connection, on the accept loop.</summary>
    public interface IConnectionFilterProvider
    {
        IConnectionFilter Create(ConnectionInfo connection);
    }

    /// <summary>
    /// Sits between the socket and the zone protocol for one connection. The host never interprets the raw bytes: a
    /// module reads its own hello, strips it, and decrypts or encrypts everything else. Inbound is only ever called from
    /// the connection's receive loop and Outbound only from its send loop, each in wire order, so per-direction cipher
    /// state needs no locking. Neither runs on a playfield tick thread. Disposed when the connection closes, if it
    /// implements <see cref="IDisposable"/>.
    /// </summary>
    public interface IConnectionFilter
    {
        /// <summary>
        /// Raw socket bytes in, plaintext AO bytes out. May write nothing (for example while the hello is incomplete).
        /// Return false to drop the connection.
        /// </summary>
        bool Inbound(ReadOnlySpan<byte> raw, IBufferWriter<byte> plaintext);

        /// <summary>
        /// Outgoing AO bytes in (after zlib once compression starts), wire bytes out. Throwing drops the connection.
        /// </summary>
        void Outbound(ReadOnlySpan<byte> plaintext, IBufferWriter<byte> wire);
    }

    public sealed class ConnectionInfo
    {
        public ConnectionInfo(Guid sessionId, EndPoint? remoteEndPoint)
        {
            SessionId = sessionId;
            RemoteEndPoint = remoteEndPoint;
        }

        public Guid SessionId { get; }

        public EndPoint? RemoteEndPoint { get; }
    }
}
