using System;
using System.Security.Cryptography;
using System.Text;

namespace OriCoop.Authority
{
    // Both ends already hold the random seat token from CsWelcome. Distinct HMAC
    // labels let HTTPS prove it belongs to the joined server without seeing a
    // credential that could be used to reclaim the player's seat.
    internal static class JoinDiagnosticsKey
    {
        public static string RequestId(byte[] joinToken)
        {
            return Derive(joinToken, "request");
        }

        public static string Capability(byte[] joinToken)
        {
            return "oricoop-diagnostics-v1." + Derive(joinToken, "capability");
        }

        private static string Derive(byte[] joinToken, string purpose)
        {
            if (joinToken == null || joinToken.Length != 16)
                throw new ArgumentException("Expected the token from a successful CsWelcome.", "joinToken");

            using (HMACSHA256 hmac = new HMACSHA256(joinToken))
            {
                byte[] digest = hmac.ComputeHash(Encoding.ASCII.GetBytes("oricoop-diagnostics-v1/" + purpose));
                return BitConverter.ToString(digest).Replace("-", "").ToLowerInvariant();
            }
        }
    }
}
