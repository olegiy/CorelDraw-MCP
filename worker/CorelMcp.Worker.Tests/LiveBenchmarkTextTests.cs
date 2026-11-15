using System.Runtime.ExceptionServices;
using System.Text.Json;
using CorelMcp.Worker.Interop;
using Xunit;

namespace CorelMcp.Worker.Tests;

[Collection("CorelLive")]
public sealed class LiveBenchmarkTextTests
{
    public sealed class TextBenchmarkFactAttribute : FactAttribute
    {
        public TextBenchmarkFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("CORELDRAW_WORKER_LIVE") != "1"
                || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CORELDRAW_TEXT_BENCHMARK_ROOT")))
                Skip = "Нужны LIVE и каталог сохранённой text benchmark серии.";
        }
    }

    private sealed record TextRow(string Text, string Font, double Size, double Left, double Bottom, double Width, double Height);

    [TextBenchmarkFact]
    public void SavedBenchmarkTextHasActualFontSizeAndMatchingPhysicalGeometry()
    {
        var selected = Environment.GetEnvironmentVariable("CORELDRAW_TEXT_BENCHMARK_ROOT");
        Assert.False(string.IsNullOrEmpty(selected));
        var directory = Path.GetFullPath(selected!);
        using var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "benchmark.json")));
        var samples = report.RootElement.GetProperty("samples").EnumerateArray()
            .Where(row => row.GetProperty("case").GetString()!.StartsWith("text", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(samples);
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
                var documents = scope.Track(application.Documents);
                (long Identity, string Name, string Path, bool Dirty, int Unit)[] SourceSnapshot()
                {
                    var rows = new List<(long, string, string, bool, int)>();
                    for (var i = 1; i <= documents.Count; i++)
                    {
                        var source = scope.Track(documents[i]);
                        rows.Add((ComScope.Identity(source), source.Name, source.FullFileName, source.Dirty, (int)source.Unit));
                    }
                    return rows.OrderBy(row => row.Item1).ToArray();
                }
                var baseline = SourceSnapshot();
                var originalActive = documents.Count > 0 ? scope.Track(application.ActiveDocument) : null;
                var originalWindow = application.ActiveWindow is { } window ? scope.Track(window) : null;
                var optimization = application.Optimization;
                var eventsEnabled = application.EventsEnabled;
                var snapshots = new Dictionary<string, TextRow[]>();
                foreach (var sample in samples)
                {
                    var name = sample.GetProperty("case").GetString()!;
                    var mode = sample.GetProperty("mode").GetString()!;
                    var iteration = sample.GetProperty("iteration").GetInt32();
                    var file = Path.Combine(directory, $"{name}-{mode}-{iteration}.cdr");
                    var copy = Path.Combine(directory, $"text-check-{Guid.NewGuid():N}.cdr");
                    File.Copy(file, copy, false);
                    VGCore.Document? owned = null;
                    using var closeHold = new ComScope();
                    using var reading = new ComScope();
                    try
                    {
                        owned = closeHold.Track(application.OpenDocument(copy));
                        Assert.Equal(Path.GetFullPath(copy), Path.GetFullPath(owned.FullFileName), StringComparer.OrdinalIgnoreCase);
                        var shapes = reading.Track(reading.Track(owned.ActivePage).Shapes);
                        var count = int.Parse(name[4..], System.Globalization.CultureInfo.InvariantCulture);
                        Assert.Equal(count, shapes.Count);
                        var mm = application.ConvertUnits(1, owned.Unit, VGCore.cdrUnit.cdrMillimeter);
                        var rows = new List<TextRow>();
                        for (var index = 1; index <= shapes.Count; index++)
                        {
                            var shape = reading.Track(shapes[index]);
                            Assert.Equal(VGCore.cdrShapeType.cdrTextShape, shape.Type);
                            var story = reading.Track(reading.Track(shape.Text).Story);
                            Assert.Equal("Arial", story.Font);
                            Assert.InRange(story.Size, 11.999, 12.001);
                            var text = story.WideText;
                            var ordinal = int.Parse(text["Ёж — этикетка ".Length..], System.Globalization.CultureInfo.InvariantCulture);
                            Assert.Equal("Ёж — этикетка " + ordinal, text);
                            Assert.InRange(ordinal, 0, count - 1);
                            Assert.True(double.IsFinite(shape.LeftX) && double.IsFinite(shape.BottomY));
                            Assert.True(double.IsFinite(shape.SizeWidth) && shape.SizeWidth > 0);
                            Assert.True(double.IsFinite(shape.SizeHeight) && shape.SizeHeight > 0);
                            rows.Add(new(text, story.Font, story.Size, shape.LeftX * mm, shape.BottomY * mm,
                                shape.SizeWidth * mm, shape.SizeHeight * mm));
                        }
                        var ordered = rows.OrderBy(row => row.Text, StringComparer.Ordinal).ToArray();
                        Assert.Equal(count, ordered.Select(row => row.Text).Distinct().Count());
                        if (snapshots.TryGetValue(name, out var before))
                        {
                            for (var index = 0; index < count; index++)
                            {
                                Assert.Equal(before[index].Text, ordered[index].Text);
                                Assert.InRange(ordered[index].Left, before[index].Left - .001, before[index].Left + .001);
                                Assert.InRange(ordered[index].Bottom, before[index].Bottom - .001, before[index].Bottom + .001);
                                Assert.InRange(ordered[index].Width, before[index].Width - .001, before[index].Width + .001);
                                Assert.InRange(ordered[index].Height, before[index].Height - .001, before[index].Height + .001);
                            }
                        }
                        else snapshots.Add(name, ordered);
                        File.WriteAllText(Path.Combine(directory, $"text-proof-{name}-{mode}-{iteration}.json"), JsonSerializer.Serialize(ordered));
                    }
                    finally
                    {
                        reading.Dispose();
                        if (owned is not null) { owned.Dirty = false; owned.Close(); }
                    }
                }
                Assert.Equal(baseline, SourceSnapshot());
                Assert.Equal(optimization, application.Optimization);
                Assert.Equal(eventsEnabled, application.EventsEnabled);
                if (originalActive is not null)
                {
                    if (ComScope.Identity(scope.Track(application.ActiveDocument)) != ComScope.Identity(originalActive))
                    {
                        originalActive.Activate();
                        originalWindow?.Activate();
                    }
                    Assert.Equal(ComScope.Identity(originalActive), ComScope.Identity(scope.Track(application.ActiveDocument)));
                }
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
