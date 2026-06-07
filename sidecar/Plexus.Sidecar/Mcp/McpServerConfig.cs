namespace Plexus.Sidecar.Mcp;

// Mirror of McpServerConfig in contract/blocks.ts. Registry of user-configured
// MCP servers (the ONLY servers the host ever connects to — never a URL from a
// tool result or model output). HTTP credentials live in the keychain, keyed by
// server id ("plexus-mcp-{id}-key"); never inline here, never logged.
public sealed class McpServerConfig
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public McpTransport Transport { get; set; } = new();
    public bool Enabled { get; set; } = true;
    public string? ToolPolicy { get; set; } // "auto" | "confirm-destructive" | "confirm-all"
}

public sealed class McpTransport
{
    public string Kind { get; set; } = "stdio"; // "stdio" | "http"

    // stdio
    public string? Command { get; set; }
    public List<string>? Args { get; set; }
    public Dictionary<string, string>? Env { get; set; }

    // http
    public string? Url { get; set; }
}
