using System;
using System.Net;

namespace OriCoop.Authority
{
    // A probe returns one routable address literal. Do not accept hostnames, port
    // numbers, scoped IPv6, legacy integer/octal IPv4 or other resolver input.
    internal static class DiagnosticAddress
    {
        internal static string PublicLiteral(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length > 45 || text.IndexOf('%') >= 0)
                return null;
            foreach (char c in text)
                if (!Uri.IsHexDigit(c) && c != '.' && c != ':') return null;
            IPAddress ip;
            if (!IPAddress.TryParse(text, out ip)) return null;
            byte[] b = ip.GetAddressBytes();
            if (b.Length == 4)
            {
                if (ip.ToString() != text || b[0] == 0 || b[0] == 10 || b[0] == 127 || b[0] >= 224
                    || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
                    || (b[0] == 169 && b[1] == 254) || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                    || (b[0] == 192 && (b[1] == 168 || (b[1] == 0 && (b[2] == 0 || b[2] == 2)) || (b[1] == 88 && b[2] == 99)))
                    || (b[0] == 198 && (b[1] == 18 || b[1] == 19 || (b[1] == 51 && b[2] == 100)))
                    || (b[0] == 203 && b[1] == 0 && b[2] == 113)) return null;
            }
            else
            {
                if (b.Length != 16 || (b[0] & 0xe0) != 0x20 || (b[0] == 0x20 && b[1] == 0x02)
                    || (b[0] == 0x20 && b[1] == 0x01 && (b[2] < 2 || (b[2] == 0x0d && b[3] == 0xb8)))
                    || (b[0] == 0x3f && b[1] == 0xff && b[2] < 0x10)) return null;
            }
            return ip.ToString();
        }
    }
}
