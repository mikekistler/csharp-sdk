using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json.Serialization.Metadata;

namespace ModelContextProtocol.AspNetCore;

internal sealed class StreamableHttpHandler(
    IOptions<McpServerOptions> mcpServerOptionsSnapshot,
    IOptionsFactory<McpServerOptions> mcpServerOptionsFactory,
    IOptions<HttpServerTransportOptions> httpServerTransportOptions,
    StatefulSessionManager sessionManager,
    IHostApplicationLifetime hostApplicationLifetime,
    IServiceProvider applicationServices,
    ILoggerFactory loggerFactory)
{
    private static readonly JsonTypeInfo<JsonRpcMessage> s_messageTypeInfo = GetRequiredJsonTypeInfo<JsonRpcMessage>();
    private static readonly JsonTypeInfo<JsonRpcError> s_errorTypeInfo = GetRequiredJsonTypeInfo<JsonRpcError>();

    public HttpServerTransportOptions HttpServerTransportOptions => httpServerTransportOptions.Value;

    public async Task HandlePostRequestAsync(HttpContext context)
    {
        // The Streamable HTTP spec mandates the client MUST accept both application/json and text/event-stream.
        // ASP.NET Core Minimal APIs mostly try to stay out of the business of response content negotiation,
        // so we have to do this manually. The spec doesn't mandate that servers MUST reject these requests,
        // but it's probably good to at least start out trying to be strict.
        var typedHeaders = context.Request.GetTypedHeaders();
        if (!typedHeaders.Accept.Any(MatchesApplicationJsonMediaType) || !typedHeaders.Accept.Any(MatchesTextEventStreamMediaType))
        {
            await WriteJsonRpcErrorAsync(context,
                "Not Acceptable: Client must accept both application/json and text/event-stream",
                StatusCodes.Status406NotAcceptable);
            return;
        }

        var session = await GetOrCreateSessionAsync(context);
        if (session is null)
        {
            return;
        }

        await using var _ = await session.AcquireReferenceAsync(context.RequestAborted);

        var message = await ReadJsonRpcMessageAsync(context);
        if (message is null)
        {
            await WriteJsonRpcErrorAsync(context,
                "Bad Request: The POST body did not contain a valid JSON-RPC message.",
                StatusCodes.Status400BadRequest);
            return;
        }

        // Validate MCP headers match the request body values per the HTTP Standardization SEP.
        // Servers MUST reject requests where header values don't match body values.
        // Header validation is only enforced for protocol versions >= MinVersionForHeaderValidation.
        // If no protocol version header is present (e.g., initialize request), validation is skipped.
        var requestProtocolVersion = context.Request.Headers[McpHttpHeaders.ProtocolVersion].ToString();
        if (message is JsonRpcRequest request &&
            IsHeaderValidationRequired(requestProtocolVersion))
        {
            var validationError = ValidateMcpHeaders(context.Request.Headers, request);
            if (validationError is not null)
            {
                await WriteJsonRpcErrorAsync(context,
                    validationError,
                    StatusCodes.Status400BadRequest,
                    (int)McpErrorCode.HeaderMismatch);
                return;
            }
        }

        InitializeSseResponse(context);
        var wroteResponse = await session.Transport.HandlePostRequestAsync(message, context.Response.Body, context.RequestAborted);
        if (!wroteResponse)
        {
            // We wound up writing nothing, so there should be no Content-Type response header.
            context.Response.Headers.ContentType = (string?)null;
            context.Response.StatusCode = StatusCodes.Status202Accepted;
        }
    }

    public async Task HandleGetRequestAsync(HttpContext context)
    {
        if (!context.Request.GetTypedHeaders().Accept.Any(MatchesTextEventStreamMediaType))
        {
            await WriteJsonRpcErrorAsync(context,
                "Not Acceptable: Client must accept text/event-stream",
                StatusCodes.Status406NotAcceptable);
            return;
        }

        var sessionId = context.Request.Headers[McpHttpHeaders.SessionId].ToString();
        var session = await GetSessionAsync(context, sessionId);
        if (session is null)
        {
            return;
        }

        var lastEventId = context.Request.Headers[McpHttpHeaders.LastEventId].ToString();
        if (!string.IsNullOrEmpty(lastEventId))
        {
            await HandleResumedStreamAsync(context, session, lastEventId);
        }
        else
        {
            await HandleUnsolicitedMessageStreamAsync(context, session);
        }
    }

    private async Task HandleResumedStreamAsync(HttpContext context, StreamableHttpSession session, string lastEventId)
    {
        if (HttpServerTransportOptions.Stateless)
        {
            await WriteJsonRpcErrorAsync(context,
                "Bad Request: The Last-Event-ID header is not supported in stateless mode.",
                StatusCodes.Status400BadRequest);
            return;
        }

        var eventStreamReader = await GetEventStreamReaderAsync(context, lastEventId);
        if (eventStreamReader is null)
        {
            // There was an error obtaining the event stream; consider the request failed.
            return;
        }

        if (!string.Equals(session.Id, eventStreamReader.SessionId, StringComparison.Ordinal))
        {
            await WriteJsonRpcErrorAsync(context,
                "Bad Request: The Last-Event-ID header refers to a session with a different session ID.",
                StatusCodes.Status400BadRequest);
            return;
        }

        using var sseCts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, hostApplicationLifetime.ApplicationStopping);
        var cancellationToken = sseCts.Token;

        await using var _ = await session.AcquireReferenceAsync(cancellationToken);

        InitializeSseResponse(context);
        await eventStreamReader.CopyToAsync(context.Response.Body, context.RequestAborted);
    }

    private async Task HandleUnsolicitedMessageStreamAsync(HttpContext context, StreamableHttpSession session)
    {
        if (!session.TryStartGetRequest())
        {
            await WriteJsonRpcErrorAsync(context,
                "Bad Request: This server does not support multiple GET requests. Start a new session or use Last-Event-ID header to resume.",
                StatusCodes.Status400BadRequest);
            return;
        }

        // Link the GET request to both RequestAborted and ApplicationStopping.
        // The GET request should complete immediately during graceful shutdown without waiting for
        // in-flight POST requests to complete. This prevents slow shutdown when clients are still connected.
        using var sseCts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, hostApplicationLifetime.ApplicationStopping);
        var cancellationToken = sseCts.Token;

        try
        {
            await using var _ = await session.AcquireReferenceAsync(cancellationToken);
            InitializeSseResponse(context);
            await session.Transport.HandleGetRequestAsync(context.Response.Body, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // RequestAborted always triggers when the client disconnects before a complete response body is written,
            // but this is how SSE connections are typically closed.
        }
    }

    private static async Task HandleResumePostResponseStreamAsync(HttpContext context, ISseEventStreamReader eventStreamReader)
    {
        InitializeSseResponse(context);
        await eventStreamReader.CopyToAsync(context.Response.Body, context.RequestAborted);
    }

    public async Task HandleDeleteRequestAsync(HttpContext context)
    {
        var sessionId = context.Request.Headers[McpHttpHeaders.SessionId].ToString();
        if (sessionManager.TryRemove(sessionId, out var session))
        {
            await session.DisposeAsync();
        }
    }

    private async ValueTask<StreamableHttpSession?> GetSessionAsync(HttpContext context, string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
        {
            await WriteJsonRpcErrorAsync(context, "Bad Request: Mcp-Session-Id header is required", StatusCodes.Status400BadRequest);
            return null;
        }

        if (!sessionManager.TryGetValue(sessionId, out var session))
        {
            // -32001 isn't part of the MCP standard, but this is what the typescript-sdk currently does.
            // One of the few other usages I found was from some Ethereum JSON-RPC documentation and this
            // JSON-RPC library from Microsoft called StreamJsonRpc where it's called JsonRpcErrorCode.NoMarshaledObjectFound
            // https://learn.microsoft.com/dotnet/api/streamjsonrpc.protocol.jsonrpcerrorcode?view=streamjsonrpc-2.9#fields
            await WriteJsonRpcErrorAsync(context, "Session not found", StatusCodes.Status404NotFound, -32001);
            return null;
        }

        if (!session.HasSameUserId(context.User))
        {
            await WriteJsonRpcErrorAsync(context,
                "Forbidden: The currently authenticated user does not match the user who initiated the session.",
                StatusCodes.Status403Forbidden);
            return null;
        }

        context.Response.Headers[McpHttpHeaders.SessionId] = session.Id;
        context.Features.Set(session.Server);
        return session;
    }

    private async ValueTask<StreamableHttpSession?> GetOrCreateSessionAsync(HttpContext context)
    {
        var sessionId = context.Request.Headers[McpHttpHeaders.SessionId].ToString();

        if (string.IsNullOrEmpty(sessionId))
        {
            return await StartNewSessionAsync(context);
        }
        else if (HttpServerTransportOptions.Stateless)
        {
            // In stateless mode, we should not be getting existing sessions via sessionId
            // This path should not be reached in stateless mode
            await WriteJsonRpcErrorAsync(context, "Bad Request: The Mcp-Session-Id header is not supported in stateless mode", StatusCodes.Status400BadRequest);
            return null;
        }
        else
        {
            return await GetSessionAsync(context, sessionId);
        }
    }

    private async ValueTask<StreamableHttpSession> StartNewSessionAsync(HttpContext context)
    {
        string sessionId;
        StreamableHttpServerTransport transport;

        if (!HttpServerTransportOptions.Stateless)
        {
            sessionId = MakeNewSessionId();
            transport = new(loggerFactory)
            {
                SessionId = sessionId,
                FlowExecutionContextFromRequests = !HttpServerTransportOptions.PerSessionExecutionContext,
                EventStreamStore = HttpServerTransportOptions.EventStreamStore,
            };
            context.Response.Headers[McpHttpHeaders.SessionId] = sessionId;
        }
        else
        {
            // In stateless mode, each request is independent. Don't set any session ID on the transport.
            // If in the future we support resuming stateless requests, we should populate
            // the event stream store and retry interval here as well.
            sessionId = "";
            transport = new(loggerFactory)
            {
                Stateless = true,
            };
        }

        return await CreateSessionAsync(context, transport, sessionId);
    }

    private async ValueTask<StreamableHttpSession> CreateSessionAsync(
        HttpContext context,
        StreamableHttpServerTransport transport,
        string sessionId)
    {
        var mcpServerServices = applicationServices;
        var mcpServerOptions = mcpServerOptionsSnapshot.Value;
        if (HttpServerTransportOptions.Stateless || HttpServerTransportOptions.ConfigureSessionOptions is not null)
        {
            mcpServerOptions = mcpServerOptionsFactory.Create(Options.DefaultName);

            if (HttpServerTransportOptions.Stateless)
            {
                // The session does not outlive the request in stateless mode.
                mcpServerServices = context.RequestServices;
                mcpServerOptions.ScopeRequests = false;
            }

            if (HttpServerTransportOptions.ConfigureSessionOptions is { } configureSessionOptions)
            {
                await configureSessionOptions(context, mcpServerOptions, context.RequestAborted);
            }
        }

        var server = McpServer.Create(transport, mcpServerOptions, loggerFactory, mcpServerServices);
        context.Features.Set(server);

        var userIdClaim = GetUserIdClaim(context.User);
        var session = new StreamableHttpSession(sessionId, transport, server, userIdClaim, sessionManager);

        var runSessionAsync = HttpServerTransportOptions.RunSessionHandler ?? RunSessionAsync;
        session.ServerRunTask = runSessionAsync(context, server, session.SessionClosed);

        return session;
    }

    private async ValueTask<ISseEventStreamReader?> GetEventStreamReaderAsync(HttpContext context, string lastEventId)
    {
        if (HttpServerTransportOptions.EventStreamStore is not { } eventStreamStore)
        {
            await WriteJsonRpcErrorAsync(context,
                "Bad Request: This server does not support resuming streams.",
                StatusCodes.Status400BadRequest);
            return null;
        }

        var eventStreamReader = await eventStreamStore.GetStreamReaderAsync(lastEventId, context.RequestAborted);
        if (eventStreamReader is null)
        {
            await WriteJsonRpcErrorAsync(context,
                "Bad Request: The specified Last-Event-ID is either invalid or expired.",
                StatusCodes.Status400BadRequest);
            return null;
        }

        return eventStreamReader;
    }

    private static Task WriteJsonRpcErrorAsync(HttpContext context, string errorMessage, int statusCode, int errorCode = -32000)
    {
        var jsonRpcError = new JsonRpcError
        {
            Error = new()
            {
                Code = errorCode,
                Message = errorMessage,
            },
        };
        return Results.Json(jsonRpcError, s_errorTypeInfo, statusCode: statusCode).ExecuteAsync(context);
    }

    internal static void InitializeSseResponse(HttpContext context)
    {
        context.Response.Headers.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache,no-store";

        // Make sure we disable all response buffering for SSE.
        context.Response.Headers.ContentEncoding = "identity";
        context.Features.GetRequiredFeature<IHttpResponseBodyFeature>().DisableBuffering();
    }

    internal static string MakeNewSessionId()
    {
        Span<byte> buffer = stackalloc byte[16];
        RandomNumberGenerator.Fill(buffer);
        return WebEncoders.Base64UrlEncode(buffer);
    }

    internal static async Task<JsonRpcMessage?> ReadJsonRpcMessageAsync(HttpContext context)
    {
        // Implementation for reading a JSON-RPC message from the request body
        var message = await context.Request.ReadFromJsonAsync(s_messageTypeInfo, context.RequestAborted);

        if (context.User?.Identity?.IsAuthenticated == true && message is not null)
        {
            message.Context = new()
            {
                User = context.User,
            };
        }

        return message;
    }

    internal static Task RunSessionAsync(HttpContext httpContext, McpServer session, CancellationToken requestAborted)
        => session.RunAsync(requestAborted);

    // SignalR only checks for ClaimTypes.NameIdentifier in HttpConnectionDispatcher, but AspNetCore.Antiforgery checks that plus the sub and UPN claims.
    // However, we short-circuit unlike antiforgery since we expect to call this to verify MCP messages a lot more frequently than
    // verifying antiforgery tokens from <form> posts.
    internal static UserIdClaim? GetUserIdClaim(ClaimsPrincipal user)
    {
        if (user?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        var claim = user.FindFirst(ClaimTypes.NameIdentifier) ?? user.FindFirst("sub") ?? user.FindFirst(ClaimTypes.Upn);

        if (claim is { } idClaim)
        {
            return new(idClaim.Type, idClaim.Value, idClaim.Issuer);
        }

        return null;
    }

    internal static JsonTypeInfo<T> GetRequiredJsonTypeInfo<T>() => (JsonTypeInfo<T>)McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(T));

    /// <summary>
    /// Validates that MCP HTTP headers match the corresponding values in the JSON-RPC request body.
    /// </summary>
    /// <param name="headers">The HTTP request headers.</param>
    /// <param name="request">The parsed JSON-RPC request.</param>
    /// <returns>An error message if validation fails; <see langword="null"/> if validation succeeds.</returns>
    internal static string? ValidateMcpHeaders(IHeaderDictionary headers, JsonRpcRequest request)
    {
        // Validate Mcp-Method header (required for all requests)
        if (!headers.TryGetValue(McpHttpHeaders.Method, out var methodHeader) || string.IsNullOrEmpty(methodHeader))
        {
            return $"Header mismatch: Required header '{McpHttpHeaders.Method}' is missing.";
        }

        // Method values are case-sensitive per the SEP
        var methodHeaderValue = methodHeader.ToString().Trim();
        if (!string.Equals(methodHeaderValue, request.Method, StringComparison.Ordinal))
        {
            return $"Header mismatch: {McpHttpHeaders.Method} header value '{methodHeaderValue}' does not match body value '{request.Method}'.";
        }

        // Validate method-specific headers
        switch (request.Method)
        {
            case RequestMethods.ToolsCall:
                var toolNameError = ValidateParamHeader(headers, McpHttpHeaders.ToolName, request.Params, "name");
                if (toolNameError is not null)
                {
                    return toolNameError;
                }
                // Validate custom parameter headers (Mcp-Param-*) for tools/call
                return ValidateCustomParamHeaders(headers, request.Params);

            case RequestMethods.ResourcesRead:
                return ValidateParamHeader(headers, McpHttpHeaders.ResourceUri, request.Params, "uri");

            case RequestMethods.PromptsGet:
                return ValidateParamHeader(headers, McpHttpHeaders.PromptName, request.Params, "name");
        }

        return null;
    }

    /// <summary>
    /// Validates that a method-specific header matches the corresponding parameter value in the request body.
    /// </summary>
    private static string? ValidateParamHeader(IHeaderDictionary headers, string headerName, System.Text.Json.Nodes.JsonNode? requestParams, string paramName)
    {
        // Get the expected value from the request body
        string? expectedValue = null;
        if (requestParams?[paramName]?.GetValue<string>() is string value)
        {
            expectedValue = value;
        }

        // Get the header value (HTTP parsers trim leading/trailing whitespace)
        if (!headers.TryGetValue(headerName, out var headerValue) || string.IsNullOrEmpty(headerValue))
        {
            if (expectedValue is not null)
            {
                return $"Header mismatch: Required header '{headerName}' is missing.";
            }
            // Both header and body value are missing/null - this is valid
            return null;
        }

        var headerValueStr = headerValue.ToString().Trim();

        if (expectedValue is null)
        {
            return $"Header mismatch: {headerName} header is present but no corresponding value exists in the request body.";
        }

        // Compare values (case-sensitive)
        if (!string.Equals(headerValueStr, expectedValue, StringComparison.Ordinal))
        {
            return $"Header mismatch: {headerName} header value '{headerValueStr}' does not match body value '{expectedValue}'.";
        }

        return null;
    }

    /// <summary>
    /// Validates custom parameter headers (Mcp-Param-*) against the request body arguments.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This implements lenient validation per the HTTP Standardization SEP:
    /// </para>
    /// <list type="bullet">
    /// <item><description>If a header is present, it MUST match the corresponding argument value in the body</description></item>
    /// <item><description>If a header is missing but the argument exists in the body, the request is accepted (lenient mode)</description></item>
    /// </list>
    /// <para>
    /// Base64-encoded values (with <c>=?base64?{value}?=</c> wrapper) are decoded before comparison.
    /// </para>
    /// </remarks>
    private static string? ValidateCustomParamHeaders(IHeaderDictionary headers, System.Text.Json.Nodes.JsonNode? requestParams)
    {
        // Get the arguments object from the request body
        var arguments = requestParams?["arguments"];
        if (arguments is null || arguments.GetValueKind() != System.Text.Json.JsonValueKind.Object)
        {
            // No arguments in body, so no validation needed for Mcp-Param-* headers
            // If headers are present with no body arguments, that's an error
            foreach (var header in headers)
            {
                if (header.Key.StartsWith(McpHttpHeaders.ParamPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    return $"Header mismatch: {header.Key} header is present but no arguments exist in the request body.";
                }
            }
            return null;
        }

        // Validate each Mcp-Param-* header against the corresponding argument
        foreach (var header in headers)
        {
            if (!header.Key.StartsWith(McpHttpHeaders.ParamPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Extract the parameter name from the header (Mcp-Param-{Name} -> {Name})
            // Note: We need to find the matching argument in a case-sensitive way since JSON keys are case-sensitive
            var headerParamName = header.Key.Substring(McpHttpHeaders.ParamPrefix.Length);
            var headerValue = header.Value.ToString().Trim();

            // Decode Base64 if needed
            var decodedHeaderValue = Client.McpHeaderEncoder.DecodeValue(headerValue);
            if (decodedHeaderValue is null && headerValue.StartsWith("=?base64?", StringComparison.OrdinalIgnoreCase))
            {
                return $"Header mismatch: {header.Key} contains invalid Base64 encoding.";
            }
            decodedHeaderValue ??= headerValue;

            // Find the matching argument in the body
            // The x-mcp-header name may differ from the JSON property name, so we need to check all arguments
            // and compare the header value against each one's string representation
            string? matchingArgName = null;
            string? matchingArgValue = null;

            foreach (var prop in arguments.AsObject())
            {
                // Check if this property name matches the header param name (case-insensitive for header matching)
                if (string.Equals(prop.Key, headerParamName, StringComparison.OrdinalIgnoreCase))
                {
                    matchingArgName = prop.Key;
                    matchingArgValue = ConvertJsonValueToString(prop.Value);
                    break;
                }
            }

            if (matchingArgName is null)
            {
                return $"Header mismatch: {header.Key} header is present but no matching argument exists in the request body.";
            }

            if (matchingArgValue is null)
            {
                return $"Header mismatch: {header.Key} header is present but the argument '{matchingArgName}' is null.";
            }

            // Compare values (case-sensitive for the actual value)
            if (!string.Equals(decodedHeaderValue, matchingArgValue, StringComparison.Ordinal))
            {
                return $"Header mismatch: {header.Key} header value '{decodedHeaderValue}' does not match body argument '{matchingArgName}' value '{matchingArgValue}'.";
            }
        }

        return null;
    }

    /// <summary>
    /// Converts a JSON value to its string representation for header comparison.
    /// </summary>
    private static string? ConvertJsonValueToString(System.Text.Json.Nodes.JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        return node.GetValueKind() switch
        {
            System.Text.Json.JsonValueKind.String => node.GetValue<string>(),
            System.Text.Json.JsonValueKind.Number => node.ToJsonString(), // Use raw JSON representation for numbers
            System.Text.Json.JsonValueKind.True => "true",
            System.Text.Json.JsonValueKind.False => "false",
            System.Text.Json.JsonValueKind.Null => null,
            _ => null // Arrays and objects are not supported for headers
        };
    }

    /// <summary>
    /// Determines if HTTP header validation is required based on the negotiated protocol version.
    /// </summary>
    /// <param name="requestProtocolVersion">The negotiated protocol version, or null if not yet negotiated.</param>
    /// <returns><see langword="true"/> if header validation is required; otherwise, <see langword="false"/>.</returns>
    /// <remarks>
    /// Header validation is only required for protocol versions >= <see cref="McpHttpHeaders.MinVersionForHeaderValidation"/>.
    /// This allows older clients to continue working without sending the new headers.
    /// </remarks>
    internal static bool IsHeaderValidationRequired(string? requestProtocolVersion)
    {
        if (string.IsNullOrEmpty(requestProtocolVersion))
        {
            return false;
        }

        // Protocol versions are date-based strings (e.g., "2025-06-18").
        // String comparison works correctly for ISO date format.
        return string.CompareOrdinal(requestProtocolVersion, McpHttpHeaders.MinVersionForHeaderValidation) >= 0;
    }

    private static bool MatchesApplicationJsonMediaType(MediaTypeHeaderValue acceptHeaderValue)
        => acceptHeaderValue.MatchesMediaType("application/json");

    private static bool MatchesTextEventStreamMediaType(MediaTypeHeaderValue acceptHeaderValue)
        => acceptHeaderValue.MatchesMediaType("text/event-stream");
}
