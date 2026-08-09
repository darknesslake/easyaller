using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Easyaller.Core.Provisioning;
using Microsoft.Win32;

namespace Easyaller.App;

public sealed class FileProvisioningExecutionStateStore : IProvisioningExecutionStateStore
{
    private const string StateFileName = "provisioning-execution.json";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public PendingProvisioningExecution? ReadPending()
    {
        var path = GetStatePath();
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var pending = JsonSerializer.Deserialize<PendingProvisioningExecution>(File.ReadAllText(path), SerializerOptions);
            return IsValid(pending) ? pending : null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public void SavePending(PendingProvisioningExecution pending)
    {
        ArgumentNullException.ThrowIfNull(pending);
        if (!IsValid(pending))
        {
            throw new ArgumentException("Provisioning resume state is invalid.", nameof(pending));
        }

        var path = GetStatePath();
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, "." + StateFileName + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(pending, SerializerOptions) + "\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public void ClearPending(Guid executionId)
    {
        var pending = ReadPending();
        if (pending?.ExecutionId == executionId)
        {
            File.Delete(GetStatePath());
        }
    }

    private static string GetStatePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Easyaller",
        "state",
        StateFileName);

    private static bool IsValid(PendingProvisioningExecution? pending) =>
        pending is not null &&
        pending.ExecutionId != Guid.Empty &&
        pending.ProfileId != Guid.Empty &&
        pending.ProfileRevision > 0 &&
        !string.IsNullOrWhiteSpace(pending.ExpectedComputerName) &&
        pending.CreatedUtc != default;
}

public sealed class WindowsProvisioningSystemAdapter : IProvisioningSystemAdapter
{
    private const string VerifyNetworkAdapterScript = """
        $matches = @(Get-NetAdapter -IncludeHidden -ErrorAction Stop | Where-Object {
            $_.InterfaceGuid.Guid -eq $env:EASYALLER_NETWORK_ADAPTER_ID -or $_.Name -eq $env:EASYALLER_NETWORK_ADAPTER_ID
        })
        if ($matches.Count -ne 1 -or $matches[0].Status -eq 'Disabled') { exit 2 }
        """;

    private const string SetProxyScript = "Set-WinHttpProxy -ProxyServer $env:EASYALLER_PROXY_ADDRESS -ErrorAction Stop";
    private const string RenameComputerScript = "Rename-Computer -NewName $env:EASYALLER_COMPUTER_NAME -Force -ErrorAction Stop";
    private const string JoinDomainScript = """
        $password = [Console]::In.ReadToEnd()
        try {
            $securePassword = ConvertTo-SecureString -String $password -AsPlainText -Force
            $credential = [System.Management.Automation.PSCredential]::new($env:EASYALLER_DOMAIN_USER, $securePassword)
            Add-Computer -DomainName $env:EASYALLER_DOMAIN_NAME -Credential $credential -Force -ErrorAction Stop
        }
        finally {
            $password = $null
            $securePassword = $null
            $credential = $null
        }
        """;

    private const string VerifyDomainScript = "if (-not (Get-CimInstance Win32_ComputerSystem -ErrorAction Stop).PartOfDomain) { exit 2 }";

    public ProvisioningSystemOperationResult VerifyNetworkAdapter(string adapterId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adapterId);
        return RunPowerShell(VerifyNetworkAdapterScript, startInfo =>
            startInfo.Environment["EASYALLER_NETWORK_ADAPTER_ID"] = adapterId);
    }

    public ProvisioningSystemOperationResult SetWinHttpProxy(string proxyAddress)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(proxyAddress);
        var result = RunPowerShell(SetProxyScript, startInfo =>
            startInfo.Environment["EASYALLER_PROXY_ADDRESS"] = proxyAddress);
        return result.IsSuccess
            ? ProvisioningSystemOperationResult.Success()
            : result;
    }

    public ProvisioningSystemOperationResult RenameComputer(string computerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(computerName);
        var result = RunPowerShell(RenameComputerScript, startInfo =>
            startInfo.Environment["EASYALLER_COMPUTER_NAME"] = computerName);
        return result.IsSuccess
            ? ProvisioningSystemOperationResult.Success(requiresRestart: true)
            : result;
    }

    public ProvisioningSystemOperationResult JoinDomain(string domainName, RuntimeDomainCredential credential)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domainName);
        ArgumentNullException.ThrowIfNull(credential);
        if (!OperatingSystem.IsWindows())
        {
            return ProvisioningSystemOperationResult.Failure("execution.windows.required");
        }

        if (!IsAdministrator())
        {
            return ProvisioningSystemOperationResult.Failure("execution.administrator.required");
        }

        try
        {
            var result = RunPowerShell(
                JoinDomainScript,
                startInfo =>
                {
                    startInfo.Environment["EASYALLER_DOMAIN_NAME"] = domainName;
                    startInfo.Environment["EASYALLER_DOMAIN_USER"] = credential.UserName;
                },
                credential.GetPasswordForExecution());
            return result.IsSuccess
                ? ProvisioningSystemOperationResult.Success(requiresRestart: true)
                : result;
        }
        catch (ObjectDisposedException)
        {
            return ProvisioningSystemOperationResult.Failure("execution.domain.credential.disposed");
        }
    }

    public ProvisioningSystemOperationResult VerifyComputerName(string expectedComputerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedComputerName);
        return OperatingSystem.IsWindows() && string.Equals(Environment.MachineName, expectedComputerName, StringComparison.OrdinalIgnoreCase)
            ? ProvisioningSystemOperationResult.Success()
            : ProvisioningSystemOperationResult.Failure("execution.resume.computerName.unverified");
    }

    public ProvisioningSystemOperationResult VerifyDomainJoin() => RunPowerShell(VerifyDomainScript);

    private static ProvisioningSystemOperationResult RunPowerShell(
        string script,
        Action<ProcessStartInfo>? configure = null,
        ReadOnlySpan<char> standardInput = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return ProvisioningSystemOperationResult.Failure("execution.windows.required");
        }

        if (!IsAdministrator())
        {
            return ProvisioningSystemOperationResult.Failure("execution.administrator.required");
        }

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = !standardInput.IsEmpty,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                },
            };
            process.StartInfo.ArgumentList.Add("-NoLogo");
            process.StartInfo.ArgumentList.Add("-NoProfile");
            process.StartInfo.ArgumentList.Add("-NonInteractive");
            process.StartInfo.ArgumentList.Add("-Command");
            process.StartInfo.ArgumentList.Add(script);
            configure?.Invoke(process.StartInfo);

            if (!process.Start())
            {
                return ProvisioningSystemOperationResult.Failure("execution.powershell.start.failed");
            }

            if (!standardInput.IsEmpty)
            {
                process.StandardInput.Write(standardInput);
                process.StandardInput.Close();
            }

            process.WaitForExit();
            return process.ExitCode == 0
                ? ProvisioningSystemOperationResult.Success()
                : ProvisioningSystemOperationResult.Failure("execution.windows.operation.failed");
        }
        catch (Win32Exception)
        {
            return ProvisioningSystemOperationResult.Failure("execution.powershell.unavailable");
        }
        catch (InvalidOperationException)
        {
            return ProvisioningSystemOperationResult.Failure("execution.powershell.start.failed");
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}

public sealed class WindowsProvisioningResumeLauncher : IProvisioningResumeLauncher
{
    private const string RunOnceKeyPath = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";
    private const string RunOnceValueName = "!EasyallerProvisioningResume";

    public ProvisioningSystemOperationResult RegisterResume()
    {
        if (!OperatingSystem.IsWindows())
        {
            return ProvisioningSystemOperationResult.Failure("execution.windows.required");
        }

        if (!IsAdministrator())
        {
            return ProvisioningSystemOperationResult.Failure("execution.administrator.required");
        }

        if (!TryCreateResumeCommand(out var command))
        {
            return ProvisioningSystemOperationResult.Failure("execution.resume.command.unavailable");
        }

        if (command.Length > 260)
        {
            return ProvisioningSystemOperationResult.Failure("execution.resume.command.tooLong");
        }

        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(RunOnceKeyPath, writable: true)
                ?? throw new InvalidOperationException("Easyaller RunOnce key is unavailable.");
            key.SetValue(RunOnceValueName, command, RegistryValueKind.String);
            return ProvisioningSystemOperationResult.Success();
        }
        catch (UnauthorizedAccessException)
        {
            return ProvisioningSystemOperationResult.Failure("execution.administrator.required");
        }
        catch (IOException)
        {
            return ProvisioningSystemOperationResult.Failure("execution.resume.register.failed");
        }
    }

    private static bool TryCreateResumeCommand(out string command)
    {
        command = string.Empty;
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            return false;
        }

        if (string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var managedEntry = Environment.GetCommandLineArgs()
                .Skip(1)
                .FirstOrDefault(argument => argument.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && Path.IsPathFullyQualified(argument));
            if (string.IsNullOrWhiteSpace(managedEntry))
            {
                return false;
            }

            command = Quote(processPath) + " " + Quote(managedEntry) + " --resume-provisioning";
            return true;
        }

        command = Quote(processPath) + " --resume-provisioning";
        return true;
    }

    private static string Quote(string path) => "\"" + path.Replace("\"", string.Empty, StringComparison.Ordinal) + "\"";

    [SupportedOSPlatform("windows")]
    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}

public sealed class ProvisioningResumeCompletionService(
    ProvisioningExecutionService? executionService = null)
{
    public const string ResumeArgument = "--resume-provisioning";

    private readonly ProvisioningExecutionService _executionService = executionService ?? new ProvisioningExecutionService(
        new WindowsProvisioningSystemAdapter(),
        new FileProvisioningExecutionStateStore(),
        new WindowsProvisioningResumeLauncher());

    public ProvisioningExecutionResult? TryComplete(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return arguments.Contains(ResumeArgument, StringComparer.Ordinal)
            ? _executionService.Resume()
            : null;
    }
}
