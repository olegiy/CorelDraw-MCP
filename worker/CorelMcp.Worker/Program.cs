using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using CorelMcp.Worker.Interop;

namespace CorelMcp.Worker;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        if (args.Length == 0) return RunProtocol().GetAwaiter().GetResult();
        if (args.Length != 3 || args[0] != "--live-smoke" || args[1] != "--run-directory"
            || !Path.IsPathFullyQualified(args[2]) || Environment.GetEnvironmentVariable("CORELDRAW_WORKER_LIVE") != "1")
        {
            Console.Error.WriteLine("Для live smoke нужны CORELDRAW_WORKER_LIVE=1 и --live-smoke --run-directory <абсолютный путь>; обычный JSONL-режим запускается без аргументов.");
            return 2;
        }
        var directory = args[2];
        Directory.CreateDirectory(directory);
        try
        {
            using var lease = ComOperationMutex.Acquire();
            using var context = new ApplicationContext();
            using var control = new Control();
            _ = control.Handle;
            LiveSmoke.Report? report = null;
            control.BeginInvoke(() =>
            {
                try { report = LiveSmoke.Run(directory); }
                catch (Exception error) { report = new LiveSmoke.Report { error = error.ToString() }; }
                finally { context.ExitThread(); }
            });
            System.Windows.Forms.Application.Run(context);
            File.WriteAllText(Path.Combine(directory, "smoke.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            return report?.success == true ? 0 : 1;
        }
        catch (ComMutexException error) { Console.Error.WriteLine(error.Code + ": " + error.Message); return 3; }
    }

    private static async Task<int> RunProtocol()
    {
        using var output = Console.OpenStandardOutput();
        using var input = Console.OpenStandardInput();
        var host = new StaHost(() => new OperationDispatcher());
        try
        {
            await host.Ready.ConfigureAwait(false);
            Protocol.WriteRecord(output, new { type = "ready", protocol_version = 1,
                worker_version = typeof(Program).Assembly.GetName().Version?.ToString(3),
                runtime = RuntimeInformation.FrameworkDescription,
                architecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant() });
            var reading = ReadInput(input, output, host);
            await Task.WhenAny(reading, host.Completion).ConfigureAwait(false);
            if (!reading.IsCompleted) { await host.Completion.ConfigureAwait(false); return 0; }
            var exitCode = await reading.ConfigureAwait(false);
            await host.Completion.ConfigureAwait(false);
            return exitCode;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("WORKER_STOPPED: исполнитель завершился с ошибкой; результат незавершённой операции требует проверки. " + error.GetType().Name);
            host.CompleteInput();
            try { await host.Completion.ConfigureAwait(false); } catch { }
            return 2;
        }
    }

    private static async Task<int> ReadInput(Stream input, Stream output, StaHost host)
    {
        try
        {
            await foreach (var request in Protocol.ReadRequestsAsync(input).ConfigureAwait(false))
            {
                if (!host.TrySubmit(request,
                    stage => Protocol.WriteRecord(output, new { type = "progress", job_id = request.JobId, stage }),
                    result => Protocol.WriteRecord(output, new { type = "result", job_id = request.JobId, data = result })))
                {
                    if (host.ActiveJobId == request.JobId)
                        throw new WorkerProtocolException("PROTOCOL_DUPLICATE_JOB", "Повтор активного job_id отклонён; исходная операция не считается завершённой.");
                    Protocol.WriteRecord(output, new { type = "result", job_id = request.JobId,
                        data = new WorkerResult(false, Error: new WorkerError("BUSY", "Исполнитель уже занят; скрытая очередь запрещена.", "waiting_for_mutex", false)) });
                }
            }
            return 0;
        }
        catch (WorkerProtocolException error) { Console.Error.WriteLine(error.Code + ": " + error.Message); return 2; }
        catch (IOException) { Console.Error.WriteLine("PROTOCOL_IO_ERROR: потерян канал протокола."); return 2; }
        finally { host.CompleteInput(); }
    }
}
