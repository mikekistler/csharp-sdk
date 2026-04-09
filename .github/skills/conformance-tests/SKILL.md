---
name: conformance-tests
description: Run MCP conformance tests against the C# SDK server and client implementations. Supports running the default test suite, specific scenarios, and testing with alternate conformance package versions from forks or branches. Use when asked to run conformance tests, check conformance, test against the spec, or validate MCP protocol compliance.
compatibility: Requires Node.js (for the conformance test runner) and dotnet CLI. The conformance package is installed automatically via npm.
---

# Conformance Tests

Run the [MCP conformance test suite](https://github.com/modelcontextprotocol/conformance) against the C# SDK's server and client implementations. The conformance tests validate that the SDK correctly implements the MCP specification.

## Architecture

The conformance test infrastructure has these components:

- **Conformance test runner** — A Node.js CLI tool (`@modelcontextprotocol/conformance`) that orchestrates test scenarios against MCP servers and clients. Installed via npm from the repo's `package.json`.
- **ConformanceServer** — An ASP.NET Core server (`tests/ModelContextProtocol.ConformanceServer/`) that implements all MCP capabilities needed by the conformance scenarios (tools, prompts, resources, subscriptions, logging, completions, SSE polling).
- **ConformanceClient** — A .NET client (`tests/ModelContextProtocol.ConformanceClient/`) that connects to a test server and exercises scenarios based on the `MCP_CONFORMANCE_SCENARIO` environment variable.
- **Test classes** — xUnit test classes in `tests/ModelContextProtocol.AspNetCore.Tests/` that start the server/client and invoke the conformance runner:
  - `ServerConformanceTests` — Tests the SDK server against the conformance runner
  - `ClientConformanceTests` — Tests the SDK client against the conformance runner
  - `StreamableHttpServerConformanceTests` — Protocol-level HTTP transport tests
  - `StreamableHttpClientConformanceTests` — Protocol-level HTTP client tests
- **NodeHelpers** — Utility class (`tests/Common/Utils/NodeHelpers.cs`) that manages npm dependency installation and supports the `MCP_CONFORMANCE_PACKAGE` environment variable override.

## Running Conformance Tests

### Run All Server Conformance Tests

```bash
dotnet test tests/ModelContextProtocol.AspNetCore.Tests \
  --filter "FullyQualifiedName~ServerConformanceTests" \
  --framework net10.0
```

### Run All Client Conformance Tests

```bash
dotnet test tests/ModelContextProtocol.AspNetCore.Tests \
  --filter "FullyQualifiedName~ClientConformanceTests" \
  --framework net10.0
```

### Run a Specific Scenario

Each scenario has its own test method. Use the test method name to run a specific scenario:

```bash
dotnet test tests/ModelContextProtocol.AspNetCore.Tests \
  --filter "FullyQualifiedName~ServerConformanceTests.RunConformanceTest_HttpHeaderValidation" \
  --framework net10.0
```

### Run with Detailed Output

Add the console logger to see conformance check details (pass/fail per check):

```bash
dotnet test tests/ModelContextProtocol.AspNetCore.Tests \
  --filter "FullyQualifiedName~ServerConformanceTests" \
  --framework net10.0 \
  -l "console;verbosity=detailed"
```

### Run Streamable HTTP Transport Tests

These are xUnit tests that validate HTTP-level protocol behavior directly (not via the node conformance runner):

```bash
dotnet test tests/ModelContextProtocol.AspNetCore.Tests \
  --filter "FullyQualifiedName~StreamableHttpServerConformanceTests" \
  --framework net10.0
```

## Using an Alternate Conformance Package

You can replace the `@modelcontextprotocol/conformance` npm package with a fork, branch, or local path. This is useful for:

- Testing new conformance scenarios before they are released
- Validating SDK changes against an in-development conformance suite
- Debugging conformance test failures with a modified test runner

### How It Works

The `NodeHelpers.EnsureNpmDependenciesInstalled()` method (in `tests/Common/Utils/NodeHelpers.cs`) runs `npm ci` to install dependencies from `package-lock.json` if `node_modules/` doesn't exist. To use an alternate conformance package, run a manual `npm install` **after** `npm ci` to replace just the conformance package while leaving all other dependencies intact.

### Steps

1. **Ensure base dependencies are installed** (skip if `node_modules/` already exists):

   ```bash
   npm ci
   ```

2. **Install the alternate conformance package** using `npm install --no-save`. The `--no-save` flag avoids modifying `package.json` or `package-lock.json`:

   ```bash
   npm install --no-save @modelcontextprotocol/conformance@<specifier>
   ```

3. **Run the conformance tests** as usual:

   ```bash
   dotnet test tests/ModelContextProtocol.AspNetCore.Tests \
     --filter "FullyQualifiedName~ServerConformanceTests" \
     --framework net10.0
   ```

### Examples

**Use a GitHub fork/branch:**

```bash
npm install --no-save "@modelcontextprotocol/conformance@github:mikekistler/conformance#mdk/sep-2243-conformance-tests"
```

**Use a specific commit:**

```bash
npm install --no-save "@modelcontextprotocol/conformance@github:modelcontextprotocol/conformance#abc1234"
```

**Use a local path (for local conformance development):**

```bash
npm install --no-save "@modelcontextprotocol/conformance@file:../conformance"
```

**Use a specific npm version:**

```bash
npm install --no-save "@modelcontextprotocol/conformance@0.1.15"
```

### Restoring the Default Version

To go back to the version pinned in `package-lock.json`, delete `node_modules/` and reinstall:

```bash
rm -rf node_modules
npm ci
```

### Important Notes

- Since `NodeHelpers` skips `npm ci` when `node_modules/` already exists, the manually installed override persists across test runs until you delete `node_modules/`.
- The specifier after `@` can be any valid [npm package specifier](https://docs.npmjs.com/cli/v10/using-npm/package-spec).

## Available Conformance Scenarios

To list all available scenarios from the installed conformance package:

```bash
node_modules/.bin/conformance list --server
node_modules/.bin/conformance list --client
```

## Key Files

| File | Description |
|------|-------------|
| `tests/Common/Utils/NodeHelpers.cs` | npm install logic and `MCP_CONFORMANCE_PACKAGE` override |
| `tests/ModelContextProtocol.AspNetCore.Tests/ServerConformanceTests.cs` | Server conformance test class and fixture |
| `tests/ModelContextProtocol.AspNetCore.Tests/ClientConformanceTests.cs` | Client conformance test class |
| `tests/ModelContextProtocol.AspNetCore.Tests/StreamableHttpServerConformanceTests.cs` | HTTP transport protocol tests |
| `tests/ModelContextProtocol.AspNetCore.Tests/StreamableHttpClientConformanceTests.cs` | HTTP client protocol tests |
| `tests/ModelContextProtocol.ConformanceServer/Program.cs` | Server under test |
| `tests/ModelContextProtocol.ConformanceClient/Program.cs` | Client under test |
| `package.json` | Pins the default conformance package version |
