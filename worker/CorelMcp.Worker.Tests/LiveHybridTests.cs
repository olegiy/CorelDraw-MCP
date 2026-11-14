using System.Runtime.ExceptionServices;
using CorelMcp.Worker.Hybrid;
using CorelMcp.Worker.Interop;
using Xunit;

namespace CorelMcp.Worker.Tests;

[Collection("CorelLive")]
public sealed class LiveHybridTests
{
    [LiveCorelFact]
    public void SealedNativeModuleLoadsWithoutVbeAndSurvivesAnotherClientDispose()
    {
        Exception? failure=null;
        var thread=new Thread(()=>
        {
            try
            {
                var repository=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../.."));
                var directory=Path.Combine(AppContext.BaseDirectory,"native");
                using var lease=ComOperationMutex.Acquire();
                using var connection=new CorelConnection(); connection.Connect("CorelDRAW.Application.27");
                using var scope=new ComScope();var app=connection.GetApplication();
                var documents=scope.Track(app.Documents);
                var before=new List<(string,bool)>();
                for(int i=1;i<=documents.Count;i++){var doc=scope.Track(documents[i]);before.Add((doc.FullFileName,doc.Dirty));}
                var projects=scope.Track(scope.Track(app.GMSManager).Projects);
                string name;
                using(var module=NativeModuleManifest.LoadAndVerify(directory))name=module.ProjectName;
                bool existed=false;
                for(int i=1;i<=projects.Count;i++) if(scope.Track(projects[i]).Name==name)existed=true;
                try
                {
                    using var first=new NativeVbaRuntime(directory);
                    using var second=new NativeVbaRuntime(directory);
                    var identity=first.EnsureReady(app,scope);
                    for(int i=0;i<20;i++)Assert.Equal(identity,second.EnsureReady(app,scope));
                    first.Dispose();
                    Assert.Equal(identity,second.EnsureReady(app,scope));
                    var after=new List<(string,bool)>();
                    for(int i=1;i<=documents.Count;i++){var doc=scope.Track(documents[i]);after.Add((doc.FullFileName,doc.Dirty));}
                    Assert.Equal(before,after);
                }
                finally
                {
                    if(!existed)for(int i=projects.Count;i>=1;i--)
                    {
                        var p=scope.Track(projects[i]);
                        if(p.Name==name&&string.Equals(Path.GetFullPath(p.FullFileName),Path.Combine(directory,"CorelMcp.Runtime.gms"),StringComparison.OrdinalIgnoreCase))p.Unload();
                    }
                }
            }catch(Exception error){failure=error;}
        });
        thread.SetApartmentState(ApartmentState.STA);thread.Start();
        // Do not turn a still-running native call into a completed test or release its mutex.
        thread.Join();
        if(failure is not null)ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
