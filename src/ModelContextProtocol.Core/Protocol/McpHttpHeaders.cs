namespace ModelContextProtocol.Protocol;

/// <summary>
/// Constants for MCP-specific HTTP header names used in the Streamable HTTP transport.
/// </summary>
/// <remarks>
/// Per RFC 9110, HTTP header names are case-insensitive. Clients and servers must
/// use case-insensitive comparisons when processing these headers.
/// </remarks>
public static class McpHttpHeaders
{
    // ===== Existing headers (centralized from scattered string literals) =====

    /// <summary>The session identifier.</summary>
    public const string SessionId = "Mcp-Session-Id";

    /// <summary>The negotiated protocol version.</summary>
    public const string ProtocolVersion = "MCP-Protocol-Version";

    /// <summary>The last event ID for SSE stream resumption.</summary>
    public const string LastEventId = "Last-Event-ID";

    // ===== New headers from the HTTP Standardization Proposal =====

    /// <summary>
    /// The minimum protocol version that requires HTTP header validation.
    /// </summary>
    /// <remarks>
    /// This is a placeholder version for the HTTP Standardization SEP.
    /// Update this value when the SEP is finalized and assigned a real version.
    /// </remarks>
    public const string MinVersionForHeaderValidation = "2026-mm-dd";

    /// <summary>The JSON-RPC method being invoked (e.g., "tools/call", "resources/read").</summary>
    public const string Method = "Mcp-Method";

    /// <summary>The name of the tool being called (for tools/call requests).</summary>
    public const string ToolName = "Mcp-Tool-Name";

    /// <summary>The URI of the resource being read (for resources/read requests).</summary>
    public const string ResourceUri = "Mcp-Resource";

    /// <summary>The name of the prompt being retrieved (for prompts/get requests).</summary>
    public const string PromptName = "Mcp-Prompt-Name";

    /// <summary>Prefix for custom parameter headers (Mcp-Param-{Name}).</summary>
    public const string ParamPrefix = "Mcp-Param-";
}
