namespace MaterialHarvester.Cli;

public sealed class CliOptions
{
    private static readonly string[] RequiredFlags = ["--request", "--events", "--result", "--cancel"];

    public string Command { get; private init; } = "";
    public string RequestPath { get; private init; } = "";
    public string EventsPath { get; private init; } = "";
    public string ResultPath { get; private init; } = "";
    public string CancelPath { get; private init; } = "";

    public static CliOptions Parse(IReadOnlyList<string> args)
    {
        if (args.Count != 9 || args[0] is not ("split" or "compose"))
        {
            throw new ContractException(
                "用法：MaterialHarvester.Cli <split|compose> --request <path> --events <path> --result <path> --cancel <path>");
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int index = 1; index < args.Count; index += 2)
        {
            string flag = args[index];
            string value = args[index + 1];
            if (!RequiredFlags.Contains(flag, StringComparer.Ordinal) || values.ContainsKey(flag))
                throw new ContractException($"未知或重复参数：{flag}");
            if (string.IsNullOrWhiteSpace(value))
                throw new ContractException($"参数 {flag} 的路径不能为空。");
            values.Add(flag, value);
        }

        if (RequiredFlags.Any(flag => !values.ContainsKey(flag)))
            throw new ContractException("CLI 参数不完整。");

        return new CliOptions
        {
            Command = args[0],
            RequestPath = values["--request"],
            EventsPath = values["--events"],
            ResultPath = values["--result"],
            CancelPath = values["--cancel"]
        };
    }
}
