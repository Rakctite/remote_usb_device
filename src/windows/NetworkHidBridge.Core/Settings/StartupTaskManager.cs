using System.Diagnostics;

namespace NetworkHidBridge.Core.Settings;

public sealed record StartupTaskResult(bool Success, string Message);

public sealed class StartupTaskManager
{
    public const string TaskName = "IoTSys Network HID Bridge";

    public static IReadOnlyList<string> BuildCreateArguments(string executablePath, string userName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        return
        [
            "/Create", "/TN", TaskName,
            "/TR", $"\"{executablePath}\"",
            "/SC", "ONLOGON",
            "/RL", "HIGHEST",
            "/RU", userName,
            "/F",
        ];
    }

    public static IReadOnlyList<string> BuildDeleteArguments() =>
        ["/Delete", "/TN", TaskName, "/F"];

    public static IReadOnlyList<string> BuildQueryArguments() =>
        ["/Query", "/TN", TaskName];

    public async Task<StartupTaskResult> SetEnabledAsync(
        bool enabled,
        string executablePath,
        CancellationToken cancellationToken = default)
    {
        string user = $"{Environment.UserDomainName}\\{Environment.UserName}";
        IReadOnlyList<string> arguments = enabled
            ? BuildCreateArguments(executablePath, user)
            : BuildDeleteArguments();
        StartupTaskResult operation = await RunAsync(arguments, cancellationToken).ConfigureAwait(false);
        if (enabled && !operation.Success)
        {
            return operation;
        }

        StartupTaskResult query = await RunAsync(BuildQueryArguments(), cancellationToken).ConfigureAwait(false);
        if (enabled)
        {
            return query.Success
                ? new(true, "자동 시작 작업을 만들고 확인했습니다.")
                : new(false, $"자동 시작 작업 확인 실패: {query.Message}");
        }

        return query.Success
            ? new(false, "자동 시작 작업이 삭제 후에도 남아 있습니다.")
            : new(true, "자동 시작 작업이 없음을 확인했습니다.");
    }

    public async Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default)
    {
        StartupTaskResult result = await RunAsync(
            BuildQueryArguments(), cancellationToken).ConfigureAwait(false);
        return result.Success;
    }

    private static async Task<StartupTaskResult> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("schtasks.exe")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            return new(false, "작업 스케줄러를 시작하지 못했습니다.");
        }
        string output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        string error = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        string message = string.IsNullOrWhiteSpace(error) ? output.Trim() : error.Trim();
        return new(process.ExitCode == 0, message);
    }
}
