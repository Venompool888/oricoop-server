using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace OriCoop.Authority
{
    // Both ends use the same versioned wire constraints. A collector can omit
    // an unsupported optional value without causing rejection of the whole form.
    internal static class JoinDiagnosticsFields
    {
        internal static bool Valid(string key, string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 100) return false;
            switch (key)
            {
                case "v": return value == "1";
                case "probe": return value == "ok" || value == "failed";
                case "provider": return value == "ipify" || value == "icanhazip";
                case "ip": return DiagnosticAddress.PublicLiteral(value) != null;
                case "os": return Regex.IsMatch(value, @"\A[0-9]{1,10}(?:\.[0-9]{1,10}){0,3}\z");
                case "arch": return value == "x86" || value == "x64" || value == "arm" || value == "arm64" || value == "ia64";
                case "systemLang":
                case "gameLang": return Regex.IsMatch(value, @"\A[A-Za-z0-9_-]{1,40}\z");
                case "timezone": return Regex.IsMatch(value, @"\A[A-Za-z0-9][A-Za-z0-9 ()/._+\-]{0,99}\z");
                case "utcOffset":
                    int minutes;
                    return Int32.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out minutes)
                        && minutes >= -840 && minutes <= 840;
                case "gameFs":
                case "saveFs": return Regex.IsMatch(value, @"\A[A-Za-z0-9._-]{1,32}\z");
                case "gameFree":
                case "saveFree": return value == "lt1GiB" || value == "1to10GiB" || value == "10to50GiB" || value == "ge50GiB";
                default: return false;
            }
        }
    }
}
