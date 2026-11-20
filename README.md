# McpHub

A single stdio MCP server for Claude Desktop that fans out to several remote MCP servers over **Streamable HTTP**.

Claude Desktop only needs one entry; McpHub connects to each upstream server, merges their tools, and exposes them as `<prefix>_<tool>`.

## Features

- Aggregates any number of remote MCP servers behind one stdio server
- Tools are namespaced by prefix (e.g. `pve_list_vms`, `ssh_exec`)
- Merges upstream `instructions` into the hub's `initialize` response
- Handles JSON and SSE (`text/event-stream`) responses, batches, and paginated `tools/list`
- Forwards cancellation, progress and log notifications
- Reconnects automatically when an upstream session expires (HTTP 404)
- If an upstream is down at startup, its tools are left out and the client is told to reload (`tools/list_changed`) once it comes back
- Native AOT build: one small self-contained executable, no .NET runtime required

## Usage

```
McpHub prefix=url [prefix=url ...]
```

- `prefix`: letters, digits and `-` only; must be unique
- `url`: the upstream's Streamable HTTP MCP endpoint

Examples:

Windows:

```
McpHub.exe pve=http://192.168.1.37:3001/mcp ssh=http://192.168.1.37:3002/mcp
```

macOS / Linux:

```
./McpHub pve=http://192.168.1.37:3001/mcp ssh=http://192.168.1.37:3002/mcp
```

stdout carries JSON-RPC only; all diagnostics go to stderr (visible in Claude's MCP log).

## Claude Desktop configuration

Add to `claude_desktop_config.json`:

| OS      | Config file location                                              |
|---------|-------------------------------------------------------------------|
| Windows | `%APPDATA%\Claude\claude_desktop_config.json`                     |
| macOS   | `~/Library/Application Support/Claude/claude_desktop_config.json` |
| Linux   | `~/.config/Claude/claude_desktop_config.json`                     |

Windows:

```json
{
  "mcpServers": {
    "hub": {
      "command": "C:\\Tools\\McpHub\\McpHub.exe",
      "args": [
        "pve=http://192.168.1.37:3001/mcp",
        "ssh=http://192.168.1.37:3002/mcp"
      ]
    }
  }
}
```

macOS / Linux (use an absolute path; `~` is not expanded):

```json
{
  "mcpServers": {
    "hub": {
      "command": "/usr/local/bin/McpHub",
      "args": [
        "pve=http://192.168.1.37:3001/mcp",
        "ssh=http://192.168.1.37:3002/mcp"
      ]
    }
  }
}
```

### Claude Code

```
claude mcp add hub -- /usr/local/bin/McpHub pve=http://192.168.1.37:3001/mcp ssh=http://192.168.1.37:3002/mcp
```

## Building

Requires the .NET 10 SDK plus the native toolchain for AOT:

- Windows: Visual Studio C++ build tools
- macOS: Xcode command line tools (`xcode-select --install`)
- Linux: `clang` and `zlib1g-dev` (Debian/Ubuntu: `sudo apt install clang zlib1g-dev`; Fedora: `sudo dnf install clang zlib-devel`)

Native AOT does not cross-compile between operating systems, so build on the OS you are targeting.

```
# Windows
dotnet publish -c Release -r win-x64

# macOS (Apple Silicon / Intel)
dotnet publish -c Release -r osx-arm64
dotnet publish -c Release -r osx-x64

# Linux (x64 / ARM64)
dotnet publish -c Release -r linux-x64
dotnet publish -c Release -r linux-arm64
```

Output: `bin/Release/net10.0/<rid>/publish/McpHub` (`McpHub.exe` on Windows).

Install on macOS / Linux:

```
sudo install -m 755 bin/Release/net10.0/linux-x64/publish/McpHub /usr/local/bin/McpHub
```

On macOS, a binary downloaded from elsewhere may be quarantined; clear it with `xattr -d com.apple.quarantine /usr/local/bin/McpHub`.

Or use the included publish profile (Windows, `win-x64`):

```
dotnet publish -p:PublishProfile=FolderProfile
```

Output: `bin/Release/net10.0/publish/win-x64/McpHub.exe`

For a quick debug run without AOT:

```
dotnet run -- pve=http://localhost:3001/mcp
```

## Notes

- System proxies are bypassed (intended for LAN servers).
- Server-to-client requests other than `ping` (sampling, elicitation, roots) are answered with "method not found".
- Tool calls have no hub-side timeout; they end when the upstream replies or the client cancels.
