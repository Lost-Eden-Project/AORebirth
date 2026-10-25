using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using SmokeLounge.AOtomation.Messaging.Messages;
using SmokeLounge.AOtomation.Messaging.Serialization;

/// <summary>TCP framing only; all application messages use the repository AOtomation serializer.</summary>
sealed class ConnectedWireClient : IDisposable
{
    readonly TcpClient client = new();
    readonly MessageSerializer serializer = new();
    readonly NetworkStream network;
    readonly bool paddedLoginFrames;
    Stream input;
    public List<MessageBody> Received { get; } = [];
    public List<byte[]> Packets { get; } = [];
    public ConnectedWireClient(int port, bool paddedLoginFrames = false)
    {
        this.paddedLoginFrames = paddedLoginFrames;
        client.ReceiveTimeout = 15000; client.SendTimeout = 15000; client.NoDelay = true;
        client.Connect(IPAddress.Loopback, port);
        input = network = client.GetStream();
    }
    public void Send(MessageBody body, int characterId = 0, int? headerSender = null, int? headerReceiver = null)
    {
        using var stream = new MemoryStream();
        serializer.Serialize(stream, new Message { Header = new Header {
            MessageId = 0xdfdf, PacketType = body.PacketType, Unknown = 1,
            Sender = headerSender ?? (body is SmokeLounge.AOtomation.Messaging.Messages.SystemMessages.ZoneLoginMessage zone ? zone.CharacterId : characterId),
            Receiver = headerReceiver ?? (body is SmokeLounge.AOtomation.Messaging.Messages.SystemMessages.ZoneLoginMessage ? 2 : 0) }, Body = body });
        byte[] bytes = stream.ToArray();
        if (bytes.Length < 16) throw new FixtureFailure("connected-message-not-serialized");
        // ZoneSession.TryPopPacket consumes four-byte-aligned plaintext frames.
        if (!paddedLoginFrames && bytes.Length % 4 != 0)
            Array.Resize(ref bytes, bytes.Length + 4 - bytes.Length % 4);
        network.Write(bytes); network.Flush();
        Console.WriteLine("WIRE_SENT=" + body.GetType().Name);
    }
    public T Wait<T>(Func<T, bool>? matches = null) where T : MessageBody
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(30))
        {
            MessageBody? body;
            try { body = Read(); }
            catch (IOException exception)
            {
                string reason = exception is EndOfStreamException ? "end-of-stream"
                    : exception.InnerException is SocketException socket ? socket.SocketErrorCode.ToString()
                    : exception.GetType().Name;
                throw new FixtureFailure("connected-read-failed-waiting-" + typeof(T).Name + "-" + reason);
            }
            Console.WriteLine("WIRE_RECEIVED=" + (body?.GetType().Name ?? "transport-or-unmapped"));
            if (body != null) Received.Add(body);
            if (body is T result && (matches == null || matches(result))) return result;
        }
        throw new FixtureFailure("connected-packet-timeout-" + typeof(T).Name);
    }
    MessageBody? Read()
    {
        byte[] header = new byte[16]; input.ReadExactly(header);
        int length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(6, 2));
        if (length < 16 || length > ushort.MaxValue) throw new FixtureFailure("connected-invalid-frame-size");
        byte[] packet = new byte[length]; header.CopyTo(packet, 0);
        input.ReadExactly(packet.AsSpan(16));
        // LoginEngine.Client.Send aligns the transmitted buffer to four bytes
        // without changing Header.Size. ZoneSession does not add this padding.
        if (paddedLoginFrames && length % 4 != 0)
        {
            byte[] padding = new byte[4 - length % 4]; input.ReadExactly(padding);
            if (padding.Any(value => value != 0)) throw new FixtureFailure("connected-invalid-login-padding");
        }
        // Exact negotiation framing from ZoneSession.InitiateCompressionPacket.
        if (header[2] == 0x7f && header[3] == 0 && length == 16)
        {
            if (!ReferenceEquals(input, network)) throw new FixtureFailure("connected-compression-renegotiated");
            input = new ZLibStream(network, CompressionMode.Decompress, leaveOpen: true);
            return null;
        }
        Packets.Add(packet);
        using var stream = new MemoryStream(packet);
        SerializationContext? context = null;
        try
        {
            return serializer.Deserialize(stream, out context)?.Body;
        }
        catch (Exception exception)
        {
            short frameType = BinaryPrimitives.ReadInt16BigEndian(header.AsSpan(2, 2));
            string key = frameType == (short)PacketType.N3Message && packet.Length >= 20
                ? " N3_TYPE=0x" + BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(16, 4)).ToString("X8") : "";
            Console.Error.WriteLine("WIRE_DECODE_FAILED FRAME=0x" + ((ushort)frameType).ToString("X4") + key
                + " LENGTH=" + packet.Length + " OFFSET=" + stream.Position + " ERROR=" + exception.GetType().Name);
            // Only N3 messages carry gameplay data. Never print authentication payloads or diagnostic values.
            if (frameType == (short)PacketType.N3Message)
            {
                Exception? error = exception;
                for (int depth = 0; error != null && depth < 8; depth++, error = error.InnerException)
                {
                    string firstLine = error.Message.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
                    Console.Error.WriteLine("WIRE_DECODE_EXCEPTION TYPE=" + error.GetType().Name + " MESSAGE="
                        + firstLine[..Math.Min(firstLine.Length, 512)]);
                }
                DiagnosticInfo? field = context?.DiagnosticInfos.LastOrDefault();
                for (int depth = 0; field != null && depth < 16; depth++)
                {
                    var property = field.PropertyMetaData?.Property;
                    Console.Error.WriteLine("WIRE_DECODE_FIELD NAME=" + (property == null ? "<body>" : property.DeclaringType?.Name + "." + property.Name)
                        + " OFFSET=" + field.Offset + " LENGTH=" + field.Length);
                    field = field.DiagnosticInfos.LastOrDefault();
                }
            }
            throw;
        }
    }
    public bool AdmissionRejected(int characterId)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(30))
        {
            MessageBody? body;
            try { body = Read(); }
            catch (EndOfStreamException) { return true; }
            if (body != null) Received.Add(body);
            if (body is SmokeLounge.AOtomation.Messaging.Messages.N3Messages.FullCharacterMessage full
                && full.Identity.Instance == characterId) return false;
        }
        throw new FixtureFailure("connected-negative-admission-inconclusive");
    }
    public void Dispose()
    {
        client.Dispose();
        if (!ReferenceEquals(input, network)) input.Dispose();
    }
}
