# Implementation Plan: HTTP Standardization Proposal for MCP C# SDK

## Overview

This plan implements the HTTP Standardization Proposal which aims to expose MCP routing information in standard HTTP headers for better compatibility with network infrastructure (load balancers, proxies, gateways, observability tools).

**Goal:** Improve compatibility with existing network infrastructure by exposing critical routing and context information in standard HTTP locations, rather than burying it solely within the JSON-RPC payload.

**Benefits:**
- Load balancers can route requests without deep packet inspection
- Rate limiting solutions can operate on headers
- Authorization gateways can inspect method/tool information
- Observability tools can extract metrics from headers
- Web application firewalls can apply rules based on MCP operations

---

## Phase 1: Standard Headers (Required)

### 1.1 Define Header Constants

**File:** Create new file `src/ModelContextProtocol.Core/Protocol/McpHttpHeaders.cs`

> **Note:** The headers `Mcp-Session-Id` and `MCP-Protocol-Version` already exist in the codebase but are currently defined as:
> - A private constant in `StreamableHttpHandler.cs`: `private const string McpSessionIdHeaderName = "Mcp-Session-Id";`
> - String literals in `StreamableHttpClientSessionTransport.cs`: `"Mcp-Session-Id"` and `"MCP-Protocol-Version"`
>
> This task will **centralize** these existing definitions into the new `McpHttpHeaders` class and **update all existing usages** to reference the centralized constants.

```csharp
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

    // ===== New headers from this proposal =====

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
```

### 1.1a Update Existing Usages to Use Centralized Constants

**Files to update:**

| File | Current Code | Change To |
|------|--------------|-----------|
| `StreamableHttpHandler.cs` | `private const string McpSessionIdHeaderName = "Mcp-Session-Id";` | Remove local constant, use `McpHttpHeaders.SessionId` |
| `StreamableHttpClientSessionTransport.cs` | `headers.Add("Mcp-Session-Id", sessionId);` | `headers.Add(McpHttpHeaders.SessionId, sessionId);` |
| `StreamableHttpClientSessionTransport.cs` | `headers.Add("MCP-Protocol-Version", protocolVersion);` | `headers.Add(McpHttpHeaders.ProtocolVersion, protocolVersion);` |
| `StreamableHttpClientSessionTransport.cs` | `response.Headers.TryGetValues("Mcp-Session-Id", ...)` | `response.Headers.TryGetValues(McpHttpHeaders.SessionId, ...)` |

### 1.2 Update Client Transport to Add Standard Headers

**File:** `src/ModelContextProtocol.Core/Client/StreamableHttpClientSessionTransport.cs`

**Changes:**
- Modify `SendHttpRequestAsync` to extract method and params from the `JsonRpcMessage`
- Add `Mcp-Method` header for all requests
- Add `Mcp-Tool-Name` header for `tools/call` requests (from `params.name`)
- Add `Mcp-Resource` header for `resources/read` requests (from `params.uri`)
- Add `Mcp-Prompt-Name` header for `prompts/get` requests (from `params.name`)

**Implementation approach:**
1. Create a new method `AddMcpStandardHeaders(HttpRequestHeaders headers, JsonRpcMessage message)`
2. Parse the message to extract method and relevant params
3. Call this method in `SendHttpRequestAsync` after `CopyAdditionalHeaders`

```csharp
internal static void AddMcpStandardHeaders(HttpRequestHeaders headers, JsonRpcMessage message)
{
    if (message is not JsonRpcRequest request)
    {
        return;
    }

    // Always add the method header
    headers.Add(McpHttpHeaders.Method, request.Method);

    // Add method-specific headers based on params
    switch (request.Method)
    {
        case RequestMethods.ToolsCall:
            if (request.Params is JsonElement toolParams &&
                toolParams.TryGetProperty("name", out var toolName))
            {
                headers.Add(McpHttpHeaders.ToolName, toolName.GetString());
            }
            break;

        case RequestMethods.ResourcesRead:
            if (request.Params is JsonElement resourceParams &&
                resourceParams.TryGetProperty("uri", out var resourceUri))
            {
                headers.Add(McpHttpHeaders.ResourceUri, resourceUri.GetString());
            }
            break;

        case RequestMethods.PromptsGet:
            if (request.Params is JsonElement promptParams &&
                promptParams.TryGetProperty("name", out var promptName))
            {
                headers.Add(McpHttpHeaders.PromptName, promptName.GetString());
            }
            break;
    }
}
```

### 1.3 Update SSE Client Transport

**File:** `src/ModelContextProtocol.Core/Client/SseClientSessionTransport.cs`

Apply the same header additions to `SendMessageAsync` by calling the shared `AddMcpStandardHeaders` method.

### 1.4 Server-Side Validation

**File:** `src/ModelContextProtocol.AspNetCore/StreamableHttpHandler.cs`

**Changes:**
- Add header validation in request handling
- Compare header values against request body values
- Return 400 Bad Request if headers don't match body values
- Implement case-insensitive header name comparison

```csharp
private static bool ValidateMcpHeaders(HttpContext context, JsonRpcRequest request)
{
    // Validate Mcp-Method header matches request.Method
    if (context.Request.Headers.TryGetValue(McpHttpHeaders.Method, out var methodHeader))
    {
        if (!string.Equals(methodHeader, request.Method, StringComparison.Ordinal))
        {
            return false;
        }
    }

    // Validate method-specific headers...
    return true;
}
```

---

## Phase 2: Custom Headers from Tool Parameters

### 2.1 Define `McpHeaderAttribute`

**File:** Create new file `src/ModelContextProtocol.Core/Server/McpHeaderAttribute.cs`

```csharp
namespace ModelContextProtocol.Server;

/// <summary>
/// Indicates that a tool parameter should be mirrored as an HTTP header in client requests.
/// </summary>
/// <remarks>
/// <para>
/// When applied to a parameter, the SDK will include an <c>x-mcp-header</c> extension property
/// in the parameter's JSON schema. Clients will then mirror this parameter's value into an
/// HTTP header named <c>Mcp-Param-{Name}</c>.
/// </para>
/// <para>
/// Only parameters with primitive types (string, number, boolean) may use this attribute.
/// The header name must contain only ASCII characters (excluding space and ":") and must be
/// case-insensitive unique within the tool's input schema.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// [McpServerTool]
/// public static string ExecuteSql(
///     [McpHeader("Region")] string region,
///     string query)
/// {
///     // The client will add header: Mcp-Param-Region: {region value}
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property)]
public sealed class McpHeaderAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="McpHeaderAttribute"/> class.
    /// </summary>
    /// <param name="name">
    /// The name portion of the header. The full header name will be <c>Mcp-Param-{name}</c>.
    /// Must contain only ASCII characters (excluding space and ":").
    /// </param>
    /// <exception cref="ArgumentException">
    /// The name contains invalid characters or is empty.
    /// </exception>
    public McpHeaderAttribute(string name)
    {
        Throw.IfNullOrWhiteSpace(name);
        ValidateHeaderName(name);
        Name = name;
    }

    /// <summary>
    /// Gets the name portion of the header.
    /// </summary>
    public string Name { get; }

    private static void ValidateHeaderName(string name)
    {
        foreach (char c in name)
        {
            // Valid token characters per RFC 9110
            if (c < 0x21 || c > 0x7E || c == ':' || c == ' ')
            {
                throw new ArgumentException(
                    $"Header name contains invalid character '{c}'. " +
                    "Only ASCII characters (0x21-0x7E) excluding space and ':' are allowed.",
                    nameof(name));
            }
        }
    }
}
```

### 2.2 Modify AIFunction JSON Schema Generation

**File:** `src/ModelContextProtocol.Core/Server/AIFunctionMcpServerTool.cs`

**Changes:**
- During tool creation, detect `[McpHeader]` attributes on parameters
- Add `x-mcp-header` extension property to the parameter's JSON schema
- Validate that only primitive types have `[McpHeader]`
- Validate case-insensitive uniqueness of header names within the tool

**Key implementation points:**
1. In the schema generation logic, check for `McpHeaderAttribute` on each parameter
2. For parameters with the attribute, modify the JSON schema to include:
   ```json
   {
     "type": "string",
     "x-mcp-header": "Region"
   }
   ```
3. Throw if the parameter type is not primitive (string, number, boolean, integer)
4. Track header names and throw if duplicates exist (case-insensitive)

### 2.3 Client-Side Header Extraction

**File:** Create new file `src/ModelContextProtocol.Core/Client/McpHeaderExtractor.cs`

```csharp
namespace ModelContextProtocol.Client;

/// <summary>
/// Extracts parameter values to HTTP headers based on x-mcp-header schema extensions.
/// </summary>
internal static class McpHeaderExtractor
{
    /// <summary>
    /// Inspects a tool's input schema for properties marked with x-mcp-header and adds
    /// corresponding HTTP headers for the provided argument values.
    /// </summary>
    public static void AddParameterHeaders(
        HttpRequestHeaders headers,
        Tool tool,
        IDictionary<string, JsonElement>? arguments)
    {
        if (arguments is null)
        {
            return;
        }

        // Parse tool.InputSchema for properties with x-mcp-header
        if (!tool.InputSchema.TryGetProperty("properties", out var properties))
        {
            return;
        }

        foreach (var property in properties.EnumerateObject())
        {
            if (!property.Value.TryGetProperty("x-mcp-header", out var headerNameElement))
            {
                continue;
            }

            var headerName = headerNameElement.GetString();
            if (string.IsNullOrEmpty(headerName))
            {
                continue;
            }

            // Get the argument value
            if (!arguments.TryGetValue(property.Name, out var argValue))
            {
                continue;
            }

            // Convert to string and sanitize
            var headerValue = ConvertToHeaderValue(argValue);
            if (headerValue is not null)
            {
                headers.Add($"{McpHttpHeaders.ParamPrefix}{headerName}", headerValue);
            }
        }
    }

    private static string? ConvertToHeaderValue(JsonElement element)
    {
        var value = element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };

        return SanitizeHeaderValue(value);
    }

    private static string? SanitizeHeaderValue(string? value)
    {
        if (value is null)
        {
            return null;
        }

        // Remove non-ASCII and control characters
        var sb = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            if (c >= 0x20 && c <= 0x7E && c != '\r' && c != '\n')
            {
                sb.Append(c);
            }
        }
        return sb.Length > 0 ? sb.ToString() : null;
    }
}
```

### 2.4 Integrate Header Extraction in Client Transport

**File:** `src/ModelContextProtocol.Core/Client/StreamableHttpClientSessionTransport.cs`

**Changes:**
- For `tools/call` requests, look up the tool from the client's tool cache
- Call `McpHeaderExtractor.AddParameterHeaders` to add custom parameter headers
- Handle case where tool schema is not available (skip custom headers gracefully)

### 2.5 Client-Side Tool Caching

**File:** `src/ModelContextProtocol.Core/Client/McpClient.cs`

**Changes:**
- Add a `ConcurrentDictionary<string, Tool>` to cache tool definitions
- Populate cache after `ListToolsAsync` calls
- Expose internal method `TryGetCachedTool(string name, out Tool tool)` for header extraction
- Consider invalidation on `tools/list_changed` notifications

---

## Phase 3: Protocol Types Updates

### 3.1 Verify Extension Property Preservation

**File:** `src/ModelContextProtocol.Core/Protocol/Tool.cs`

**Verification:** The current `JsonElement` storage for `InputSchema` should already preserve `x-mcp-header` extension properties during serialization/deserialization. Add tests to verify this behavior.

---

## Phase 4: Server-Side Validation (Optional Enhancement)

### 4.1 Validate Custom Headers on Server

**File:** `src/ModelContextProtocol.AspNetCore/StreamableHttpHandler.cs`

**Changes:**
- For `tools/call` requests, validate that `Mcp-Param-*` headers match argument values
- Look up tool definition to find expected parameter headers
- Return 400 if header value doesn't match body value

---

## Phase 5: Testing

### 5.1 Unit Tests

**File:** Create `tests/ModelContextProtocol.Tests/Client/McpHeaderExtractorTests.cs`
- Test extraction of `x-mcp-header` properties from JSON schema
- Test sanitization of header values (ASCII-only, no newlines)
- Test handling of missing arguments
- Test all primitive types (string, number, boolean)

**File:** Create `tests/ModelContextProtocol.Tests/Server/McpHeaderAttributeTests.cs`
- Test attribute validation (ASCII-only names)
- Test rejection of invalid characters (space, colon, non-ASCII)
- Test schema generation includes `x-mcp-header`
- Test error on non-primitive parameter types
- Test case-insensitive uniqueness validation

**File:** Update `tests/ModelContextProtocol.Tests/Server/McpServerToolTests.cs`
- Test that `[McpHeader]` attribute generates correct schema extension
- Test error when applied to non-primitive parameter types
- Test case-insensitive uniqueness validation across parameters

### 5.2 Integration Tests

**File:** Update `tests/ModelContextProtocol.AspNetCore.Tests/MapMcpStreamableHttpTests.cs`
- Test that `Mcp-Method` header is sent for all requests
- Test that `Mcp-Tool-Name` header is sent for `tools/call`
- Test that `Mcp-Resource` header is sent for `resources/read`
- Test that `Mcp-Prompt-Name` header is sent for `prompts/get`
- Test server validation rejects mismatched headers
- Test custom `Mcp-Param-*` headers flow correctly
- Test case-insensitive header name handling

---

## Phase 6: Documentation

### 6.1 Update XML Documentation
- Add comprehensive XML docs to all new types
- Update existing transport documentation to mention header requirements

### 6.2 Update Conceptual Documentation
- Create `docs/concepts/http-headers.md` explaining the header protocol
- Document the `[McpHeader]` attribute usage with examples
- Explain backward compatibility considerations

---

## Implementation Order

| Priority | Task | Estimated Effort | Dependencies |
|----------|------|------------------|--------------|
| 1 | Define `McpHttpHeaders` constants | Small | None |
| 2 | Add standard headers in `StreamableHttpClientSessionTransport` | Medium | 1 |
| 3 | Add standard headers in `SseClientSessionTransport` | Small | 1, 2 |
| 4 | Server-side validation of standard headers | Medium | 1 |
| 5 | Create `McpHeaderAttribute` | Small | None |
| 6 | Update schema generation for `x-mcp-header` | Medium | 5 |
| 7 | Create `McpHeaderExtractor` | Medium | 1 |
| 8 | Client-side tool caching in `McpClient` | Medium | None |
| 9 | Integrate header extraction in client transport | Medium | 7, 8 |
| 10 | Server-side custom header validation | Medium | 4, 6 |
| 11 | Unit tests | Large | 1-10 |
| 12 | Integration tests | Large | 1-10 |
| 13 | Documentation | Medium | 1-10 |

---

## Key Implementation Details

### Header Name Case Insensitivity
Per the proposal and RFC 9110, header names are case-insensitive. Use `StringComparer.OrdinalIgnoreCase` for all header name comparisons.

### Value Sanitization
When adding parameter values to headers:
- Ensure ASCII-only characters (0x20-0x7E)
- Remove/escape newlines (`\r`, `\n`)
- Consider practical HTTP header size limits (~8KB typical)

### Backward Compatibility
- Standard headers are **required** for the new MCP version
- Servers **must** validate headers match body values
- Consider version negotiation to determine header requirements
- Older clients won't send headers; servers should reject based on protocol version

### Error Handling
- Missing required headers → 400 Bad Request
- Header/body mismatch → 400 Bad Request with descriptive message
- Invalid header characters → sanitize on client, reject on server if needed

---

## Files Summary

### New Files

| File | Purpose |
|------|---------|
| `src/ModelContextProtocol.Core/Protocol/McpHttpHeaders.cs` | Header name constants |
| `src/ModelContextProtocol.Core/Server/McpHeaderAttribute.cs` | Attribute for parameter headers |
| `src/ModelContextProtocol.Core/Client/McpHeaderExtractor.cs` | Extract params to headers |
| `tests/ModelContextProtocol.Tests/Client/McpHeaderExtractorTests.cs` | Unit tests |
| `tests/ModelContextProtocol.Tests/Server/McpHeaderAttributeTests.cs` | Unit tests |
| `docs/concepts/http-headers.md` | Conceptual documentation |

### Modified Files

| File | Change |
|------|--------|
| `src/ModelContextProtocol.Core/Client/StreamableHttpClientSessionTransport.cs` | Add standard + custom headers |
| `src/ModelContextProtocol.Core/Client/SseClientSessionTransport.cs` | Add standard headers |
| `src/ModelContextProtocol.Core/Client/McpClient.cs` | Tool caching |
| `src/ModelContextProtocol.Core/Server/AIFunctionMcpServerTool.cs` | Schema generation for `x-mcp-header` |
| `src/ModelContextProtocol.AspNetCore/StreamableHttpHandler.cs` | Header validation |
| `tests/ModelContextProtocol.AspNetCore.Tests/MapMcpStreamableHttpTests.cs` | Integration tests |
| `tests/ModelContextProtocol.Tests/Server/McpServerToolTests.cs` | Attribute tests |

---

## Open Questions

1. **Protocol Version Gating:** Should header validation be gated on the negotiated protocol version? This would allow older clients to continue working.

2. **Header Size Limits:** Should we enforce maximum lengths for custom parameter header values?

3. **Complex Types:** The proposal currently restricts `x-mcp-header` to primitive types. Should we consider JSON serialization for arrays/objects in the future?

4. **Notification Messages:** Should notifications also include the `Mcp-Method` header, or only requests?

5. **GET Requests:** The Streamable HTTP transport supports optional GET requests for unsolicited messages. Do headers apply there?
