using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace Bifurcate.Core;

/// <summary>
/// A-record lookup against a specific DNS server. The system resolver can cache an ISP answer
/// while the SQL client uses the VPN's DNS, and those two IPs are not always the same.
/// </summary>
public static class DnsAQuery
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);

    public readonly record struct Lookup(bool ServerAnswered, IReadOnlyList<IPAddress> Addresses);

    public static Lookup Query(string host, IReadOnlyList<IPAddress> servers, TimeSpan? timeout = null)
    {
        bool answered = false;

        foreach (IPAddress server in servers)
        {
            try
            {
                IReadOnlyList<IPAddress> addresses = Query(host, server, timeout ?? DefaultTimeout);
                answered = true;
                if (addresses.Count > 0) { return new Lookup(true, addresses); }
            }
            catch (Exception ex) when (ex is SocketException or TimeoutException or InvalidDataException)
            {
                // Try the next VPN DNS server.
            }
        }

        return new Lookup(answered, []);
    }

    public static IReadOnlyList<IPAddress> Query(string host, IPAddress server, TimeSpan timeout)
    {
        ushort id = (ushort)Random.Shared.Next(0, ushort.MaxValue);
        byte[] request = BuildQuery(id, host);

        using Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.ReceiveTimeout = (int)timeout.TotalMilliseconds;
        socket.SendTimeout = (int)timeout.TotalMilliseconds;
        socket.Connect(new IPEndPoint(server, 53));
        socket.Send(request);

        byte[] buffer = new byte[512];
        int read = socket.Receive(buffer);
        return ParseAnswers(buffer.AsSpan(0, read), id);
    }

    public static byte[] BuildQuery(ushort id, string host)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);

        writer.Write(BinaryPrimitives.ReverseEndianness(id));
        writer.Write(BinaryPrimitives.ReverseEndianness((ushort)0x0100));
        writer.Write(BinaryPrimitives.ReverseEndianness((ushort)1));
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        WriteName(writer, host);
        writer.Write(BinaryPrimitives.ReverseEndianness((ushort)1));
        writer.Write(BinaryPrimitives.ReverseEndianness((ushort)1));

        return stream.ToArray();
    }

    public static IReadOnlyList<IPAddress> ParseAnswers(ReadOnlySpan<byte> packet, ushort expectedId)
    {
        if (packet.Length < 12) { throw new InvalidDataException("DNS response is too short."); }

        ushort id = BinaryPrimitives.ReadUInt16BigEndian(packet);
        if (id != expectedId) { throw new InvalidDataException("DNS response id did not match the query."); }

        ushort flags = BinaryPrimitives.ReadUInt16BigEndian(packet[2..]);
        if ((flags & 0x000F) is not 0 and not 3)
        {
            throw new InvalidDataException($"DNS server returned rcode {flags & 0x000F}.");
        }

        int questions = BinaryPrimitives.ReadUInt16BigEndian(packet[4..]);
        int answers = BinaryPrimitives.ReadUInt16BigEndian(packet[6..]);
        int offset = 12;

        for (int i = 0; i < questions; i++)
        {
            offset = SkipName(packet, offset);
            offset += 4;
        }

        List<IPAddress> addresses = [];
        for (int i = 0; i < answers && offset < packet.Length; i++)
        {
            offset = SkipName(packet, offset);
            if (offset + 10 > packet.Length) { break; }

            ushort type = BinaryPrimitives.ReadUInt16BigEndian(packet[offset..]);
            offset += 8;
            ushort length = BinaryPrimitives.ReadUInt16BigEndian(packet[offset..]);
            offset += 2;

            if (type == 1 && length == 4 && offset + 4 <= packet.Length)
            {
                addresses.Add(new IPAddress(packet.Slice(offset, 4)));
            }

            offset += length;
        }

        return addresses;
    }

    private static void WriteName(BinaryWriter writer, string host)
    {
        foreach (string label in host.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            byte[] bytes = System.Text.Encoding.ASCII.GetBytes(label);
            writer.Write((byte)bytes.Length);
            writer.Write(bytes);
        }

        writer.Write((byte)0);
    }

    private static int SkipName(ReadOnlySpan<byte> packet, int offset)
    {
        int hops = 0;
        while (offset < packet.Length && hops++ < 64)
        {
            byte length = packet[offset];
            if (length == 0) { return offset + 1; }

            if ((length & 0xC0) == 0xC0)
            {
                return offset + 2;
            }

            offset += 1 + length;
        }

        return packet.Length;
    }
}
