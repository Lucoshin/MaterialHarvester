using System.Text;
using System.Text.Json;

namespace MaterialHarvester.Cli;

public interface IJobExecutor
{
    Task<JobResult> ExecuteAsync(
        JobRequest request,
        IJobEventSink events,
        CancellationToken cancellationToken);
}

public sealed class MissingDependencyException : Exception
{
    public MissingDependencyException(string message) : base(message) { }
}

public static class CliApplication
{
    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        IJobExecutor executor,
        CancellationToken stoppingToken = default)
    {
        ArgumentNullException.ThrowIfNull(executor);
        CliOptions? options = null;
        JobRequest? request = null;
        CancellationTokenSource? cancelWatcherStop = null;
        Task? cancelWatcher = null;

        try
        {
            options = CliOptions.Parse(args);
            string requestJson = await File.ReadAllTextAsync(options.RequestPath, stoppingToken);
            request = JsonSerializer.Deserialize<JobRequest>(requestJson, JsonDefaults.Options)
                ?? throw new ContractException("请求 JSON 不能为空。");
            RequestValidator.Validate(request, options.Command);

            var events = new JsonLineEventWriter(options.EventsPath);
            using var jobCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            cancelWatcherStop = new CancellationTokenSource();
            cancelWatcher = WatchCancelSignalAsync(
                options.CancelPath,
                jobCancellation,
                cancelWatcherStop.Token);

            events.Write(new JobEvent { Type = "stage", Stage = "started", Message = "任务已启动" });
            JobResult result = await executor.ExecuteAsync(request, events, jobCancellation.Token);
            await WriteResultAsync(options.ResultPath, result);
            return result.Status switch
            {
                "succeeded" => 0,
                "cancelled" => 130,
                _ => 4
            };
        }
        catch (OperationCanceledException)
        {
            JobResult result = FailureResult(request, "cancelled", null);
            if (options is not null) await WriteResultAsync(options.ResultPath, result);
            return 130;
        }
        catch (Exception error) when (error is ContractException or JsonException or IOException)
        {
            JobResult result = FailureResult(request, "failed", error.Message);
            if (options is not null) await WriteResultAsync(options.ResultPath, result);
            Console.Error.WriteLine(error.Message);
            return 2;
        }
        catch (MissingDependencyException error)
        {
            JobResult result = FailureResult(request, "failed", error.Message);
            if (options is not null) await WriteResultAsync(options.ResultPath, result);
            Console.Error.WriteLine(error.Message);
            return 3;
        }
        catch (Exception error)
        {
            JobResult result = FailureResult(request, "failed", error.Message);
            if (options is not null) await WriteResultAsync(options.ResultPath, result);
            Console.Error.WriteLine(error.Message);
            return 4;
        }
        finally
        {
            if (cancelWatcherStop is not null)
            {
                cancelWatcherStop.Cancel();
                if (cancelWatcher is not null)
                {
                    try { await cancelWatcher; } catch (OperationCanceledException) { }
                }
                cancelWatcherStop.Dispose();
            }
        }
    }

    private static async Task WatchCancelSignalAsync(
        string cancelPath,
        CancellationTokenSource jobCancellation,
        CancellationToken stoppingToken)
    {
        while (true)
        {
            stoppingToken.ThrowIfCancellationRequested();
            if (File.Exists(cancelPath))
            {
                jobCancellation.Cancel();
                return;
            }
            await Task.Delay(50, stoppingToken);
        }
    }

    private static JobResult FailureResult(JobRequest? request, string status, string? error) => new()
    {
        JobId = request?.JobId ?? "",
        Status = status,
        Error = error
    };

    private static async Task WriteResultAsync(string path, JobResult result)
    {
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string temporaryPath = fullPath + ".tmp";
        await File.WriteAllTextAsync(
            temporaryPath,
            JsonSerializer.Serialize(result, JsonDefaults.Options),
            new UTF8Encoding(false));
        File.Move(temporaryPath, fullPath, true);
    }
}
