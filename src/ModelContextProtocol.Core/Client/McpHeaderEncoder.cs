using System.Text;

namespace ModelContextProtocol.Client;

/// <summary>
/// Encodes parameter values for use in MCP HTTP headers according to the HTTP Standardization SEP.
/// </summary>
/// <remarks>
/// <para>
/// This encoder handles conversion of parameter values to HTTP header-safe strings,
/// including Base64 encoding for values that cannot be safely transmitted as plain text.
/// </para>
/// <para>
/// Encoding rules:
/// <list type="bullet">
/// <item><description>Plain ASCII values (0x21-0x7E, space, tab): sent as-is</description></item>
/// <item><description>Values with leading/trailing whitespace: Base64 encoded with <c>=?base64?{value}?=</c> wrapper</description></item>
/// <item><description>Non-ASCII characters: Base64 encoded</description></item>
/// <item><description>Control characters: Base64 encoded</description></item>
/// <item><description>Values exceeding 8192 bytes: header is omitted</description></item>
/// </list>
/// </para>
/// </remarks>
internal static class McpHeaderEncoder
{
    /// <summary>
    /// Maximum allowed length for encoded header values in bytes.
    /// Values exceeding this limit must be omitted.
    /// </summary>
    public const int MaxHeaderValueLength = 8192;

    /// <summary>
    /// Prefix for Base64-encoded values.
    /// </summary>
    private const string Base64Prefix = "=?base64?";

    /// <summary>
    /// Suffix for Base64-encoded values.
    /// </summary>
    private const string Base64Suffix = "?=";

    /// <summary>
    /// Encodes a parameter value for use in an HTTP header.
    /// </summary>
    /// <param name="value">The value to encode. Can be string, number, or boolean.</param>
    /// <returns>
    /// The encoded header value, or <see langword="null"/> if the value cannot be encoded
    /// (e.g., exceeds length limit or is not a supported type).
    /// </returns>
    public static string? EncodeValue(object? value)
    {
        if (value is null)
        {
            return null;
        }

        // Step 1: Type conversion to string representation
        var stringValue = ConvertToString(value);
        if (stringValue is null)
        {
            return null;
        }

        // Step 2: Check if Base64 encoding is needed
        if (RequiresBase64Encoding(stringValue))
        {
            return EncodeAsBase64(stringValue);
        }

        // Step 3: Length validation for plain values
        if (stringValue.Length > MaxHeaderValueLength)
        {
            return null;
        }

        return stringValue;
    }

    /// <summary>
    /// Converts a typed value to its string representation according to SEP rules.
    /// </summary>
    /// <param name="value">The value to convert.</param>
    /// <returns>The string representation, or <see langword="null"/> for unsupported types.</returns>
    private static string? ConvertToString(object value)
    {
        return value switch
        {
            string s => s,
            bool b => b ? "true" : "false",
            // Handle all numeric types - use invariant culture for consistent formatting
            byte n => n.ToString(System.Globalization.CultureInfo.InvariantCulture),
            sbyte n => n.ToString(System.Globalization.CultureInfo.InvariantCulture),
            short n => n.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ushort n => n.ToString(System.Globalization.CultureInfo.InvariantCulture),
            int n => n.ToString(System.Globalization.CultureInfo.InvariantCulture),
            uint n => n.ToString(System.Globalization.CultureInfo.InvariantCulture),
            long n => n.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ulong n => n.ToString(System.Globalization.CultureInfo.InvariantCulture),
            float n => n.ToString(System.Globalization.CultureInfo.InvariantCulture),
            double n => n.ToString(System.Globalization.CultureInfo.InvariantCulture),
            decimal n => n.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => null // Unsupported type (arrays, objects, etc.)
        };
    }

    /// <summary>
    /// Determines whether a string value requires Base64 encoding for safe HTTP header transmission.
    /// </summary>
    /// <param name="value">The string value to check.</param>
    /// <returns><see langword="true"/> if Base64 encoding is required; otherwise, <see langword="false"/>.</returns>
    private static bool RequiresBase64Encoding(string value)
    {
        if (value.Length == 0)
        {
            return false;
        }

        // Check for leading/trailing whitespace (space or tab)
        if (value[0] == ' ' || value[0] == '\t' ||
            value[^1] == ' ' || value[^1] == '\t')
        {
            return true;
        }

        // Check each character for validity
        foreach (char c in value)
        {
            // Valid HTTP header field value characters: visible ASCII (0x21-0x7E), space (0x20), tab (0x09)
            // Control characters (0x00-0x1F, 0x7F) and non-ASCII (> 0x7F) require encoding
            if (c < 0x20 || c > 0x7E)
            {
                // Allow space and tab only (already checked for leading/trailing above)
                if (c != '\t')
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Encodes a string value as Base64 with the SEP-defined wrapper format.
    /// </summary>
    /// <param name="value">The string value to encode.</param>
    /// <returns>
    /// The Base64-encoded value with wrapper, or <see langword="null"/> if the encoded value exceeds the length limit.
    /// </returns>
    private static string? EncodeAsBase64(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var base64 = Convert.ToBase64String(bytes);
        var encoded = $"{Base64Prefix}{base64}{Base64Suffix}";

        if (encoded.Length > MaxHeaderValueLength)
        {
            return null;
        }

        return encoded;
    }

    /// <summary>
    /// Decodes a header value that may be Base64-encoded according to SEP rules.
    /// </summary>
    /// <param name="headerValue">The header value to decode.</param>
    /// <returns>
    /// The decoded string value, or <see langword="null"/> if decoding fails.
    /// If the value is not Base64-encoded, returns the original value.
    /// </returns>
    public static string? DecodeValue(string? headerValue)
    {
        if (headerValue is null || headerValue.Length == 0)
        {
            return headerValue;
        }

        // Check for Base64 wrapper (case-insensitive prefix check per SEP)
        if (headerValue.StartsWith(Base64Prefix, StringComparison.OrdinalIgnoreCase) &&
            headerValue.EndsWith(Base64Suffix, StringComparison.Ordinal))
        {
            // Extract the Base64 content
            var base64Content = headerValue.Substring(
                Base64Prefix.Length,
                headerValue.Length - Base64Prefix.Length - Base64Suffix.Length);

            try
            {
                var bytes = Convert.FromBase64String(base64Content);
                return Encoding.UTF8.GetString(bytes);
            }
            catch (FormatException)
            {
                // Invalid Base64 - return null to indicate decoding failure
                return null;
            }
        }

        // Not Base64-encoded, return as-is
        return headerValue;
    }
}
