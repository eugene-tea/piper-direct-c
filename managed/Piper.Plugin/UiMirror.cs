using System.Text.Json;
using System.Text.Json.Nodes;
using LuckyEngine.PluginSdk;

namespace LuckyRobots.Piper.Plugin;

/// <summary>What the Pipers panel shows right now: the last published widget tree with every
/// later patch applied, keyed by the same wire ids the editor sees. Agent tools read this
/// instead of a screenshot. It only observes publications; it never drives the panel.</summary>
internal sealed class UiMirror
{
    private readonly object gate = new();
    private JsonObject? root;
    private Dictionary<string, JsonObject> byId = new(StringComparer.Ordinal);
    private TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public long Version { get; private set; }

    public void Replace(Widget tree)
    {
        var node = JsonSerializer.SerializeToNode(tree, PluginProtocol.Json) as JsonObject;
        lock (gate)
        {
            root = node;
            byId = new(StringComparer.Ordinal);
            if (node is not null) Index(node);
            Signal();
        }
    }

    public void Apply(IReadOnlyDictionary<string, JsonObject> patches)
    {
        lock (gate)
        {
            foreach (var (id, patch) in patches)
            {
                if (!byId.TryGetValue(id, out var widget)) continue;
                foreach (var (key, value) in patch) widget[key] = value?.DeepClone();
            }
            Signal();
        }
    }

    /// <summary>Completes on the next publication after <paramref name="version"/>, or when the wait ends.</summary>
    public async Task WaitForChange(long version, TimeSpan timeout, CancellationToken cancellation)
    {
        Task next;
        lock (gate) { if (Version != version) return; next = changed.Task; }
        try { await next.WaitAsync(timeout, cancellation); }
        catch (TimeoutException) { }
    }

    /// <summary>Every widget with its effective visibility, in tree order. Copies, safe to read off the gate.</summary>
    public IReadOnlyList<UiMirrorWidget> Snapshot()
    {
        lock (gate)
        {
            var list = new List<UiMirrorWidget>();
            if (root is not null) Walk(root, true, 0, list);
            return list;
        }
    }

    private void Index(JsonObject node)
    {
        if (node["id"]?.GetValue<string>() is { Length: > 0 } id) byId[id] = node;
        if (node["children"] is JsonArray children)
            foreach (var child in children.OfType<JsonObject>()) Index(child);
    }

    private static void Walk(JsonObject node, bool parentVisible, int depth, List<UiMirrorWidget> list)
    {
        bool visible = parentVisible && node["visible"]?.GetValue<bool>() != false;
        list.Add(new(node["id"]?.GetValue<string>() ?? "", node["type"]?.GetValue<string>() ?? "", Text(node),
            visible, node["enabled"]?.GetValue<bool>() != false, node["appearance"]?.GetValue<string>() ?? "", depth));
        if (node["children"] is JsonArray children)
            foreach (var child in children.OfType<JsonObject>()) Walk(child, visible, depth + 1, list);
    }

    private static string Text(JsonObject node) =>
        node["text"] is JsonValue text && text.TryGetValue(out string? value) ? value : "";

    private void Signal()
    {
        Version++;
        var done = changed;
        changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        done.TrySetResult();
    }
}

/// <param name="WireId">The id the editor sends back in a click; carries the view epoch and robot scope.</param>
/// <param name="Visible">False when this widget or any ancestor is hidden.</param>
internal sealed record UiMirrorWidget(string WireId, string Type, string Text, bool Visible, bool Enabled, string Appearance, int Depth)
{
    /// <summary>The id as the plugin code names it, without the view epoch or robot prefix.</summary>
    public string Id => PipersMcpPolicy.LocalId(WireId);
}
