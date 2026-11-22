using System.Text.Json.Nodes;

namespace LuckyRobots.Piper.Plugin;

/// <summary>What the agent endpoint reports. Like the other view builders it reads plugin state and
/// writes nothing; the panel content comes from <see cref="WorkspaceComponent.Mirror"/>.</summary>
public sealed partial class PipersPlugin
{
    internal const string PanelId = Panel;

    /// <summary>One compact snapshot an agent reads instead of a screenshot.</summary>
    internal JsonObject AgentStatus()
    {
        var widgets = workspace.Mirror.Snapshot();
        string Shown(string id) => widgets.FirstOrDefault(w => w.Id == id && w.Visible)?.Text ?? "";
        var robots = new JsonArray();
        foreach (var profile in preferences.Robots ?? [])
        {
            var (word, tone) = ProfileDisplay(profile);
            robots.Add(new JsonObject { ["id"] = profile.Id, ["name"] = profile.Name, ["status"] = word, ["tone"] = tone, ["followSelected"] = profile.FollowSimulationSelected });
        }
        var errors = new JsonArray(widgets.Where(w => w.Visible && w.Appearance == "danger" && w.Text.Length > 0)
            .Select(w => (JsonNode)new JsonObject { ["id"] = w.Id, ["text"] = w.Text }).ToArray());
        return new JsonObject
        {
            ["server"] = ServerWord,
            ["serverStatus"] = Shown("backend.status"),
            ["page"] = Navigation.Page.ToString(),
            ["selectedRobot"] = ActiveProfile.Id,
            ["robots"] = robots,
            ["follow"] = new JsonObject
            {
                ["active"] = GroupFollowActive,
                ["stage"] = follow.Outcome.Stage.ToString(),
                ["status"] = Shown("group.follow.status"),
                ["lastFailure"] = LastRunFailure(),
                ["failedRobot"] = LastRunFailedProfile,
            },
            ["message"] = message,
            ["actionError"] = uiActionError,
            ["errors"] = errors,
            ["uiVersion"] = workspace.Mirror.Version,
        };
    }

    internal string AgentLastFailure(bool full)
    {
        string directory = Path.Combine(engine.Start.SettingsDirectory, "diagnostics", "simulation-follow");
        var file = Directory.Exists(directory) ? new DirectoryInfo(directory).GetFiles("*.json").MaxBy(f => f.LastWriteTimeUtc) : null;
        if (file is null) return "No follow diagnostics recorded yet.";
        string raw = File.ReadAllText(file.FullName);
        if (full) return raw;
        var failure = JsonNode.Parse(raw)?["firstFailure"] as JsonObject;
        var reduced = new JsonObject { ["file"] = file.Name, ["written"] = file.LastWriteTimeUtc.ToString("O") };
        if (failure is null) { reduced["firstFailure"] = null; return reduced.ToJsonString(); }
        foreach (string key in new[] { "RecordedUtc", "Phase", "Operation", "ErrorType", "Reason", "Ordered" })
            reduced[key] = failure[key]?.DeepClone();
        return reduced.ToJsonString();
    }
}
