namespace MaterialHarvester.Cli;

internal static class Program
{
    private static async Task<int> Main(string[] args) =>
        await CliApplication.RunAsync(args, new VideoJobExecutor());
}
