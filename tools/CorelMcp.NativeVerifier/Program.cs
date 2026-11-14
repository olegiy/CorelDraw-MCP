using System.Text.Json;
using CorelMcp.Worker.Hybrid;
using CorelMcp.Worker.Interop;

internal static class Program
{
    [STAThread] private static int Main(string[] args)
    {
        if(args.Length!=2||!Path.IsPathFullyQualified(args[0])||!Path.IsPathFullyQualified(args[1]))return 2;
        var path=Path.GetFullPath(args[0]);var directory=Path.GetDirectoryName(path)!;
        var output=Path.GetFullPath(args[1]);Directory.CreateDirectory(output);
        var report=new Dictionary<string,object?>{["success"]=false,["runtime_vbe_calls"]=0,["module_path"]=path};
        try
        {
            using var module=NativeModuleManifest.LoadAndVerify(directory);
            if(path!=module.FullPath)throw new InvalidDataException("Путь не совпал с manifest.");
            using var lease=ComOperationMutex.Acquire();
            using var connection=new CorelConnection();connection.Connect("CorelDRAW.Application.27");
            using var scope=new ComScope();var app=connection.GetApplication();var documents=scope.Track(app.Documents);
            var before=new List<(string,bool)>();for(int i=1;i<=documents.Count;i++){var doc=scope.Track(documents[i]);before.Add((doc.FullFileName,doc.Dirty));}
            var projects=scope.Track(scope.Track(app.GMSManager).Projects);bool existed=false;
            for(int i=1;i<=projects.Count;i++){var project=scope.Track(projects[i]);if(project.Name==module.ProjectName&&project.FullFileName==path)existed=true;}
            try
            {
                using var runtime=new NativeVbaRuntime(directory);
                NativeModuleIdentity? identity=null;for(int i=0;i<20;i++)identity=runtime.EnsureReady(app,scope);
                var after=new List<(string,bool)>();for(int i=1;i<=documents.Count;i++){var doc=scope.Track(documents[i]);after.Add((doc.FullFileName,doc.Dirty));}
                if(!before.SequenceEqual(after))throw new InvalidOperationException("Состояние исходных документов изменилось.");
                report["identity"]=identity;report["handshakes"]=20;report["documents_unchanged"]=true;report["success"]=true;
            }
            finally
            {
                if(!existed)for(int i=projects.Count;i>=1;i--)
                {
                    var p=scope.Track(projects[i]);if(p.Name==module.ProjectName&&string.Equals(Path.GetFullPath(p.FullFileName),path,StringComparison.OrdinalIgnoreCase))p.Unload();
                }
            }
        }
        catch(Exception error){report["success"]=false;report["error"]=error.Message;}
        File.WriteAllText(Path.Combine(output,"native-verification.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine(JsonSerializer.Serialize(report));return (bool)report["success"]!?0:1;
    }
}
