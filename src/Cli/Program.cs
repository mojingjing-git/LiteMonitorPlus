using System.Text.Json;
using LiteMonitor.src.Plugins;

namespace LiteMonitor.Cli;

/// <summary>
/// CLI 能力入口（可行性验证版）。
///
/// 输出契约（Agent 友好）：
/// - **stdout**：机器可读 JSON（每次调用恰好一个 JSON 对象）。
/// - **stderr**：人类可读的一行摘要。
/// - **退出码**：语义化，见 <see cref="ExitCodes"/>。
///
/// 退出码契约（与仓库既有约定同族）：
/// - 0 = 成功 / 校验通过
/// - 1 = 校验失败（命令正常执行，发现问题）
/// - 2 = 环境不具备（文件不存在、不可读等）
/// - 64 = 参数错误
/// - 70 = 未处理异常
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (Exception ex)
        {
            WriteJson(new
            {
                ok = false,
                command = "unknown",
                error_code = "unhandled_exception",
                message = ex.Message
            });
            Console.Error.WriteLine($"未处理异常：{ex.Message}");
            return ExitCodes.UnhandledException;
        }
    }

    private static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            return Usage("缺少命令。可用命令：theme-validate");
        }

        string command = args[0];
        switch (command)
        {
            case "theme-validate":
                return RunThemeValidate(args);
            case "help":
            case "--help":
            case "-h":
                Console.Error.WriteLine("用法：LiteMonitor.Cli theme-validate --path=<file> [--route=gpu|legacy] [--material=liquid_glass|cyber_punk|legacy] [--declared-size=<bytes>]");
                return ExitCodes.Success;
            default:
                return Usage($"未知命令 '{command}'。可用命令：theme-validate");
        }
    }

    /// <summary>
    /// 主题内容校验。直接复用客户端同一份 <see cref="ThemeMarketValidator"/>，
    /// 保证「客户端判定」与「仓库/Agent 判定」逐条同判据。
    /// </summary>
    private static int RunThemeValidate(string[] args)
    {
        string? path = null;
        string route = "gpu";
        string material = "liquid_glass";
        int? declaredSize = null;

        for (int i = 1; i < args.Length; i++)
        {
            string argument = args[i];
            if (argument.StartsWith("--path=", StringComparison.Ordinal))
            {
                path = argument["--path=".Length..];
            }
            else if (argument.StartsWith("--route=", StringComparison.Ordinal))
            {
                route = argument["--route=".Length..];
            }
            else if (argument.StartsWith("--material=", StringComparison.Ordinal))
            {
                material = argument["--material=".Length..];
            }
            else if (argument.StartsWith("--declared-size=", StringComparison.Ordinal))
            {
                if (!int.TryParse(argument["--declared-size=".Length..], out int parsed))
                {
                    return Usage($"--declared-size 需要整数，收到 '{argument}'");
                }
                declaredSize = parsed;
            }
            else
            {
                return Usage($"未知参数 '{argument}'");
            }
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return Usage("缺少 --path=<file>");
        }

        if (!File.Exists(path))
        {
            WriteJson(new
            {
                ok = false,
                command = "theme-validate",
                path,
                error_code = "file_not_found",
                message = "主题文件不存在"
            });
            Console.Error.WriteLine($"主题文件不存在：{path}");
            return ExitCodes.EnvironmentUnavailable;
        }

        ThemeMarketValidationResult result = ThemeMarketValidator.ValidateFile(path, route, material, declaredSize);

        WriteJson(new
        {
            ok = result.IsValid,
            command = "theme-validate",
            path,
            route,
            material,
            error_code = result.ErrorCode,
            message = result.Message
        });

        Console.Error.WriteLine(result.IsValid
            ? $"主题校验通过：{path}"
            : $"主题校验失败（{result.ErrorCode}）：{path}");

        return result.IsValid ? ExitCodes.Success : ExitCodes.ValidationFailed;
    }

    private static int Usage(string message)
    {
        WriteJson(new
        {
            ok = false,
            command = "usage",
            error_code = "invalid_arguments",
            message
        });
        Console.Error.WriteLine(message);
        return ExitCodes.InvalidArguments;
    }

    private static void WriteJson(object payload)
    {
        Console.Out.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = false,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }));
    }
}

internal static class ExitCodes
{
    public const int Success = 0;
    public const int ValidationFailed = 1;
    public const int EnvironmentUnavailable = 2;
    public const int InvalidArguments = 64;
    public const int UnhandledException = 70;
}
