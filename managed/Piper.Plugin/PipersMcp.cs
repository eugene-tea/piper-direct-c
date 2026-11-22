using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LuckyEngine.PluginSdk;

namespace LuckyRobots.Piper.Plugin;

/// <summary>Which panel actions an agent may take on its own. Stops and navigation are free; every other
/// action, and anything that can move an arm or drop a gripper, needs the operator's go
/// (<c>confirm: true</c>). Editor-originated streams (<c>engine.*</c>) are never accepted from an agent.</summary>
internal static class PipersMcpPolicy
{
    public static string LocalId(string wireId)
    {
        string id = wireId;
        if (id.StartsWith("view.", StringComparison.Ordinal) && id.IndexOf('.', 5) is > 5 and var end) id = id[(end + 1)..];
        if (id.StartsWith("robot.", StringComparison.Ordinal) && id.IndexOf('.', 6) is > 6 and var cut) id = id[(cut + 1)..];
        return id;
    }

    public static bool IsStop(string localId) => localId is "stop" or "simulation.stop" or "motion.hold" or "group.follow.cancel" or "recovery.guide.cancel" ||
        localId.StartsWith(RobotProfileRouting.RobotStopPrefix, StringComparison.Ordinal);

    public static bool Rejected(string localId) => localId.StartsWith("engine.", StringComparison.Ordinal);

    /// <summary>True when the action cannot move hardware or change what the arms will do.</summary>
    public static bool Free(string localId) => IsStop(localId) ||
        localId.StartsWith("workspace.", StringComparison.Ordinal) && !WorkspaceCommandPolicy.RequiresControls(localId) ||
        localId.StartsWith("profiles.select.", StringComparison.Ordinal) ||
        localId is "backend.start" or "backend.check" or "backend.restart" or "backend.restart.cancel" or "disable.confirm.cancel";
}

/// <summary>A loopback MCP endpoint (streamable HTTP, JSON responses) over the Pipers panel. Reads come
/// from <see cref="UiMirror"/>; clicks go through <see cref="PipersPlugin.HandleEventAsync"/>, the same
/// gate as a mouse click, so every plugin guard still applies. On exactly when the editor serves its own MCP.</summary>
internal sealed class PipersMcp : IAsyncDisposable
{
    public const int DefaultPort = 19340;
    private const int MaxOutline = 400;
    private readonly PipersPlugin plugin;
    private readonly HttpListener listener = new();
    private readonly CancellationTokenSource stop = new();
    private readonly string token;
    private Task? loop;

    public int Port { get; }

    private PipersMcp(PipersPlugin plugin, int port, string token)
    { this.plugin = plugin; Port = port; this.token = token; listener.Prefixes.Add($"http://127.0.0.1:{port}/"); }

    /// <summary>The tool surface without a listener, for tests.</summary>
    internal static PipersMcp Detached(PipersPlugin plugin) => new(plugin, 0, "detached");

    /// <summary>True when the editor serves its own MCP (<c>host.info.agentMcpHttpPort</c> non-zero). Pipers follows
    /// the editor: one switch, the editor's, decides whether agents are offered tools at all.</summary>
    public static bool EditorOffersAgents(JsonElement hostInfo) =>
        hostInfo.ValueKind == JsonValueKind.Object && hostInfo.TryGetProperty("agentMcpHttpPort", out var port) &&
        port.ValueKind == JsonValueKind.Number && port.TryGetInt32(out int value) && value > 0;

    public static PipersMcp Start(PipersPlugin plugin, string settingsDirectory, int port = DefaultPort)
    {
        var server = new PipersMcp(plugin, port, LoadToken(Path.Combine(settingsDirectory, "mcp-token")));
        server.listener.Start();
        server.loop = server.Serve();
        Trace.TraceInformation("Agent access (MCP) on http://127.0.0.1:{0}/mcp; bearer token in {1}", port, Path.Combine(settingsDirectory, "mcp-token"));
        return server;
    }

    private static string LoadToken(string file)
    {
        if (File.Exists(file) && File.ReadAllText(file).Trim() is { Length: >= 32 } saved) return saved;
        string fresh = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        File.WriteAllText(file, fresh);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return fresh;
    }

    private async Task Serve()
    {
        while (!stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync().WaitAsync(stop.Token); }
            catch (Exception) when (stop.IsCancellationRequested || !listener.IsListening) { return; }
            _ = Task.Run(() => Respond(context));
        }
    }

    private async Task Respond(HttpListenerContext context)
    {
        var response = context.Response;
        try
        {
            var request = context.Request;
            if (request.Url?.AbsolutePath != "/mcp") { response.StatusCode = 404; return; }
            if (request.Headers["Origin"] is { Length: > 0 } origin && !LoopbackOrigin(origin)) { response.StatusCode = 403; return; }
            if (request.Headers["Authorization"] != "Bearer " + token) { response.StatusCode = 401; return; }
            if (request.HttpMethod != "POST") { response.StatusCode = 405; return; }
            using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
            var message = JsonNode.Parse(await reader.ReadToEndAsync()) as JsonObject;
            if (message?["id"] is not { } id) { response.StatusCode = 202; return; }
            var reply = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone() };
            try { reply["result"] = await Dispatch(message["method"]?.GetValue<string>() ?? "", message["params"] as JsonObject ?? new()); }
            catch (McpError error) { reply["error"] = new JsonObject { ["code"] = error.Code, ["message"] = error.Message }; }
            byte[] body = Encoding.UTF8.GetBytes(reply.ToJsonString());
            response.ContentType = "application/json";
            response.ContentLength64 = body.Length;
            await response.OutputStream.WriteAsync(body, stop.Token);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException) { response.StatusCode = 400; }
        catch (Exception error) { Trace.TraceWarning("MCP request: {0}", error.Message); response.StatusCode = 500; }
        finally { try { response.Close(); } catch (Exception) { } }
    }

    private static bool LoopbackOrigin(string origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri) && (uri.IsLoopback || uri.Host == "localhost");

    internal async Task<JsonNode> Dispatch(string method, JsonObject parameters) => method switch
    {
        "initialize" => new JsonObject
        {
            ["protocolVersion"] = parameters["protocolVersion"]?.GetValue<string>() ?? "2025-03-26",
            ["serverInfo"] = new JsonObject { ["name"] = "pipers", ["version"] = "0.1.0" },
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
            ["instructions"] = "Pipers robot-arm panel. Start with pipers_status. Find widget ids with pipers_ui, press them with pipers_click, " +
                "then pipers_wait for the result. Stops and navigation are free; anything else needs confirm:true, which you may only send " +
                "after the human operator said go for that exact step. Real arms can move.",
        },
        "ping" => new JsonObject(),
        "tools/list" => new JsonObject { ["tools"] = Tools() },
        "tools/call" => await Call(parameters["name"]?.GetValue<string>() ?? "", parameters["arguments"] as JsonObject ?? new()),
        _ => throw new McpError(-32601, "Unknown method " + method),
    };

    private async Task<JsonNode> Call(string name, JsonObject args)
    {
        try
        {
            string text = name switch
            {
                "pipers_status" => plugin.AgentStatus().ToJsonString(),
                "pipers_ui" => Outline(args["under"]?.GetValue<string>(), args["match"]?.GetValue<string>(), args["all"]?.GetValue<bool>() == true),
                "pipers_click" => await Click(Required(args, "id"), args["value"], args["confirm"]?.GetValue<bool>() == true),
                "pipers_wait" => await Wait(args),
                "pipers_last_failure" => plugin.AgentLastFailure(args["full"]?.GetValue<bool>() == true),
                _ => throw new McpError(-32602, "Unknown tool " + name),
            };
            return Content(text, false);
        }
        catch (McpError) { throw; }
        catch (Exception error) { return Content(error.Message, true); }
    }

    private static JsonObject Content(string text, bool isError) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        ["isError"] = isError,
    };

    private static string Required(JsonObject args, string key) =>
        args[key]?.GetValue<string>() is { Length: > 0 } value ? value : throw new InvalidOperationException($"\"{key}\" is required.");

    internal string Outline(string? under, string? match, bool all)
    {
        var widgets = plugin.workspace.Mirror.Snapshot();
        IEnumerable<UiMirrorWidget> chosen = widgets;
        if (under is { Length: > 0 })
        {
            int start = widgets.ToList().FindIndex(w => w.Id == under || w.WireId == under);
            if (start < 0) throw new InvalidOperationException($"No widget \"{under}\" in the panel.");
            int depth = widgets[start].Depth;
            chosen = widgets.Skip(start).TakeWhile((w, i) => i == 0 || w.Depth > depth);
        }
        if (!all) chosen = chosen.Where(w => w.Visible);
        if (match is { Length: > 0 })
            chosen = chosen.Where(w => w.Id.Contains(match, StringComparison.OrdinalIgnoreCase) || w.Text.Contains(match, StringComparison.OrdinalIgnoreCase));
        var lines = chosen.Where(w => w.WireId.Length > 0).Take(MaxOutline + 1).Select(w =>
            new string(' ', Math.Min(w.Depth, 12)) + w.Id + " [" + w.Type + "]" +
            (w.Text.Length > 0 ? " \"" + w.Text.Replace("\n", " / ") + "\"" : "") +
            (w.Appearance.Length > 0 ? " " + w.Appearance : "") + (w.Visible ? "" : " hidden") + (w.Enabled ? "" : " disabled")).ToList();
        if (lines.Count > MaxOutline) { lines.RemoveAt(MaxOutline); lines.Add("… more; narrow with under or match"); }
        return lines.Count == 0 ? "No matching widgets." : string.Join('\n', lines);
    }

    internal async Task<string> Click(string id, JsonNode? value, bool confirm)
    {
        var widgets = plugin.workspace.Mirror.Snapshot();
        var candidates = widgets.Where(w => w.WireId == id).ToList();
        if (candidates.Count == 0) candidates = widgets.Where(w => w.Id == id && w.WireId.Length > 0).ToList();
        if (candidates.Count > 1) candidates = candidates.Where(w => w.Visible).ToList();
        if (candidates.Count == 0) throw new InvalidOperationException($"No widget \"{id}\" in the panel. Use pipers_ui to find ids.");
        if (candidates.Count > 1) throw new InvalidOperationException($"\"{id}\" is ambiguous: " + string.Join(", ", candidates.Select(w => w.WireId)));
        var widget = candidates[0];
        string local = widget.Id;
        if (PipersMcpPolicy.Rejected(local)) throw new InvalidOperationException($"\"{local}\" is sent by the editor, not by a person.");
        if (!PipersMcpPolicy.Free(local) && !confirm)
            throw new InvalidOperationException($"\"{local}\" needs the operator's go. Ask the human, then call again with confirm:true.");
        if (widget.Type is "text" or "column" or "row" or "separator" or "image")
            throw new InvalidOperationException($"\"{local}\" is a {widget.Type}, not something to press.");
        if (!widget.Visible || !widget.Enabled)
            throw new InvalidOperationException($"\"{local}\" is {(widget.Visible ? "disabled" : "hidden")}; a person could not press it either.");
        long version = plugin.workspace.Mirror.Version;
        Trace.TraceInformation("Click received (agent{0}): {1}", confirm ? ", operator go" : "", local);
        var element = JsonSerializer.SerializeToElement(value ?? JsonValue.Create(true));
        await plugin.HandleEventAsync(new(PipersPlugin.PanelId, widget.WireId, "click", element), stop.Token);
        await plugin.workspace.Mirror.WaitForChange(version, TimeSpan.FromSeconds(1.5), stop.Token);
        return plugin.AgentStatus().ToJsonString();
    }

    internal async Task<string> Wait(JsonObject args)
    {
        string id = Required(args, "id");
        string? contains = args["contains"]?.GetValue<string>();
        string? absent = args["notContains"]?.GetValue<string>();
        string? appearance = args["appearance"]?.GetValue<string>();
        bool? visible = args["visible"]?.GetValue<bool>();
        int timeoutMs = Math.Clamp(args["timeoutMs"]?.GetValue<int>() ?? 30_000, 0, 600_000);
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            long version = plugin.workspace.Mirror.Version;
            var widget = plugin.workspace.Mirror.Snapshot().FirstOrDefault(w => (w.Id == id || w.WireId == id) && w.WireId.Length > 0);
            bool met = widget is not null &&
                (contains is null || widget.Text.Contains(contains, StringComparison.OrdinalIgnoreCase)) &&
                (absent is null || !widget.Text.Contains(absent, StringComparison.OrdinalIgnoreCase)) &&
                (appearance is null || widget.Appearance == appearance) &&
                (visible is null || widget.Visible == visible);
            var remaining = TimeSpan.FromMilliseconds(timeoutMs) - deadline.Elapsed;
            if (met || remaining <= TimeSpan.Zero)
            {
                var status = plugin.AgentStatus();
                status["waited"] = new JsonObject { ["met"] = met, ["ms"] = (long)deadline.Elapsed.TotalMilliseconds, ["widget"] = widget?.Text ?? "(not in panel)" };
                return status.ToJsonString();
            }
            await plugin.workspace.Mirror.WaitForChange(version, remaining, stop.Token);
        }
    }

    private static JsonArray Tools() =>
    [
        Tool("pipers_status", "Server, every robot's status, Follow state, the last run's failure and every red error on the panel. Start here.", new()),
        Tool("pipers_ui", "Outline of the visible Pipers panel: id [type] \"text\" tone. Use it to find a widget id.", new()
        {
            ["under"] = Prop("string", "Only this widget and its children, by id"),
            ["match"] = Prop("string", "Only widgets whose id or text contains this"),
            ["all"] = Prop("boolean", "Include hidden widgets"),
        }),
        Tool("pipers_click", "Press a panel widget by id, through the same gate as a mouse click. Stops and navigation are free; anything else needs confirm:true after the operator said go.", new()
        {
            ["id"] = Prop("string", "Widget id from pipers_ui"),
            ["value"] = new JsonObject { ["description"] = "Value for a checkbox, field or segment; true for a button" },
            ["confirm"] = Prop("boolean", "The human operator approved this exact step"),
        }, "id"),
        Tool("pipers_wait", "Wait until a widget's text or tone matches, then return the status. Replaces polling.", new()
        {
            ["id"] = Prop("string", "Widget id"),
            ["contains"] = Prop("string", "Text contains (case-insensitive)"),
            ["notContains"] = Prop("string", "Text no longer contains"),
            ["appearance"] = Prop("string", "Tone, e.g. danger, success, warning"),
            ["visible"] = Prop("boolean", "Visibility"),
            ["timeoutMs"] = Prop("integer", "Default 30000, at most 600000"),
        }, "id"),
        Tool("pipers_last_failure", "Why the last follow run stopped: the newest simulation-follow diagnostics, reduced.", new()
        {
            ["full"] = Prop("boolean", "Return the whole diagnostics file"),
        }),
    ];

    private static JsonObject Tool(string name, string description, JsonObject properties, params string[] required) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = new JsonArray(required.Select(r => (JsonNode)r).ToArray()) },
    };

    private static JsonObject Prop(string type, string description) => new() { ["type"] = type, ["description"] = description };

    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync();
        try { listener.Stop(); listener.Close(); } catch (Exception) { }
        if (loop is not null) try { await loop.WaitAsync(TimeSpan.FromSeconds(1)); } catch (Exception) { }
        stop.Dispose();
    }

    private sealed class McpError(int code, string message) : Exception(message) { public int Code { get; } = code; }
}
