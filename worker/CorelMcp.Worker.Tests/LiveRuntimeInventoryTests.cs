using System.Runtime.ExceptionServices;
using System.Text.Json;
using CorelMcp.Worker.Hybrid;
using CorelMcp.Worker.Interop;
using Xunit;

namespace CorelMcp.Worker.Tests;

[Collection("CorelLive")]
public sealed class LiveRuntimeInventoryTests
{
    [LiveCorelFact]
    public void LoadedRuntimeInventoryRecordsExactPathsWithoutLoadingOrUnloadingProjects()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var lease = ComOperationMutex.Acquire();
                using var connection = new CorelConnection();
                connection.Connect("CorelDRAW.Application.27");
                using var scope = new ComScope();
                var application = connection.GetApplication();
                var projects = scope.Track(scope.Track(application.GMSManager).Projects);
                using var module = NativeModuleManifest.LoadAndVerify(Path.Combine(AppContext.BaseDirectory, "native"));
                var entries = new List<object>();
                var matches = 0;
                var expectedLoadedPath = Environment.GetEnvironmentVariable("CORELDRAW_RUNTIME_EXPECTED_PATH");
                for (var index = 1; index <= projects.Count; index++)
                {
                    var project = scope.Track(projects[index]);
                    if (project.Name == module.ProjectName)
                    {
                        matches++;
                        if (expectedLoadedPath is not null)
                        {
                            Assert.Equal(Path.GetFullPath(expectedLoadedPath), Path.GetFullPath(project.FullFileName), StringComparer.OrdinalIgnoreCase);
                            Assert.False(project.Dirty);
                        }
                    }
                    entries.Add(new { name = project.Name, path = project.FullFileName, dirty = project.Dirty });
                }
                var directory = Path.Combine(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../..")),
                    "artifacts", "runtime-inventory", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "inventory.json"), JsonSerializer.Serialize(new
                {
                    corel_version = application.Version, expected_name = module.ProjectName,
                    expected_path = module.FullPath, matches, projects = entries,
                    document_count = scope.Track(application.Documents).Count,
                    optimization = application.Optimization, events_enabled = application.EventsEnabled
                }, new JsonSerializerOptions { WriteIndented = true }));
                Assert.Equal(expectedLoadedPath is null ? 0 : 1, matches);
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
