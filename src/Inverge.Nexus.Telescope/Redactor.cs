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

    public static bool GlobMatch(string glob, string value)
        => Regex.IsMatch(value, "^" + string.Join(".*", glob.Split('*').Select(Regex.Escape)) + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
}
