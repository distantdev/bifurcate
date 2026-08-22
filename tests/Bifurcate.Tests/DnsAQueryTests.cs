using System.Buffers.Binary;
using System.Net;
using Bifurcate.Core;

namespace Bifurcate.Tests;

public class DnsAQueryTests
{
    [Fact]
    public void BuildQueryIsAStandardAQuestion()
    {
        byte[] expected =
        [
            0x12, 0x34, 0x01, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            7, (byte)'e', (byte)'x', (byte)'a', (byte)'m', (byte)'p', (byte)'l', (byte)'e',
            3, (byte)'c', (byte)'o', (byte)'m', 0,
            0, 1, 0, 1,
        ];

        Assert.Equal(expected, DnsAQuery.BuildQuery(0x1234, "example.com"));
    }

    [Fact]
    public void ParseAnswersReadsAnARecordBehindACompressionPointer()
    {
        byte[] query = DnsAQuery.BuildQuery(0x1234, "example.com");
        byte[] response = new byte[query.Length + 16];
        query.CopyTo(response, 0);
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(2), 0x8180);
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(6), 1);

        int at = query.Length;
        response[at++] = 0xC0;
        response[at++] = 0x0C;
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(at), 1);
        at += 2;
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(at), 1);
        at += 2;
        BinaryPrimitives.WriteUInt32BigEndian(response.AsSpan(at), 60);
        at += 4;
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(at), 4);
        at += 2;
        response[at++] = 192;
        response[at++] = 0;
        response[at++] = 2;
        response[at] = 10;

        IPAddress[] addresses = [.. DnsAQuery.ParseAnswers(response, 0x1234)];

        Assert.Equal([IPAddress.Parse("192.0.2.10")], addresses);
    }

    [Fact]
    public void ParseAnswersTreatsNxDomainAsNoAddresses()
    {
        byte[] query = DnsAQuery.BuildQuery(0x4, "missing.example");
        BinaryPrimitives.WriteUInt16BigEndian(query.AsSpan(2), 0x8183);

        Assert.Empty(DnsAQuery.ParseAnswers(query, 0x4));
    }
}
