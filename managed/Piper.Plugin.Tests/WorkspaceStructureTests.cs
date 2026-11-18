using System.Text.Json;
using System.Text.Json.Nodes;
using LuckyEngine.PluginSdk;
using LuckyRobots.Piper.Plugin;

/// <summary>The workspace's structure: the sidebar is the only navigation, a robot's page has tabs,
/// a bench page belongs to no single robot, and a follower group is chosen on one page.</summary>
internal static class WorkspaceStructureTests
{
    private static IEnumerable<Widget> All(Widget root) => new[] { root }.Concat((root.Children ?? []).SelectMany(All));

    public static void RunNavigation()
    {
        var start = WorkspaceNavigation.Initial;
        JsonElement Index(int value) => JsonSerializer.SerializeToElement(value);
        // Each robot tab lands on a page that reports the same tab back.
        string[] expected = ["controls", "robot-cameras", "recording", "settings.robot"];
        for (int tab = 0; tab < WorkspaceTabs.RobotTabs.Length; tab++)
        {
            string action = WorkspaceTabs.Action("pages", Index(tab));
            var page = start.Navigate(action);
            Check(action == expected[tab] && WorkspaceTabs.PageIndex(page) == tab && !WorkspaceTabs.BenchPage(page),
                "Robot tab " + WorkspaceTabs.RobotTabs[tab] + " did not open a page of the selected robot under the same tab");
        }
        Check(WorkspaceTabs.Action("settings.robot", Index(0)) == "settings.robot", "An ordinary workspace action was rewritten as a tab choice");
        bool rejected = false;
        try { WorkspaceTabs.Action("pages", Index(WorkspaceTabs.RobotTabs.Length)); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "A tab index outside the strip was accepted");

        foreach (string action in new[] { "overview", "follow", "camera.bench", "settings.diagnostics", "settings.cameras.bench" })
            Check(WorkspaceTabs.BenchPage(start.Navigate(action)), action + " is a bench page but was shown as a robot's page");
        foreach (string action in new[] { "controls", "recording", "replay", "settings.robot", "settings.bench", "camera.depth", "settings.cameras.gripper" })
            Check(!WorkspaceTabs.BenchPage(start.Navigate(action)), action + " belongs to the selected robot but was shown as a bench page");
        // The bench camera's setup must not be reopened as a robot's own camera.
        var robotCameras = start.Navigate("settings.cameras.bench").Navigate("robot-cameras");
        Check(robotCameras is { Page: WorkspacePage.Settings, Category: SettingsCategory.Cameras } && robotCameras.SettingsCamera != "bench",
            "A robot's Cameras tab opened the shared bench camera");
        Check(start.Navigate("settings.bench").CalibrationOpen && WorkspaceTabs.PageIndex(start.Navigate("settings.bench")) == 3,
            "Mounting calibration no longer sits under the robot's Setup tab");
    }

    public static async Task RunSidebarAndFollowPage()
    {
        var plugin = new PipersPlugin();
        var first = new RobotProfile("one", "Piper 1");
        var second = new RobotProfile("two", "Piper 3") with { FollowSimulationSelected = true };
        plugin.preferences = RobotProfileRouting.Normalize(new Preferences { Robots = [first, second], ActiveRobotId = "one" });
        // Preferences are saved to a throwaway folder; nothing is sent to an editor or a controller.
        string folder = Path.Combine(Path.GetTempPath(), "piper-workspace-structure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        plugin.engine = Fixtures.Context(new PluginStart("fixture", folder, folder), (_, _) => Task.CompletedTask,
            (_, _, _) => Task.FromResult(JsonSerializer.SerializeToElement(new { applied = false })));
        try { await Exercise(plugin); }
        finally { Directory.Delete(folder, true); }
    }

    private static async Task Exercise(PipersPlugin plugin)
    {

        var sidebar = All(plugin.RobotSidebar()).ToArray();
        Check(new[] { "profiles.select.one", "profiles.select.two", "profiles.add", "workspace.overview", "workspace.follow",
                "workspace.camera.bench@nav", "workspace.settings.diagnostics@nav", "group.follow.cancel" }.All(id => sidebar.Count(w => w.Id == id) == 1),
            "The sidebar lost a robot, a bench page, the arm server or Stop following");
        Check(sidebar.Single(w => w.Id == "profiles.select.one") is { Type: "nav-item", Value: 1 } &&
            sidebar.Single(w => w.Id == "profiles.select.two") is { Type: "nav-item", Value: 0 } &&
            sidebar.Single(w => w.Id == "group.follow.cancel").Visible == false,
            "The sidebar did not mark the open robot, or offered Stop following with no group running");

        // Each robot has its own STOP beside its name. It names its robot, so it reaches a robot that
        // is not open, on any page, and survives a view being republished.
        Check(sidebar.Count(w => w.Id == "stop.one" && w.Text == "Stop") == 1 && sidebar.Count(w => w.Id == "stop.two" && w.Text == "Stop") == 1,
            "A robot in the sidebar has no STOP of its own");
        // Neither fixture robot has an adapter, so there is nothing to stop: the button is shown but off.
        Check(sidebar.Where(w => w.Id is "stop.one" or "stop.two").All(w => w.Enabled == false && w.Hint!.Contains("not connected", StringComparison.Ordinal)),
            "A robot that is not connected still offered a live Stop");
        foreach (long epoch in new long[] { 0, 7 })
            Check(WorkspaceEventScope.Wrap(epoch, "one", "stop.two") == "stop.two", "A sidebar STOP was tied to the open robot or to one published view");
        Check(plugin.CaptureStopProfile("stop.two", plugin.preferences.Robots!.Single(p => p.Id == "one"))?.Id == "two" &&
            plugin.CaptureStopProfile("stop.one", plugin.preferences.Robots!.Single(p => p.Id == "one"))?.Id == "one" &&
            plugin.CaptureStopProfile("stop.gone", plugin.preferences.Robots!.Single(p => p.Id == "one")) is null,
            "A sidebar STOP did not stop exactly the robot it names");

        // Coming back to the open robot from a bench page is navigation only: it keeps control.
        plugin.workspace.Commit(WorkspaceNavigation.Initial.Navigate("overview"));
        plugin.approved = true;
        await plugin.HandleEventCoreAsync(new("fixture", WorkspaceEventScope.Wrap(plugin.workspace.Epoch, "one", "profiles.select.one"), "click",
            JsonSerializer.SerializeToElement(true)), CancellationToken.None);
        Check(plugin.approved && plugin.workspace.Navigation.Page == WorkspacePage.Controls && plugin.preferences.ActiveRobotId == "one",
            "Returning to the open robot from Overview released its control or did not open its Controls");
        plugin.approved = false;

        var page = All(plugin.FollowPage()).ToArray();
        Check(page.Single(w => w.Id == "group.follow.include.one").Value == 0 && page.Single(w => w.Id == "group.follow.include.two").Value == 1 &&
            page.Single(w => w.Id == "group.follow.ready.one") is { Text: "Not included", Appearance: "muted" } &&
            page.Count(w => w.Id == "group.follow.toggle") == 1 && page.All(w => w.Id != "group.follow.cancel"),
            "The Follow simulation page did not list every robot with its own Include tick");

        // Ticking a row includes that robot, whichever robot is open.
        await plugin.GroupFollowWorkspaceEvent("include.one", JsonSerializer.SerializeToElement(true), CancellationToken.None);
        Check(plugin.preferences.Robots!.Single(p => p.Id == "one").FollowSimulationSelected && plugin.preferences.ActiveRobotId == "one",
            "Including a robot from the Follow simulation page did not save, or changed the open robot");
        await plugin.GroupFollowWorkspaceEvent("include.two", JsonSerializer.SerializeToElement(false), CancellationToken.None);
        Check(!plugin.preferences.Robots!.Single(p => p.Id == "two").FollowSimulationSelected, "A robot that is not open could not be taken out of the group");
        bool rejected = false;
        try { await plugin.GroupFollowWorkspaceEvent("include.gone", JsonSerializer.SerializeToElement(true), CancellationToken.None); }
        catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "A tick for a robot that no longer exists was accepted");

        var overview = All(plugin.OverviewPage()).ToArray();
        Check(new[] { "one", "two" }.All(id => overview.Count(w => w.Id == "overview.state." + id) == 1 && overview.Count(w => w.Id == "overview.next." + id) == 1),
            "The overview did not show one row per robot");
    }
}
