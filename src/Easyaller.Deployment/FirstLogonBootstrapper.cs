using System.Text;
using Easyaller.Core.Profiles;

namespace Easyaller.Deployment;

public sealed record FirstLogonBootstrapPlan(
    string ScriptPackageRelativePath,
    string ScriptWindowsPath,
    string CommandLine);

public static class FirstLogonBootstrapper
{
    public const string ScriptPackageRelativePath = ConfigurationSetPayloadLayout.RootRelativePath + "/scripts/Start-EasyallerBootstrap.ps1";
    public const string ScriptWindowsPath = @"C:\ProgramData\Easyaller\scripts\Start-EasyallerBootstrap.ps1";
    public const string RequiredApplicationPackageRelativePath = ConfigurationSetPayloadLayout.RootRelativePath + "/payload/Easyaller.App.exe";
    public const string RunOnceValueName = "!EasyallerBootstrapResume";

    public static FirstLogonBootstrapPlan? CreatePlan(DeploymentPreparationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.EnableFirstLogonBootstrap
            ? new FirstLogonBootstrapPlan(ScriptPackageRelativePath, ScriptWindowsPath, CreateCommandLine())
            : null;
    }

    public static byte[] CreateScript(FirstLogonBootstrapPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!string.Equals(plan.ScriptPackageRelativePath, ScriptPackageRelativePath, StringComparison.Ordinal) ||
            !string.Equals(plan.ScriptWindowsPath, ScriptWindowsPath, StringComparison.Ordinal) ||
            !string.Equals(plan.CommandLine, CreateCommandLine(), StringComparison.Ordinal))
        {
            throw new ArgumentException("First-logon bootstrap plan is not supported.", nameof(plan));
        }

        return Encoding.UTF8.GetBytes("""
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('InitialBootstrap', 'ResumeBootstrap')]
    [string]$Mode
)

$ErrorActionPreference = 'Stop'
$payloadRoot = Split-Path -Parent $PSScriptRoot
$manifestPath = Join-Path $payloadRoot 'payload-manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw 'Easyaller payload manifest is missing.'
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.formatVersion -ne 1) {
    throw 'Easyaller payload manifest format is unsupported.'
}

$applicationRelativePath = 'payload/Easyaller.App.exe'
$applicationEntryFound = $false
foreach ($entry in @($manifest.files)) {
    $relativePath = [string]$entry.relativePath
    if ([string]::IsNullOrWhiteSpace($relativePath) -or $relativePath -match ':' -or $relativePath.Split([char[]]'\/') | Where-Object { $_ -eq '' -or $_ -eq '.' -or $_ -eq '..' }) {
        throw 'Easyaller payload manifest contains an unsafe path.'
    }

    $filePath = Join-Path $payloadRoot $relativePath
    if (-not (Test-Path -LiteralPath $filePath -PathType Leaf)) {
        throw 'Easyaller payload file is missing.'
    }

    $actualHash = (Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash
    if ($actualHash -ne ([string]$entry.sha256).ToUpperInvariant()) {
        throw 'Easyaller payload hash verification failed.'
    }

    if ($relativePath -eq $applicationRelativePath) {
        $applicationEntryFound = $true
    }
}

$applicationPath = Join-Path $payloadRoot $applicationRelativePath
if (-not (Test-Path -LiteralPath $applicationPath -PathType Leaf)) {
    throw 'Easyaller application payload is missing.'
}

if (-not $applicationEntryFound) {
    throw 'Easyaller application payload is not covered by the manifest.'
}

$runOncePath = 'HKLM:\Software\Microsoft\Windows\CurrentVersion\RunOnce'
$runOnceName = '!EasyallerBootstrapResume'
if ($Mode -eq 'InitialBootstrap') {
    $resumeCommand = 'powershell.exe -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "C:\ProgramData\Easyaller\scripts\Start-EasyallerBootstrap.ps1" -Mode ResumeBootstrap'
    New-ItemProperty -Path $runOncePath -Name $runOnceName -Value $resumeCommand -PropertyType String -Force | Out-Null
}

Start-Process -FilePath $applicationPath -ArgumentList '--resume'
""");
    }

    public static string CreateCommandLine() =>
        "powershell.exe -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"C:\\ProgramData\\Easyaller\\scripts\\Start-EasyallerBootstrap.ps1\" -Mode InitialBootstrap";

    public static void Validate(DeploymentPreparationRequest request, ICollection<DeploymentValidationError> errors)
    {
        if (!request.EnableFirstLogonBootstrap)
        {
            return;
        }

        if (request.Profile.Deployment.LaunchMode != ProvisionerLaunchMode.FirstLogon)
        {
            errors.Add(new DeploymentValidationError(
                "deployment.firstLogon.launchMode.invalid",
                "deployment.launchMode",
                "First-logon bootstrap requires the FirstLogon launch mode."));
        }

        if (request.TemporaryLocalAccount is null || request.TemporaryLocalAccount.IsDisposed ||
            !string.Equals(request.TemporaryLocalAccount.AccountName, TemporaryLocalAccountCredentialFactory.DefaultAccountName, StringComparison.Ordinal))
        {
            errors.Add(new DeploymentValidationError(
                "deployment.firstLogon.account.invalid",
                "temporaryLocalAccount",
                "First-logon bootstrap requires the active ProvisioningAdmin temporary account."));
        }
    }
}
