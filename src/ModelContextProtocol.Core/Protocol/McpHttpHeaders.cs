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

    // ===== New headers from the HTTP Standardization Proposal =====

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
