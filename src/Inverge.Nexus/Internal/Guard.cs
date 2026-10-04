using System;

namespace Inverge.Nexus.Internal;

/// <summary>Argument checks, so a mistake fails at the call site.</summary>
/// <remarks>
/// These validate locally rather than letting the server reject the request. A
/// blank id produces a 400 several milliseconds and one network round trip later,
/// with a message about a field name rather than a parameter name — far harder to
/// act on than an <see cref="ArgumentException"/> pointing at the argument.
/// </remarks>
internal static class Guard
{
    public static string NotBlank(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A value is required.", parameterName);
        }

        return value!;
    }

    public static void OneOf(string? value, string[] allowed, string parameterName)
    {
        NotBlank(value, parameterName);

        foreach (var candidate in allowed)
        {
            if (string.Equals(value, candidate, StringComparison.Ordinal))
            {
                return;
            }
        }

        throw new ArgumentException(
            "Must be one of: " + string.Join(", ", allowed) + ".", parameterName);
    }

    public static void AtMost(int count, int limit, string what, string parameterName)
    {
        if (count > limit)
        {
            throw new ArgumentException(
                "At most " + limit.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " " + what + " per call.",
                parameterName);
        }
    }
}
