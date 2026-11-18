using System.Text;

namespace Tether.Shared;

/// <summary>Just enough DNS wire format for a filtering proxy: read the question, answer with a failure code.</summary>
public static class DnsPacket
{
    private const int HeaderLength = 12;

    /// <summary>Name (lowercase, no trailing dot) and type of the first question. Null for anything that is not a plain query.</summary>
    public static (string Name, ushort Type)? ParseQuestion(byte[] packet)
    {
        var q = ReadQuestion(packet, out _);
        return q;
    }

    private static (string Name, ushort Type)? ReadQuestion(byte[] p, out int end)
    {
        end = 0;
        if (p.Length < HeaderLength + 5) return null;
        if ((p[2] & 0x80) != 0) return null;                 // a response, not a query
        if (((p[4] << 8) | p[5]) < 1) return null;           // no question
        var pos = HeaderLength;
        var name = new StringBuilder();
        while (true)
        {
            if (pos >= p.Length) return null;
            int len = p[pos++];
            if (len == 0) break;
            if ((len & 0xC0) != 0) return null;              // compression pointers do not appear in a question
            if (pos + len > p.Length) return null;
            if (name.Length > 0) name.Append('.');
            name.Append(Encoding.ASCII.GetString(p, pos, len));
            pos += len;
            if (name.Length > 253) return null;
        }
        if (pos + 4 > p.Length) return null;
        var type = (ushort)((p[pos] << 8) | p[pos + 1]);
        end = pos + 4;
        return (name.ToString().ToLowerInvariant(), type);
    }

    public static byte[] BuildNxDomain(byte[] query) => Reply(query, 3);
    public static byte[] BuildServFail(byte[] query) => Reply(query, 2);

    /// <summary>Header plus the original question, no answers; extra sections (EDNS) are dropped.</summary>
    private static byte[] Reply(byte[] query, int rcode)
    {
        var q = ReadQuestion(query, out var end);
        if (q == null)
        {
            // Not parseable: still answer something with the same id so the client does not hang.
            var bare = new byte[HeaderLength];
            if (query.Length >= 2) { bare[0] = query[0]; bare[1] = query[1]; }
            bare[2] = 0x80; bare[3] = (byte)(0x80 | rcode);
            return bare;
        }
        var r = new byte[end];
        Array.Copy(query, r, end);
        r[2] = (byte)(0x80 | (query[2] & 0x79));             // QR=1, keep opcode and RD
        r[3] = (byte)(0x80 | rcode);                         // RA=1
        r[4] = 0; r[5] = 1;                                  // one question
        for (var i = 6; i < HeaderLength; i++) r[i] = 0;     // no answer, authority or additional
        return r;
    }
}
