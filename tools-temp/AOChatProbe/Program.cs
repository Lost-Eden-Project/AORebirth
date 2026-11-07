// AOChatProbe: records the Anarchy Online client's chat-server traffic without touching the client.
//
// Windows raw socket capture (SIO_RCVALL, admin, no driver) sees every IPv4 packet on the local interface. The probe
// keeps only TCP segments of connections owned by an AnarchyOnline process (IP Helper's owner-PID table), puts each
// direction back in sequence order, and frames streams that speak the chat protocol (u16 type, u16 length, payload;
// all big-endian, uncompressed). Each chat packet becomes one line in chat.raw.log, in the packets.raw.log format:
//   2026-10-24T01:31:33.0920002Z RX len=46 0023002E...   (RX = server to client, TX = client to server)
// Zone connections (compressed, other framing) are left out.
//
// Usage (elevated): AOChatProbe [output-file] [process-name] [chat-ports]
//   default output chat.raw.log beside the exe, process AnarchyOnline, chat port 7105 (retail).

using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

string output = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "chat.raw.log");
string processName = args.Length > 1 ? args[1] : "AnarchyOnline";
// The chat server's port(s): retail's is 7105. Only these connections are written out; packet contents decide nothing.
var chatPorts = new HashSet<int>(args.Length > 2 ? args[2].Split(',').Select(int.Parse) : [7105]);

IPAddress local = LocalAddress();
Console.WriteLine($"Capturing on {local} for {processName}; writing {output}. Ctrl+C to stop.");

using var log = new StreamWriter(output, append: true) { AutoFlush = true };
log.WriteLine("# session " + DateTime.UtcNow.ToString("o"));

var connections = new ConnectionTable(processName);
var streams = new Dictionary<FlowKey, FlowStream>();
using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };

using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Raw, ProtocolType.IP);
socket.Bind(new IPEndPoint(local, 0));
socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.HeaderIncluded, true);
socket.IOControl(IOControlCode.ReceiveAll, BitConverter.GetBytes(1), new byte[4]);
socket.ReceiveTimeout = 500;

var buffer = new byte[65535];
var seen = new Dictionary<(int RemotePort, bool Outgoing), int>();
long lastRefresh = 0;
long lastSeen = Environment.TickCount64;
while (!cancel.IsCancellationRequested)
{
    if (Environment.TickCount64 - lastRefresh > 1000)
    {
        connections.Refresh();
        lastRefresh = Environment.TickCount64;
    }

    if (Environment.TickCount64 - lastSeen > 15000)
    {
        log.WriteLine("# seen " + (seen.Count == 0 ? "nothing" : string.Join(" ", seen.Select(p => $"{p.Key.RemotePort}/{(p.Key.Outgoing ? "TX" : "RX")}={p.Value}"))));
        seen.Clear();
        lastSeen = Environment.TickCount64;
    }

    int read;
    try
    {
        read = socket.Receive(buffer);
    }
    catch (SocketException e) when (e.SocketErrorCode == SocketError.TimedOut)
    {
        continue;
    }

    if (!Segment.TryParse(buffer, read, out Segment segment) || segment.Payload.Length == 0)
        continue;

    // Which end is the client: the local endpoint of a connection the AO process owns.
    bool outgoing = connections.Owns(segment.Source, segment.SourcePort, segment.Destination, segment.DestinationPort);
    bool incoming = !outgoing && connections.Owns(segment.Destination, segment.DestinationPort, segment.Source, segment.SourcePort);
    if (!outgoing && !incoming)
        continue;
    int remotePort = outgoing ? segment.DestinationPort : segment.SourcePort;
    seen[(remotePort, outgoing)] = seen.GetValueOrDefault((remotePort, outgoing)) + 1;
    if (!chatPorts.Contains(remotePort))
        continue;

    var key = new FlowKey(segment.Source, segment.SourcePort, segment.Destination, segment.DestinationPort);
    if (!streams.TryGetValue(key, out FlowStream? stream))
    {
        stream = new FlowStream(outgoing ? "TX" : "RX", line => log.WriteLine(line));
        streams[key] = stream;
        log.WriteLine($"# flow {stream.Direction} {segment.Source}:{segment.SourcePort} -> {segment.Destination}:{segment.DestinationPort}");
    }

    foreach (byte[] packet in stream.Add(segment.Sequence, segment.Payload))
        log.WriteLine($"{DateTime.UtcNow:o} {stream.Direction} len={packet.Length} {Convert.ToHexString(packet)}");
}

Console.WriteLine("Stopped.");

static IPAddress LocalAddress()
{
    // The interface the default route uses: connect a UDP socket (sends nothing) and read its local end.
    using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    probe.Connect(new IPEndPoint(IPAddress.Parse("8.8.8.8"), 53));
    return ((IPEndPoint)probe.LocalEndPoint!).Address;
}

readonly record struct FlowKey(IPAddress Source, int SourcePort, IPAddress Destination, int DestinationPort);

/// <summary>One IPv4 TCP segment.</summary>
readonly record struct Segment(IPAddress Source, int SourcePort, IPAddress Destination, int DestinationPort, uint Sequence, byte[] Payload)
{
    public static bool TryParse(byte[] data, int length, out Segment segment)
    {
        segment = default;
        if (length < 20 || data[0] >> 4 != 4 || data[9] != 6)
            return false;
        int ipHeader = (data[0] & 0x0F) * 4;
        int total = Math.Min(length, (data[2] << 8) | data[3]);
        if (total < ipHeader + 20)
            return false;
        int tcp = ipHeader;
        int tcpHeader = (data[tcp + 12] >> 4) * 4;
        int payloadStart = tcp + tcpHeader;
        if (payloadStart > total)
            return false;
        segment = new Segment(
            new IPAddress(data.AsSpan(12, 4)),
            (data[tcp] << 8) | data[tcp + 1],
            new IPAddress(data.AsSpan(16, 4)),
            (data[tcp + 2] << 8) | data[tcp + 3],
            (uint)((data[tcp + 4] << 24) | (data[tcp + 5] << 16) | (data[tcp + 6] << 8) | data[tcp + 7]),
            data.AsSpan(payloadStart, total - payloadStart).ToArray());
        return true;
    }
}

/// <summary>One direction of a TCP connection: segments back in order, then cut into chat packets.</summary>
sealed class FlowStream(string direction, Action<string> diagnostic)
{
    readonly SortedDictionary<uint, byte[]> _pending = new();
    readonly List<byte> _data = new();
    uint? _next;
    bool? _isChat;

    public string Direction { get; } = direction;

    public IEnumerable<byte[]> Add(uint sequence, byte[] payload)
    {
        _next ??= sequence;
        _pending[sequence] = payload;
        while (_pending.Count > 0)
        {
            uint first = _pending.Keys.First();
            int ahead = unchecked((int)(first - _next.Value));
            if (ahead > 0)
                break;
            byte[] piece = _pending[first];
            _pending.Remove(first);
            int skip = -ahead;
            if (skip >= piece.Length)
                continue;
            _data.AddRange(piece.AsSpan(skip).ToArray());
            _next = unchecked(first + (uint)piece.Length);
        }

        if (_pending.Count > 256)
            _pending.Clear();

        return Frame();
    }

    List<byte[]> Frame()
    {
        var packets = new List<byte[]>();
        while (_data.Count >= 4)
        {
            int length = (_data[2] << 8) | _data[3];
            int type = (_data[0] << 8) | _data[1];
            // Only chat-port connections reach here. A stream joined mid-packet cannot be framed: give it up once rather
            // than log garbage (restart the chat connection, e.g. relog, after starting the probe).
            if (_isChat == null)
            {
                _isChat = type < 0x400 && length < 0x4000;
                diagnostic($"# classify {Direction} framed={_isChat} first={Convert.ToHexString(_data.GetRange(0, Math.Min(12, _data.Count)).ToArray())}");
            }
            if (_isChat == false)
            {
                _data.Clear();
                break;
            }

            if (_data.Count < 4 + length)
                break;
            packets.Add(_data.GetRange(0, 4 + length).ToArray());
            _data.RemoveRange(0, 4 + length);
        }

        return packets;
    }
}

/// <summary>TCP connections owned by the named process, from IP Helper's owner-PID table.</summary>
sealed class ConnectionTable(string processName)
{
    HashSet<(IPAddress, int, IPAddress, int)> _owned = new();

    public bool Owns(IPAddress localAddress, int localPort, IPAddress remoteAddress, int remotePort)
        => _owned.Contains((localAddress, localPort, remoteAddress, remotePort));

    public void Refresh()
    {
        var pids = new HashSet<int>(Process.GetProcessesByName(processName).Select(p => p.Id));
        var owned = new HashSet<(IPAddress, int, IPAddress, int)>();
        foreach (var row in TcpRows())
        {
            if (pids.Contains((int)row.OwningPid))
                owned.Add((new IPAddress(row.LocalAddr), Port(row.LocalPort), new IPAddress(row.RemoteAddr), Port(row.RemotePort)));
        }

        _owned = owned;
    }

    static int Port(uint raw) => (int)(((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF));

    [StructLayout(LayoutKind.Sequential)]
    struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);

    static IEnumerable<MibTcpRowOwnerPid> TcpRows()
    {
        const int AfInet = 2, TcpTableOwnerPidAll = 5;
        int size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, TcpTableOwnerPidAll, 0);
        IntPtr table = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(table, ref size, false, AfInet, TcpTableOwnerPidAll, 0) != 0)
                yield break;
            int count = Marshal.ReadInt32(table);
            int rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
            for (int i = 0; i < count; i++)
                yield return Marshal.PtrToStructure<MibTcpRowOwnerPid>(table + 4 + (i * rowSize));
        }
        finally
        {
            Marshal.FreeHGlobal(table);
        }
    }
}
