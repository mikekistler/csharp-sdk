using System.Net.Http.Headers;
using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace ModelContextProtocol.Client;

/// <summary>
/// Extracts parameter values from tool call arguments and adds them as HTTP headers
/// based on <c>x-mcp-header</c> schema extensions.
/// </summary>
/// <remarks>
/// <para>
/// This class inspects a tool's input schema for properties marked with the <c>x-mcp-header</c>
/// extension and extracts corresponding argument values to be sent as HTTP headers.
/// </para>
/// <para>
/// The header name format is <c>Mcp-Param-{Name}</c> where <c>Name</c> is the value of the
/// <c>x-mcp-header</c> extension property.
/// </para>
/// </remarks>
internal static class McpHeaderExtractor
{
    /// <summary>
    /// The JSON property name for the x-mcp-header extension in tool schemas.
    /// </summary>
    private const string XMcpHeaderProperty = "x-mcp-header";

    /// <summary>
    /// Adds custom parameter headers to an HTTP request based on a tool's schema extensions.
    /// </summary>
    /// <param name="headers">The HTTP request headers to add to.</param>
    /// <param name="tool">The tool definition containing the input schema with x-mcp-header annotations.</param>
    /// <param name="arguments">The arguments being passed to the tool call.</param>
    /// <remarks>
    /// <para>
    /// This method inspects the tool's <see cref="Tool.InputSchema"/> for properties with
    /// <c>x-mcp-header</c> annotations and adds corresponding <c>Mcp-Param-{Name}</c> headers
    /// for any matching arguments.
    /// </para>
    /// <para>
    /// Arguments that are <see langword="null"/> or missing result in the header being omitted
    /// (not set to an empty value), per the SEP specification.
    /// </para>
    /// </remarks>
    public static void AddParameterHeaders(
        HttpRequestHeaders headers,
        Tool tool,
        JsonElement? arguments)
    {
        if (!arguments.HasValue || arguments.Value.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        // Get the properties from the tool's input schema
        if (tool.InputSchema.ValueKind != JsonValueKind.Object ||
            !tool.InputSchema.TryGetProperty("properties", out var properties) ||
            properties.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        // Iterate through schema properties looking for x-mcp-header annotations
        foreach (var property in properties.EnumerateObject())
        {
            if (!property.Value.TryGetProperty(XMcpHeaderProperty, out var headerNameElement))
            {
                continue;
            }

            var headerName = headerNameElement.GetString();
            if (string.IsNullOrEmpty(headerName))
            {
                continue;
            }

            // Look for the corresponding argument value
            if (!arguments.Value.TryGetProperty(property.Name, out var argValue))
            {
                // Argument not provided - omit header per SEP
                continue;
            }

            // Handle null values - omit header per SEP
            if (argValue.ValueKind == JsonValueKind.Null)
            {
                continue;
            }

            // Convert the argument value to a header-safe string
            var headerValue = ConvertJsonElementToHeaderValue(argValue);
            if (headerValue is not null)
            {
                headers.Add($"{McpHttpHeaders.ParamPrefix}{headerName}", headerValue);
            }
        }
    }

    /// <summary>
    /// Converts a JSON element to an encoded header value.
    /// </summary>
    /// <param name="element">The JSON element to convert.</param>
    /// <returns>
    /// The encoded header value, or <see langword="null"/> if the element cannot be converted
    /// (e.g., unsupported type or exceeds length limit).
    /// </returns>
    private static string? ConvertJsonElementToHeaderValue(JsonElement element)
    {
        object? value = element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetDouble(), // Let encoder handle formatting
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null // Arrays, objects, null - not supported for headers
        };

        return McpHeaderEncoder.EncodeValue(value);
    }
}
