using System.Text.Json;
using System.Text.Json.Nodes;
using LuckyEngine.PluginSdk;
using LuckyRobots.Piper.Plugin;

internal static class PipersMcpTests
{
    public static void RunPolicy()
    {
        Check(PipersMcpPolicy.LocalId("view.12.robot.piper-1.joint.6.plus") == "joint.6.plus" &&
            PipersMcpPolicy.LocalId("robot.piper-1.stop") == "stop" && PipersMcpPolicy.LocalId("stop.piper-3") == "stop.piper-3" &&
            PipersMcpPolicy.LocalId("view.3.group.follow.toggle") == "group.follow.toggle", "Wire ids did not reduce to the plugin's own ids");
        foreach (string free in new[] { "stop", "stop.piper-1", "simulation.stop", "motion.hold", "group.follow.cancel", "workspace.follow", "profiles.select.piper-3", "backend.start", "backend.restart" })
            Check(PipersMcpPolicy.Free(free), free + " should not need the operator's go");
        foreach (string guarded in new[] { "enable", "safety.accept", "zero.all", "joint.6.plus", "pika.target", "group.follow.toggle", "group.follow.arm.piper-1",
            "disable", "disable.confirm.accept", "backend.restart.confirm", "recovery.home", "replay.play", "simulation.toggle", "teleoperate.toggle" })
            Check(!PipersMcpPolicy.Free(guarded), guarded + " must need the operator's go");
        static JsonElement Info(string json) => JsonDocument.Parse(json).RootElement;
        Check(PipersMcp.EditorOffersAgents(Info("{\"api\":2,\"agentMcpHttpPort\":19331}")) &&
            !PipersMcp.EditorOffersAgents(Info("{\"api\":2,\"agentMcpHttpPort\":0}")) && !PipersMcp.EditorOffersAgents(Info("{\"api\":2}")) &&
            !PipersMcp.EditorOffersAgents(Info("{\"agentMcpHttpPort\":\"19331\"}")), "Pipers must offer agent tools exactly when the editor reports its MCP port");
        Check(PipersMcpPolicy.Rejected("engine.robot.target") && !PipersMcpPolicy.Rejected("stop"), "Editor-only streams must never be accepted from an agent");
    }

    public static async Task RunTools()
    {
        var fixture = new Fixture();
        var tools = PipersMcp.Detached(fixture.Plugin);
        await fixture.Plugin.Render(CancellationToken.None);

        var list = await tools.Dispatch("tools/list", new());
        Check(list["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).SequenceEqual(["pipers_status", "pipers_ui", "pipers_click", "pipers_wait", "pipers_last_failure"]),
            "The tool list changed");
        var init = await tools.Dispatch("initialize", new() { ["protocolVersion"] = "2025-06-18" });
        Check(init["protocolVersion"]!.GetValue<string>() == "2025-06-18" && init["serverInfo"]!["name"]!.GetValue<string>() == "pipers", "initialize did not echo the client's version");

        var status = JsonNode.Parse(await Text(tools, "pipers_status"))!;
        Check(status["robots"]![0]!["name"]!.GetValue<string>() == "Piper 1" && status["server"]!.GetValue<string>() == "Not running" &&
            status["follow"]!["active"]!.GetValue<bool>() == false, "Status did not describe the robot, the server and Follow");

        string outline = await Text(tools, "pipers_ui");
        Check(outline.Contains("enable [", StringComparison.Ordinal) && !outline.Contains("view.", StringComparison.Ordinal),
            "The outline did not list visible widgets by their plugin ids");

        // A patch reaches the mirror: a red action error shows up in status.
        fixture.Plugin.uiActionError = "Boom from a patch";
        await fixture.Plugin.Update(CancellationToken.None);
        status = JsonNode.Parse(await Text(tools, "pipers_status"))!;
        Check(status["errors"]!.AsArray().Any(e => e!["text"]!.GetValue<string>().Contains("Boom from a patch", StringComparison.Ordinal)),
            "A patched red error did not reach the status");

        // Motion needs the operator's go, and nothing is sent without it.
        int before = fixture.Publications;
        var refused = await Call(tools, "pipers_click", new() { ["id"] = "enable" });
        Check(refused.IsError && refused.Text.Contains("operator's go", StringComparison.Ordinal) && fixture.Publications == before,
            "Enable ran without the operator's go");

        var hidden = await Call(tools, "pipers_click", new() { ["id"] = "disable.confirm.cancel" });
        Check(hidden.IsError && hidden.Text.Contains("hidden", StringComparison.Ordinal), "A hidden widget was pressed");
        var label = await Call(tools, "pipers_click", new() { ["id"] = "workspace.next" });
        Check(label.IsError && label.Text.Contains("not something to press", StringComparison.Ordinal) && !fixture.Plugin.uiActionError.Contains("Unknown", StringComparison.Ordinal),
            "A label was sent to the plugin as a click");

        var missing = await Call(tools, "pipers_click", new() { ["id"] = "no.such.widget" });
        Check(missing.IsError && missing.Text.Contains("pipers_ui", StringComparison.Ordinal), "A missing widget did not point to pipers_ui");

        // Navigation is free and goes through the same gate as a mouse click.
        var navigated = await Call(tools, "pipers_click", new() { ["id"] = "workspace.follow" });
        Check(!navigated.IsError && fixture.Plugin.workspace.Navigation.Page == WorkspacePage.Follow &&
            (await Text(tools, "pipers_ui")).Contains("group.follow.toggle", StringComparison.Ordinal), "Navigation by id did not open the Follow page");
        var follow = await Call(tools, "pipers_click", new() { ["id"] = "group.follow.toggle" });
        Check(follow.IsError && follow.Text.Contains("operator's go", StringComparison.Ordinal), "Follow simulation started without the operator's go");

        // Wait returns as soon as the panel shows the text.
        var waiting = Call(tools, "pipers_wait", new() { ["id"] = "workspace.action-error", ["contains"] = "later", ["timeoutMs"] = 3000 });
        await Task.Delay(100);
        fixture.Plugin.uiActionError = "Shown later";
        await fixture.Plugin.Update(CancellationToken.None);
        var waited = await waiting.WaitAsync(TimeSpan.FromSeconds(3));
        var result = JsonNode.Parse(waited.Text)!["waited"]!;
        Check(!waited.IsError && result["met"]!.GetValue<bool>() && result["ms"]!.GetValue<long>() < 2500, "Wait did not return when the text appeared");
        var timedOut = JsonNode.Parse((await Call(tools, "pipers_wait", new() { ["id"] = "workspace.action-error", ["contains"] = "never", ["timeoutMs"] = 50 })).Text)!;
        Check(timedOut["waited"]!["met"]!.GetValue<bool>() == false, "Wait reported a condition that never became true");
    }

    private static async Task<string> Text(PipersMcp tools, string name) => (await Call(tools, name, new())).Text;

    private static async Task<(bool IsError, string Text)> Call(PipersMcp tools, string name, JsonObject args)
    {
        var result = await tools.Dispatch("tools/call", new() { ["name"] = name, ["arguments"] = args });
        return (result["isError"]!.GetValue<bool>(), result["content"]![0]!["text"]!.GetValue<string>());
    }

    private sealed class Fixture
    {
        public PipersPlugin Plugin { get; } = new();
        public int Publications;
        public Fixture()
        {
            Plugin.preferences = RobotProfileRouting.Normalize(new Preferences(Robots: [new("one", "Piper 1")], ActiveRobotId: "one"));
            Plugin.engine = Fixtures.Context(new PluginStart("fixture", Path.GetTempPath(), Path.GetTempPath()),
                (Func<object, CancellationToken, Task>)((_, _) => { Interlocked.Increment(ref Publications); return Task.CompletedTask; }),
                (Func<string, JsonObject, CancellationToken, Task<JsonElement>>)((_, _, _) => throw new InvalidOperationException("Unexpected engine request")));
        }
    }
}
