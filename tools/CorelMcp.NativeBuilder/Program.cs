using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CorelMcp.Worker.Interop;

// Developer-only build tool. Never referenced or packaged by the runtime worker.
internal static class Program
{
    [STAThread] private static int Main(string[] args)
    {
        if (args.Length != 2 || args[0] is not ("--developer-prepare" or "--developer-seal" or "--developer-discard-prepared" or "--developer-unload-test-runtime" or "--developer-unload-local-runtime" or "--developer-inspect-state")) return 2;
        string root = Path.GetFullPath(args[1]), source = Path.Combine(root, "worker", "vba");
        string output = Path.Combine(root, "worker", "native");
        Directory.CreateDirectory(output);
        string path = Path.Combine(output, "CorelMcp.Runtime.gms");
        if (args[0] == "--developer-prepare" && File.Exists(path)) throw new IOException("Существующий GMS автоматически не перезаписывается.");
        var files = Directory.GetFiles(source, "*.bas").Order(StringComparer.Ordinal).ToArray();
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string file in files) { digest.AppendData(Encoding.UTF8.GetBytes(Path.GetFileName(file) + "\n")); digest.AppendData(File.ReadAllBytes(file)); }
        string hash = Convert.ToHexStringLower(digest.GetHashAndReset());
        string name = "CorelMcp_Runtime_v1_" + hash[..12];
        using var mutex = new Mutex(false, @"Local\CorelDrawMcp_v27");
        if (!mutex.WaitOne(0)) throw new InvalidOperationException("COM занят.");
        try
        {
            using var connection = new CorelConnection(); connection.Connect("CorelDRAW.Application.27");
            using var scope = new ComScope(); var app = connection.GetApplication();
            if(args[0]=="--developer-inspect-state")
            {
                dynamic stateEditor=scope.Track((object)app.VBE);dynamic stateVbProjects=scope.Track((object)stateEditor.VBProjects);
                var stateProjects=scope.Track(scope.Track(app.GMSManager).Projects);var rows=new List<object>();
                for(int i=1;i<=stateProjects.Count;i++)
                {
                    var p=scope.Track(stateProjects[i]);bool? saved=null;string? savedError=null;
                    for(int j=1;j<=(int)stateVbProjects.Count;j++)
                    {
                        dynamic vb=scope.Track((object)stateVbProjects.Item(j));
                        if((string)vb.Name==p.Name)try{saved=(bool)vb.Saved;}catch(Exception e){savedError=e.Message;}
                    }
                    rows.Add(new {name=p.Name,path=p.FullFileName,dirty=p.Dirty,locked=p.Locked,vbe_saved=saved,vbe_saved_error=savedError});
                }
                Console.WriteLine(JsonSerializer.Serialize(new {documents=scope.Track(app.Documents).Count,projects=rows,read_only=true}));return 0;
            }
            if(args[0] is "--developer-unload-test-runtime" or "--developer-unload-local-runtime")
            {
                string testPath=Path.GetFullPath(Path.Combine(root,args[0]=="--developer-unload-test-runtime"
                    ?"worker/CorelMcp.Worker.Tests/bin/Release/net10.0-windows/native/CorelMcp.Runtime.gms"
                    :"worker/CorelMcp.Worker/bin/Release/net10.0-windows/native/CorelMcp.Runtime.gms"));
                var testProjects=scope.Track(scope.Track(app.GMSManager).Projects);
                for(int i=testProjects.Count;i>=1;i--)
                {
                    var own=scope.Track(testProjects[i]);
                    if(System.Text.RegularExpressions.Regex.IsMatch(own.Name,@"\ACorelMcp_Runtime_v1_[a-f0-9]{12}\z")
                        &&string.Equals(Path.GetFullPath(own.FullFileName),testPath,StringComparison.OrdinalIgnoreCase))
                    {
                        if(own.Dirty)throw new InvalidOperationException("Тестовый модуль изменён после загрузки; выгрузка не выполнена.");
                        own.Unload();Console.WriteLine("Выгружен собственный runtime только из фиксированного developer output.");
                    }
                }
                return 0;
            }
            if (args[0] == "--developer-discard-prepared")
            {
                if(File.Exists(Path.Combine(output,"module-manifest.json")))throw new InvalidOperationException("Зафиксированный модуль не является незавершённой сборкой.");
                using var provenance=JsonDocument.Parse(File.ReadAllText(Path.Combine(output,"build-provenance.json")));
                var p=provenance.RootElement;string preparedName=p.GetProperty("project_name").GetString()!;
                string preparedHash=p.GetProperty("source_sha256").GetString()!;
                if(preparedHash.Length!=64||preparedHash.Any(c=>!char.IsAsciiHexDigitLower(c))||preparedName!="CorelMcp_Runtime_v1_"+preparedHash[..12]
                    ||p.GetProperty("state").GetString()!="awaiting_developer_compile_and_save"
                    ||!string.Equals(Path.GetFullPath(p.GetProperty("file").GetString()!),path,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Собственная незавершённая сборка не подтверждена.");
                var projectsToDiscard=scope.Track(scope.Track(app.GMSManager).Projects);
                for(int i=projectsToDiscard.Count;i>=1;i--)
                {
                    var own=scope.Track(projectsToDiscard[i]);
                    if(own.Name==preparedName&&string.Equals(Path.GetFullPath(own.FullFileName),path,StringComparison.OrdinalIgnoreCase))own.Unload();
                }
                Console.WriteLine("Выгружена только собственная незавершённая сборка; файлы сохранены.");return 0;
            }
            if (args[0] == "--developer-seal")
            {
                var collection = scope.Track(scope.Track(app.GMSManager).Projects);
                VGCore.GMSProject? own = null;
                for (int i = 1; i <= collection.Count; i++)
                {
                    var p = scope.Track(collection[i]);
                    if (p.Name == name && string.Equals(Path.GetFullPath(p.FullFileName), path, StringComparison.OrdinalIgnoreCase)) own = p;
                }
                if (own is null || own.Dirty) throw new InvalidOperationException("Собственный сохранённый проект не подтверждён; сначала Compile/Save.");
                own.Unload();
                File.WriteAllText(Path.Combine(output, "module-manifest.json"), JsonSerializer.Serialize(new {
                    schema_version = 1, protocol = 1, module_version = "0.4.0", corel_major = 27,
                    source_sha256 = hash, project_name = name, file = "CorelMcp.Runtime.gms",
                    gms_sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))), operations = new[] { "handshake", "validate_fonts", "create_batch", "update_batch", "replace_literal", "set_text", "format_text", "duplicate", "convert_text", "extract_geometry" }
                }, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine("Собственный модуль сохранён, выгружен; manifest создан.");
                return 0;
            }
            dynamic editor = scope.Track((object)app.VBE);
            dynamic vbProjects = scope.Track((object)editor.VBProjects);
            for (int i = 1; i <= (int)vbProjects.Count; i++)
            {
                dynamic p = scope.Track((object)vbProjects.Item(i));
                if ((int)p.Mode != 2) throw new InvalidOperationException("VBA занят; сборка не начата.");
                if ((string)p.Name == name) throw new InvalidOperationException("Проект уже загружен.");
            }
            var projects = scope.Track(scope.Track(app.GMSManager).Projects);
            var project = scope.Track(projects.Create(name, false, path));
            dynamic? selected = null;
            for (int i = 1; i <= (int)vbProjects.Count; i++)
            {
                dynamic p = scope.Track((object)vbProjects.Item(i));
                if ((string)p.Name == name) selected = p;
            }
            if (selected is null) throw new InvalidOperationException("Собственный проект не найден.");
            dynamic components = scope.Track((object)selected.VBComponents);
            void Add(string moduleName, string text)
            {
                dynamic component = scope.Track((object)components.Add(1)); component.Name = moduleName;
                dynamic code = scope.Track((object)component.CodeModule);
                int count = (int)code.CountOfLines; if (count > 0) code.DeleteLines(1, count);
                code.AddFromString(text); component.Activate();
            }
            Add("M_BuildInfo", "Option Explicit\r\nPublic Const SourceHash As String = \"" + hash + "\"\r\n");
            foreach (string file in files) Add(Path.GetFileNameWithoutExtension(file), File.ReadAllText(file, Encoding.UTF8));
            editor.ActiveVBProject = selected;
            File.WriteAllText(Path.Combine(output, "build-provenance.json"), JsonSerializer.Serialize(new {
                source_sha256 = hash, project_name = name, file = path, prepared_at = DateTimeOffset.UtcNow,
                state = "awaiting_developer_compile_and_save", sources = files.Select(f => new { path = Path.GetRelativePath(root, f), sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(f))) })
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(JsonSerializer.Serialize(new { project_name = name, path, developer_compile_required = true }));
        }
        finally { mutex.ReleaseMutex(); }
        return 0;
    }
}
