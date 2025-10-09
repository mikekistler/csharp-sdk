# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

This is the official C# SDK for the Model Context Protocol (MCP), enabling .NET applications to implement and interact with MCP clients and servers. The SDK is distributed as three NuGet packages (all in preview):

- **ModelContextProtocol** - Main package with hosting and dependency injection extensions
- **ModelContextProtocol.Core** - Minimal package with low-level client/server APIs and minimal dependencies
- **ModelContextProtocol.AspNetCore** - HTTP-based MCP server support for ASP.NET Core

## Build and Test Commands

### Building
```bash
# Build the entire solution
dotnet build --configuration Release

# Or use Make
make build

# Clean build artifacts
make clean
```

### Testing
```bash
# Run all tests (excludes manual tests)
dotnet test --filter '(Execution!=Manual)' --no-build --configuration Release

# Run tests with Make
make test

# Run tests for a specific project
dotnet test tests/ModelContextProtocol.Tests --configuration Release

# Run a single test by name
dotnet test --filter "FullyQualifiedName~TestMethodName"
```

### Documentation
```bash
# Generate API documentation
make generate-docs

# Serve docs locally on port 8080
make serve-docs
```

### Package Management
```bash
# Restore NuGet packages and dotnet tools
make restore

# Create NuGet packages
dotnet pack --configuration Release
```

**Important**: Tests require Node.js and the MCP Everything Server:
```bash
npm install @modelcontextprotocol/server-everything
npm install @modelcontextprotocol/server-memory
```

## Architecture

### Core Abstractions

The SDK is built around several key abstractions:

1. **McpSession** (`src/ModelContextProtocol.Core/McpSession.cs`) - Base class for both clients and servers, providing core JSON-RPC communication:
   - Sending requests and receiving responses
   - Sending notifications
   - Registering notification handlers
   - Used by both `McpClient` and `McpServer`

2. **Transport Layer** - Separates protocol logic from transport mechanism:
   - **Client transports** (`src/ModelContextProtocol.Core/Client/`):
     - `StdioClientTransport` - Standard I/O (process-based servers)
     - `HttpClientTransport` - HTTP-based communication
     - `SseClientSessionTransport` - Server-Sent Events
     - `StreamableHttpClientSessionTransport` - Streamable HTTP
     - `StreamClientTransport` - Generic stream-based transport
   - **Server transports** (`src/ModelContextProtocol.Core/Server/`):
     - `StdioServerTransport` - Standard I/O
     - `StreamServerTransport` - Generic streams
     - `SseResponseStreamTransport` - Server-Sent Events
     - `StreamableHttpServerTransport` - Streamable HTTP

3. **Protocol Types** (`src/ModelContextProtocol.Core/Protocol/`) - JSON-RPC message types and MCP protocol definitions matching the MCP specification

### Client Architecture

Clients (`src/ModelContextProtocol.Core/Client/`) connect to MCP servers and invoke tools/prompts/resources:

- `McpClient` - Main client interface
- `McpClientImpl` - Implementation
- `McpClientFactory` - Factory for creating clients
- `McpClientTool`, `McpClientPrompt`, `McpClientResource` - Wrappers for server primitives
- **Integration with Microsoft.Extensions.AI**: `McpClientTool` inherits from `AIFunction` for seamless use with `IChatClient`

**Client pattern**:
```csharp
var transport = new StdioClientTransport(new StdioClientTransportOptions { ... });
var client = await McpClient.CreateAsync(transport);
var tools = await client.ListToolsAsync();
await client.CallToolAsync("toolName", arguments);
```

### Server Architecture

Servers (`src/ModelContextProtocol.Core/Server/`) expose tools, prompts, and resources:

- `McpServer` - Main server interface
- `McpServerImpl` - Implementation
- `McpServerTool`, `McpServerPrompt`, `McpServerResource` - Server-side primitives
- **Attribute-based registration**:
  - `[McpServerToolType]` - Mark class containing tool methods
  - `[McpServerTool]` - Mark method as a tool
  - `[McpServerPromptType]` / `[McpServerPrompt]` - For prompts
  - `[McpServerResourceType]` / `[McpServerResource]` - For resources

**Server patterns**:

1. **With dependency injection** (`ModelContextProtocol` package):
```csharp
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();
```

2. **Low-level** (`ModelContextProtocol.Core` package):
```csharp
var server = new McpServer(options);
await server.RunAsync(transport);
```

### Dependency Injection Integration

The `ModelContextProtocol` package (`src/ModelContextProtocol/`) provides hosting extensions:

- `IMcpServerBuilder` - Fluent API for configuring servers
- `McpServerBuilderExtensions` - Extension methods for registration
- `SingleSessionMcpServerHostedService` - IHostedService implementation
- Automatic discovery of tools/prompts/resources via attributes

### ASP.NET Core Integration

The `ModelContextProtocol.AspNetCore` package enables HTTP-based MCP servers:

- HTTP and SSE transport support
- `MapMcp()` extension to route MCP requests
- Per-session tool registration support
- OAuth authentication support
- **Long-running tool operations** via HTTP polling (see below)

## Project Structure

```
src/
├── Common/                           # Shared build configuration
├── ModelContextProtocol.Core/        # Core SDK (minimal dependencies)
│   ├── Client/                       # Client implementations and transports
│   ├── Server/                       # Server implementations and transports
│   ├── Protocol/                     # MCP protocol types and JSON-RPC messages
│   └── Authentication/               # OAuth authentication support
├── ModelContextProtocol/             # Main package with DI/hosting
└── ModelContextProtocol.AspNetCore/  # ASP.NET Core extensions

tests/
├── ModelContextProtocol.Tests/       # Core tests
├── ModelContextProtocol.AspNetCore.Tests/
├── ModelContextProtocol.TestServer/  # Test server for integration tests
├── ModelContextProtocol.TestOAuthServer/
└── ModelContextProtocol.TestSseServer/

samples/                              # Example implementations
docs/                                 # Documentation and DocFX config
```

## Development Notes

### Build Configuration

- **Language version**: C# preview features enabled
- **Target frameworks**: Multi-targeted for .NET Standard 2.0, .NET 8.0, .NET 9.0, and .NET 10.0
- **Central package management**: Package versions defined in `Directory.Packages.props`
- **Common properties**: `Directory.Build.props` sets common MSBuild properties
- **Artifacts**: Build outputs go to `artifacts/` directory
- **TreatWarningsAsErrors**: Enabled - all warnings must be fixed
- **Nullable**: Enabled - all reference types are non-nullable by default

### SDK Version

This project uses .NET 10.0 SDK (RC). See `global.json` for exact version requirements.

### Testing Conventions

- Tests use **xunit v3**
- Base class `LoggedTest` provides logging to test output
- `ClientServerTestBase` sets up in-process client-server communication using pipes
- Integration tests use `ClientIntegrationTestFixture` which spawns real server processes
- Test filter `(Execution!=Manual)` excludes manual tests from automated runs
- Tests require the MCP Everything Server (Node.js package) to be installed

### Key Patterns

1. **AIFunction Integration**: Server primitives (tools, prompts, resources) are created from methods using `AIFunctionFactory.Create()` from Microsoft.Extensions.AI

2. **JSON-RPC**: All MCP communication uses JSON-RPC 2.0. See `Protocol/` for message types

3. **Async patterns**: All I/O operations are async. Use `ValueTask` for frequently synchronous operations

4. **Transport abstraction**: New transports implement `IClientTransport` (client) or provide a stream pair (server)

5. **Error handling**: Use `McpException` for protocol-level errors with `McpErrorCode` enum

6. **Progress reporting**: Long-running operations support progress via `IProgress<ProgressNotificationValue>`

7. **Cancellation**: All async operations accept `CancellationToken`

### Long-Running Tool Operations (HTTP Only)

For HTTP-based servers, tools can initiate long-running operations that return immediately with a status URL:

**Setup**:
```csharp
builder.Services
    .AddLongRunningToolOperations(resultRetention: TimeSpan.FromMinutes(10))
    .AddMcpServer()
    .WithHttpTransport()
    .WithTools<MyTools>();

app.MapMcp();
app.MapLongRunningToolOperations(); // Maps GET /mcp/operations/{id}
```

**Tool implementation**:
```csharp
[McpServerTool]
public static CallToolResult LongOperation(
    LongRunningToolOperationStore store,
    IHttpContextAccessor httpContextAccessor,
    int input)
{
    return store.StartLongRunningOperation(
        httpContextAccessor.HttpContext!,
        async (ct) =>
        {
            await Task.Delay(10000, ct); // Long work
            return new CallToolResult { /* result */ };
        });
}
```

**How it works**:
1. Client calls tool via MCP → Server returns immediately with `statusUrl` in the `CallToolResult`
2. Client extracts `statusUrl` and polls it via HTTP GET
3. Status endpoint returns:
   - **202 Accepted** with `Location` header if still in progress
   - **200 OK** with the `CallToolResult` body when complete

See `samples/LongRunningToolsHttpSample/` for a complete example with curl commands.

### Common Gotchas

- Server methods marked with `[McpServerTool]` can have dependencies injected as parameters
- The `McpServer` itself can be injected into tool methods for making sampling requests back to the client
- Long-running operations only work with HTTP transports, not stdio
- Transport implementations must handle connection lifecycle properly (connect, send, receive, disconnect)
- Multi-targeting means being careful with APIs that differ across frameworks
- Protocol types in `Protocol/` are auto-generated or closely follow the MCP spec - modifications should match spec changes
