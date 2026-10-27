using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Inverge.Nexus.Telescope;

/// <summary>Removes secrets before anything leaves the process. The Nexus server redacts again.</summary>
internal static class Redactor
{
    public const string Redacted = "********";

    private static readonly HashSet<string> Defaults = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "authorization", "proxy-authorization", "cookie", "set-cookie", "x-api-key", "x-csrf-token", "x-xsrf-token",
        "token", "access_token", "refresh_token", "id_token", "api_key", "apikey", "client_secret", "private_key",
        "credit_card", "card_number", "cvv", "cvc", "ssn", "__requestverificationtoken",
    };

    public static JsonObject Redact(JsonObject value, IEnumerable<string> extra)
    {
        var keys = new HashSet<string>(Defaults.Concat(extra), StringComparer.OrdinalIgnoreCase);
        Walk(value, keys, 0);
        return value;
    }

    private static void Walk(JsonNode? node, HashSet<string> keys, int depth)
    {
        if (depth > 10 || node is null)
        {
            return;
        }

        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(p => p.Key).ToList())
            {
                if (keys.Contains(key) || key.Contains("password", StringComparison.OrdinalIgnoreCase) || key.Contains("secret", StringComparison.OrdinalIgnoreCase))
                {
                    var v = obj[key];
                    if (v is not null && v.ToString().Length > 0)
                    {
                        obj[key] = Redacted;
                    }
                }
                else
                {
                    Walk(obj[key], keys, depth + 1);
                }
            }
        }
        else if (node is JsonArray arr)
        {
            foreach (var item in arr)
            {
                Walk(item, keys, depth + 1);
            }
        }
    }

    /// <summary><c>200</c> (exact), <c>2xx</c> (class) or <c>301-304</c> (inclusive range) against a 3-digit status.</summary>
    public static bool StatusMatch(string pattern, string status)
    {
        if (status.Length != 3 || !status.All(char.IsAsciiDigit))
        {
            return false;
        }

        var p = pattern.Trim().ToLowerInvariant();
        if (p.Length == 3 && char.IsAsciiDigit(p[0]) && p.EndsWith("xx", StringComparison.Ordinal))
        {
            return p[0] == status[0];
        }

        var dash = p.IndexOf('-');
        if (dash > 0
            && int.TryParse(p.AsSpan(0, dash).Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var from)
            && int.TryParse(p.AsSpan(dash + 1).Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var to))
        {
            var code = int.Parse(status, System.Globalization.CultureInfo.InvariantCulture);
            return code >= from && code <= to;
        }

        return p == status;
    }

    public static bool GlobMatch(string glob, string value)
        => Regex.IsMatch(value, "^" + string.Join(".*", glob.Split('*').Select(Regex.Escape)) + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
}
