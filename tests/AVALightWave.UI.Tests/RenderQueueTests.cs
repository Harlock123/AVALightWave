using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using AVALightWave.Core.Tests;

namespace AVALightWave.UI.Tests;

/// <summary>
/// Render › Render Queue headless (adding the scene, local rendering and its progress, cancelling, removing finished
/// jobs, a farm folder), and a real render node process (AVALightWave --render-node) rendering a tiny job.
/// </summary>
public class RenderQueueTests
{
    private static RenderQueueWindow Open(MainWindow w, Workbench bench)
    {
        w.QueueFolder = bench.File("queue");
        w.LocalNodeOptions = new FarmNodeOptions { Name = "test window", Threads = 1, Poll = TimeSpan.FromMilliseconds(100), Heartbeat = TimeSpan.FromMilliseconds(100) };
        var q = w.OpenRenderQueue();
        Ui.Pump();
        Ui.Find<TextBox>(q, "QueueWidth").Text = "32";
        Ui.Find<TextBox>(q, "QueueHeight").Text = "24";
        Ui.Find<TextBox>(q, "QueueSamples").Text = "1";
        Ui.Find<TextBox>(q, "QueueFirst").Text = "1";
        Ui.Find<TextBox>(q, "QueueLast").Text = "3";
        return q;
    }

    private static void Refresh(RenderQueueWindow q) => Ui.Wait(q.RefreshAsync());

    [AvaloniaFact]
    public void The_queue_takes_the_scene_renders_it_here_and_cancels_and_clears_jobs()
    {
        using var bench = new Workbench();
        var w = Ui.Open();
        w.LoadObject(bench.Object("crate.lwo"));
        var q = Open(w, bench);
        Assert.Same(q, w.OpenRenderQueue());
        Assert.Contains(bench.File("queue"), Ui.Find<TextBlock>(q, "QueueFolder").Text);
        Assert.Contains("AVALightWave --render-node", q.Command);
        Refresh(q);
        Assert.Empty(q.Jobs);
        Assert.Contains("No jobs", q.Detail);

        long version = w.Session.Scene.Version;
        var sent = Ui.Wait(q.AddCurrentScene())!;
        Assert.Equal(3, sent.Job.Frames.Count);
        Assert.Equal(version, w.Session.Scene.Version);
        var job = Assert.Single(q.Jobs);
        Assert.Equal(FarmJobState.Waiting, job.State);
        Assert.Contains("No node is rendering", q.Status);
        Assert.Contains("0 of 3 frames, waiting", Assert.IsType<Grid>(Assert.Single(Ui.Find<ListBox>(q, "QueueJobs").Items)).Children.OfType<TextBlock>().Single().Text);

        // Start Local Rendering: a node in this process works the queue, and the list follows it.
        Ui.Wait(q.ToggleLocal());
        Assert.True(w.LocalRendering);
        Assert.Equal("Stop Local Rendering", Ui.Find<Button>(q, "QueueLocal").Content);
        Ui.Until(() => { Refresh(q); return q.Jobs.Single().State == FarmJobState.Done; }, "The local node didn't render the job.");
        Assert.Equal(3, q.Jobs.Single().Done);
        Ui.Until(() => { Refresh(q); return q.Nodes.Any(n => n.Name == "test window"); }, "The local node wasn't listed.");
        Assert.Contains("test window", q.NodesText);
        Assert.True(File.Exists(Path.Combine(sent.OutputFolder, "frame0003.png")));
        Ui.Wait(q.ToggleLocal());
        Assert.False(w.LocalRendering);

        // A second job, cancelled before any node takes it.
        Ui.Find<TextBox>(q, "QueueJobName").Text = "Second";
        var second = Ui.Wait(q.AddCurrentScene())!;
        Ui.Find<ListBox>(q, "QueueJobs").SelectedIndex = 1;
        Assert.Equal(second.Job.Id, q.SelectedId);
        Assert.Contains(second.OutputFolder, q.Detail);
        Ui.Wait(q.CancelSelected());
        Assert.Equal(FarmJobState.Cancelled, q.Jobs.Single(j => j.Id == second.Job.Id).State);
        Assert.Equal(second.Job.Id, q.SelectedId);
        // Move it ahead of the first, then clear both away.
        Ui.Wait(q.Move(-1));
        Assert.Equal(second.Job.Id, q.Jobs[0].Id);
        Ui.Wait(q.RemoveFinished());
        Assert.Empty(q.Jobs);
        Assert.Contains("Removed 2 finished jobs", q.Status);
        Assert.False(Directory.Exists(sent.Folder));

        // A bad setting is said, not sent.
        Ui.Find<TextBox>(q, "QueueSamples").Text = "lots";
        Assert.Null(Ui.Wait(q.AddCurrentScene()));
        Assert.Contains("whole number", q.Status);
        q.Close();
        w.Close();
    }

    [AvaloniaFact]
    public void A_farm_folder_can_be_chosen_and_local_rendering_stops_when_it_changes()
    {
        using var bench = new Workbench();
        var w = Ui.Open();
        var q = Open(w, bench);
        string farm = Directory.CreateDirectory(bench.File("farm")).FullName;
        q.PickFolder = () => Task.FromResult<string?>(farm);
        Ui.Wait(q.ToggleLocal());
        Assert.True(w.LocalRendering);
        var choose = q.GetLogicalDescendants().OfType<Button>().First(b => (b.Content as string) == "Farm Folder…");
        choose.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Ui.Until(() => w.QueueFolder == farm, "The farm folder wasn't taken.");
        Ui.Until(() => !w.LocalRendering, "Local rendering didn't stop.");
        Assert.Contains("Render farm: " + farm, Ui.Find<TextBlock>(q, "QueueFolder").Text);
        Assert.Equal($"AVALightWave --render-node \"{farm}\"", q.Command);
        // Agents' queue_render goes where the window's queue is.
        Assert.Equal(farm, w.Agents.QueueFolder);
        Ui.Wait(q.UseFolder(w.LocalQueueFolder));
        Assert.StartsWith("Local queue: ", Ui.Find<TextBlock>(q, "QueueFolder").Text);
        Assert.False(Directory.Exists(w.LocalQueueFolder));
        q.Close();
        w.Close();
    }

    [Fact]
    public async Task A_render_node_process_renders_a_job_from_the_command_line()
    {
        using var bench = new Workbench();
        var d = new SceneDocument();
        d.New();
        d.LoadObject(bench.Object("crate.lwo"));
        string farm = bench.File("farm");
        var sent = new FarmFolder(farm).Submit(FarmFolder.Snapshot(d.Scene), new FarmSubmit { Name = "Process", First = 1, Last = 2, Width = 32, Height = 24, Samples = 1 });

        string app = Path.Combine(AppContext.BaseDirectory, "AVALightWave.dll");
        Assert.True(File.Exists(app), "The app wasn't built beside the tests.");
        string dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && File.Exists(host) ? host : "dotnet";
        var start = new ProcessStartInfo(dotnet)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            WorkingDirectory = bench.Path,
        };
        foreach (var a in new[] { app, "--render-node", farm, "--once", "--threads", "1", "--name", "child" }) start.ArgumentList.Add(a);
        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var errors = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        try { await process.WaitForExitAsync(limit.Token); }
        catch (OperationCanceledException)
        {
            // Overran: stop exactly this process (never by name or pattern).
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            Assert.Fail("The render node didn't finish in two minutes.");
        }
        string said = await output, problems = await errors;
        Assert.True(process.ExitCode == 0, $"Exit code {process.ExitCode}: {problems}");
        Assert.Contains("AVALightWave render node child", said);
        Assert.Equal(2, said.Split('\n').Count(l => l.StartsWith("Rendered frame")));
        Assert.Equal(FarmJobState.Done, new FarmFolder(farm).Status(sent.Job.Id).State);
        Assert.True(File.Exists(Path.Combine(sent.OutputFolder, "frame0002.png")));
    }
}
