using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using CorelMcp.Worker.Interop;

namespace CorelMcp.Worker.Operations;

internal static class DuplicateDemo
{
    public const string ProjectName = "CorelMcp_DuplicateDemo";
    private const string MacroName = "M_DuplicateDemo.RunRegistered";
    private const double GapMm = 5;

    internal static (string Path, byte[] Sha256) Configuration()
    {
        string? path = Environment.GetEnvironmentVariable("CORELDRAW_MCP_DUPLICATE_DEMO_GMS");
        string? hash = Environment.GetEnvironmentVariable("CORELDRAW_MCP_DUPLICATE_DEMO_SHA256");
        if (string.IsNullOrEmpty(path) || !Regex.IsMatch(path, @"\A[A-Za-z]:[\\/]") ||
            !string.Equals(Path.GetExtension(path), ".gms", StringComparison.OrdinalIgnoreCase) ||
            hash is null || !Regex.IsMatch(hash, @"\A[0-9a-fA-F]{64}\z"))
            throw new OperationFailure("MACRO_DISABLED", "Тестовый GMS и его SHA-256 должны быть заданы владельцем сервера.");
        return (Path.GetFullPath(path), Convert.FromHexString(hash));
    }

    private static FileStream Pin(string path, byte[] expected)
    {
        try
        {
            for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("GMS path contains a reparse point.");
            var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                if (stream.Length is < 1 or > 16 * 1024 * 1024 ||
                    !CryptographicOperations.FixedTimeEquals(SHA256.HashData(stream), expected))
                    throw new IOException("GMS digest mismatch.");
                return stream;
            }
            catch { stream.Dispose(); throw; }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new OperationFailure("MACRO_ARTIFACT_INVALID", "Чтение или проверка SHA-256 тестового GMS не прошли.");
        }
    }

    public static object Execute(OperationContext c, RunMacroArguments args)
    {
        c.Stage("macro_preflight");
        var (path, expected) = Configuration();
        using var pinned = Pin(path, expected);
        var target = new DrawingTarget(c, args.PageIndex);
        var original = target.Resolve(args.StaticIds, groups: false)[0];
        var before = Snapshot(c, target);
        if (!before.ContainsKey(original.StaticID))
            throw new OperationFailure("UNSUPPORTED_MACRO_TARGET", "Тестовый макрос принимает фигуру верхнего уровня.");
        original.GetBoundingBox(out _, out _, out double width, out _, true);
        double step = width + target.U(GapMm);
        if (!double.IsFinite(step) || step <= 0)
            throw new OperationFailure("INVALID_GEOMETRY", "Шаг копирования не определён.");

        c.Stage("verifying_macro");
        var manager = c.Track(c.App.GMSManager);
        var projects = c.Track(manager.Projects);
        for (int index = 1; index <= projects.Count; index++)
        {
            var project = c.Track(projects[index]);
            if (string.Equals(project.Name, ProjectName, StringComparison.OrdinalIgnoreCase) || SamePath(project, path))
                throw new OperationFailure("MACRO_ALREADY_LOADED", "Сначала выгрузите открытый тестовый GMS, чтобы загрузить проверенную версию с диска.");
        }
        var registered = c.Track(projects.Load(path, false, false));
        c.DeferCleanup("macro_gms", () =>
        {
            if (c.Session.NativeOutcomeUnknown || !Matches(registered, path))
            {
                c.Cleanup.ProjectRetained = true;
                throw new OperationFailure("MACRO_RETAINED", "Тестовый проект оставлен загруженным до проверки исхода.", null);
            }
            try { registered.Unload(); }
            catch
            {
                c.Cleanup.ProjectRetained = true;
                throw;
            }
        });
        if (!Matches(registered, path)) throw InvalidIdentity();

        c.DrawingDetails["macro_id"] = args.MacroId;
        c.DrawingDetails["macro_version"] = Convert.ToHexStringLower(expected);
        c.DrawingDetails["requested_copies"] = args.Parameters.Count;
        c.DrawingDetails["gap_mm"] = GapMm;
        target.Document.Activate();
        target.Page.Activate();
        bool dirtyBefore = target.Document.Dirty;
        CommandGroup.Run(
            () => { c.Mutating(); target.Document.BeginCommandGroup("CorelMCP duplicate_demo"); },
            () =>
            {
                if (target.Document.Dirty != dirtyBefore)
                {
                    c.RecordMutation([], []);
                    c.DrawingDetails["document_dirty_changed"] = true;
                }
                c.Stage("executing_macro");
                string nonce = Guid.NewGuid().ToString("N");
                object[] parameters = [original, args.Parameters.Count, step, nonce];
                MacroReply reply;
                try
                {
                    c.ExecutionEngine = "vba_gms";
                    reply = ReadReply(manager.RunMacro(ProjectName, MacroName, ref parameters) as string, nonce, args.Parameters.Count);
                    c.Stage("verifying_macro_result");
                    var after = Snapshot(c, target);
                    var createdIds = after.Keys.Except(before.Keys).ToHashSet();
                    if (!before.Keys.All(after.ContainsKey) || !createdIds.SetEquals(reply.Ids))
                        throw new InvalidDataException("Несовпадение отчёта и состава страницы.");
                    c.RecordKnownChanges([], createdIds, []);
                    foreach (int id in reply.Ids) c.DrawingItems.Add(target.Info(after[id]));
                }
                catch (Exception)
                {
                    c.Session.MarkNativeOutcomeUnknown();
                    throw new OperationFailure("NATIVE_OUTCOME_UNKNOWN", "Исход VBA-вызова не подтверждён; повтор запрещён.", null);
                }
                c.DrawingDetails["vba_status"] = reply.ErrorNumber == 0 ? "ok" : "error";
                if (reply.ErrorNumber != 0)
                {
                    c.DrawingDetails["vba_error_number"] = reply.ErrorNumber;
                    c.Stage("vba_runtime_error");
                    throw new OperationFailure("MACRO_RUNTIME_ERROR", "Тестовый макрос завершился ошибкой VBA.");
                }
                c.Stage("mutation_observed");
            },
            target.Document.EndCommandGroup, c.Cleanup);
        return c.DrawingResult(false);
    }

    private static Dictionary<int, VGCore.Shape> Snapshot(OperationContext c, DrawingTarget target)
    {
        var shapes = c.Track(target.Page.Shapes);
        var result = new Dictionary<int, VGCore.Shape>();
        for (int index = 1; index <= shapes.Count; index++)
        {
            var shape = c.Track(shapes[index]);
            result.Add(shape.StaticID, shape);
        }
        return result;
    }

    private static bool Matches(VGCore.GMSProject project, string path) =>
        project.Name == ProjectName && !project.Dirty && SamePath(project, path);

    private static bool SamePath(VGCore.GMSProject project, string path)
    {
        string? fullName = project.FullFileName;
        return !string.IsNullOrEmpty(fullName) && Path.IsPathFullyQualified(fullName) &&
            string.Equals(Path.GetFullPath(fullName), path, StringComparison.OrdinalIgnoreCase);
    }

    private static OperationFailure InvalidIdentity() =>
        new("MACRO_IDENTITY_MISMATCH", "Имя, путь или сохранённое состояние загруженного тестового GMS не совпадает.");

    internal sealed record MacroReply(int ErrorNumber, int[] Ids);

    internal static MacroReply ReadReply(string? raw, string nonce, int count)
    {
        if (raw is null || raw.Length > 16384) throw new InvalidDataException("Нет результата VBA.");
        string[] parts = raw.Split('|');
        if (parts.Length != 4 || parts[0] != nonce ||
            !int.TryParse(parts[2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int error) ||
            (parts[1] == "OK" ? error != 0 : parts[1] != "ERROR" || error == 0))
            throw new InvalidDataException("Неверный статус VBA.");
        string[] fields = parts[3].Length == 0 ? [] : parts[3].Split(',');
        if (fields.Length > count || (error == 0 && fields.Length != count))
            throw new InvalidDataException("Неверное число копий в результате VBA.");
        int[] ids = new int[fields.Length];
        var seen = new HashSet<int>();
        for (int index = 0; index < fields.Length; index++)
            if (!int.TryParse(fields[index], NumberStyles.None, CultureInfo.InvariantCulture, out ids[index])
                || ids[index] <= 0 || !seen.Add(ids[index]))
                throw new InvalidDataException("Неверный StaticID копии.");
        return new(error, ids);
    }
}
