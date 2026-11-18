using Tether.Shared;

namespace Tether.Tests;

public class DnsPacketTests
{
    // id 0x1234, RD set, one question: example.com A IN
    private static readonly byte[] ExampleA =
    {
        0x12, 0x34, 0x01, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        7, (byte)'e', (byte)'x', (byte)'a', (byte)'m', (byte)'p', (byte)'l', (byte)'e',
        3, (byte)'c', (byte)'o', (byte)'m', 0,
        0x00, 0x01, 0x00, 0x01,
    };

    [Fact]
    public void ParseQuestion_reads_name_and_type()
    {
        var q = DnsPacket.ParseQuestion(ExampleA);
        Assert.NotNull(q);
        Assert.Equal("example.com", q!.Value.Name);
        Assert.Equal((ushort)1, q.Value.Type);
    }

    [Fact]
    public void BuildNxDomain_keeps_the_id_and_sets_response_and_rcode_3()
    {
        var r = DnsPacket.BuildNxDomain(ExampleA);
        Assert.Equal(0x12, r[0]);
        Assert.Equal(0x34, r[1]);
        Assert.NotEqual(0, r[2] & 0x80);          // QR: this is a response
        Assert.Equal(3, r[3] & 0x0F);             // RCODE NXDOMAIN
        Assert.Equal(1, (r[4] << 8) | r[5]);      // question echoed
        Assert.Equal(0, (r[6] << 8) | r[7]);      // no answers
        Assert.Equal(ExampleA.Length, r.Length);  // header + the same question section
    }

    [Fact]
    public void BuildServFail_sets_rcode_2()
        => Assert.Equal(2, DnsPacket.BuildServFail(ExampleA)[3] & 0x0F);

    [Fact]
    public void Malformed_packets_return_null_not_an_exception()
    {
        Assert.Null(DnsPacket.ParseQuestion(Array.Empty<byte>()));
        Assert.Null(DnsPacket.ParseQuestion(ExampleA[..15]));                         // truncated inside the name
        var overflow = (byte[])ExampleA.Clone(); overflow[12] = 63;                   // label longer than the packet
        Assert.Null(DnsPacket.ParseQuestion(overflow));
        var pointer = (byte[])ExampleA.Clone(); pointer[12] = 0xC0;                   // compression pointer in a question
        Assert.Null(DnsPacket.ParseQuestion(pointer));
        var response = (byte[])ExampleA.Clone(); response[2] |= 0x80;                 // already a response
        Assert.Null(DnsPacket.ParseQuestion(response));
    }
}
