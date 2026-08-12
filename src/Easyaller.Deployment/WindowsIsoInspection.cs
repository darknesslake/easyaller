using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Easyaller.Core.Profiles;

namespace Easyaller.Deployment;

public enum IsoPayloadFormat
{
    Wim,
    Esd,
}

public sealed record IsoInstallImage(
    int Index,
    string Name,
    string EditionId,
    string Architecture,
    string? Version);

public sealed record IsoContentReport(
    IReadOnlyList<string> RelativePaths,
    IsoPayloadFormat? PayloadFormat,
    IReadOnlyList<IsoInstallImage> Images);

public sealed record IsoContentReadResult(
    IsoContentReport? Report,
    IReadOnlyList<DeploymentValidationError> Errors)
{
    public bool IsAvailable => Report is not null && Errors.Count == 0;
}

public sealed record IsoInspectionRequest(
    string IsoPath,
    WindowsDeploymentTarget Target,
    long MaximumSizeBytes = WindowsIsoInspectionService.DefaultMaximumSizeBytes);

public sealed record IsoInspectionResult(
    string IsoPath,
    long? SizeBytes,
    string? Sha256,
    IsoContentReport? Content,
    IReadOnlyList<DeploymentValidationError> Errors)
{
    public bool IsReadyForFutureUsbCreation => Content is not null && Errors.Count == 0;
}

public interface IIsoContentReader
{
    IsoContentReadResult Read(string isoPath);
}

public sealed class WindowsIsoInspectionService
{
    public const long DefaultMaximumSizeBytes = 12L * 1024 * 1024 * 1024;

    private static readonly string[] RequiredPaths =
    [
        "setup.exe",
        "sources/setup.exe",
        "sources/boot.wim",
    ];

    private readonly IIsoContentReader _contentReader;

    public WindowsIsoInspectionService(IIsoContentReader? contentReader = null)
    {
        _contentReader = contentReader ?? new WindowsIsoContentReader();
    }

    public IsoInspectionResult Inspect(IsoInspectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var errors = new List<DeploymentValidationError>();
        if (string.IsNullOrWhiteSpace(request.IsoPath) || !Path.IsPathFullyQualified(request.IsoPath))
        {
            errors.Add(Error("usb.iso.path.invalid", "isoPath", "ISO path must be an absolute local file path."));
            return Result(request.IsoPath, null, null, null, errors);
        }

        if (!string.Equals(Path.GetExtension(request.IsoPath), ".iso", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(Error("usb.iso.extension.invalid", "isoPath", "Only files with the .iso extension can be inspected."));
            return Result(request.IsoPath, null, null, null, errors);
        }

        if (request.MaximumSizeBytes < 1)
        {
            errors.Add(Error("usb.iso.size.limit.invalid", "maximumSizeBytes", "Maximum ISO size must be positive."));
            return Result(request.IsoPath, null, null, null, errors);
        }

        FileInfo file;
        try
        {
            file = new FileInfo(request.IsoPath);
            if (!file.Exists)
            {
                errors.Add(Error("usb.iso.file.missing", "isoPath", "ISO file does not exist."));
                return Result(request.IsoPath, null, null, null, errors);
            }
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            errors.Add(Error("usb.iso.path.invalid", "isoPath", exception.Message));
            return Result(request.IsoPath, null, null, null, errors);
        }

        if (file.Length < 1 || file.Length > request.MaximumSizeBytes)
        {
            errors.Add(Error("usb.iso.size.outOfRange", "isoPath", "ISO file size is outside the permitted range."));
            return Result(request.IsoPath, file.Length, null, null, errors);
        }

        string hash;
        try
        {
            using var stream = file.OpenRead();
            hash = Convert.ToHexString(SHA256.HashData(stream));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            errors.Add(Error("usb.iso.hash.failed", "isoPath", exception.Message));
            return Result(request.IsoPath, file.Length, null, null, errors);
        }

        var content = _contentReader.Read(file.FullName);
        errors.AddRange(content.Errors);
        if (content.Report is not null)
        {
            ValidateContent(content.Report, request.Target, errors);
        }

        return Result(file.FullName, file.Length, hash, content.Report, errors);
    }

    private static void ValidateContent(
        IsoContentReport content,
        WindowsDeploymentTarget target,
        ICollection<DeploymentValidationError> errors)
    {
        var paths = new HashSet<string>(content.RelativePaths, StringComparer.OrdinalIgnoreCase);
        foreach (var requiredPath in RequiredPaths)
        {
            if (!paths.Contains(requiredPath))
            {
                errors.Add(Error("usb.iso.structure.missing", "iso.paths", $"ISO is missing required path '{requiredPath}'."));
            }
        }

        var wim = paths.Contains("sources/install.wim");
        var esd = paths.Contains("sources/install.esd");
        if (wim == esd || content.PayloadFormat is null)
        {
            errors.Add(Error("usb.iso.installImage.invalid", "iso.paths", "ISO must contain exactly one supported install image: sources/install.wim or sources/install.esd."));
        }
        else if (content.PayloadFormat != (wim ? IsoPayloadFormat.Wim : IsoPayloadFormat.Esd))
        {
            errors.Add(Error("usb.iso.installImage.inconsistent", "iso.payloadFormat", "Mounted ISO report does not match the discovered install image."));
        }

        if (target.Architecture != WindowsArchitecture.Amd64)
        {
            errors.Add(Error("usb.iso.target.architecture.unsupported", "target.architecture", "Only amd64 deployment targets are supported."));
        }

        if (content.Images.Count == 0)
        {
            errors.Add(Error("usb.iso.images.missing", "iso.images", "ISO install image does not expose any Windows editions."));
            return;
        }

        foreach (var image in content.Images)
        {
            if (!string.Equals(image.Architecture, "amd64", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(Error("usb.iso.image.architecture.unsupported", "iso.images", $"Image {image.Index} is not amd64."));
            }
        }

        var requestedEdition = target.Edition switch
        {
            WindowsEdition.Professional => "Professional",
            WindowsEdition.Enterprise => "Enterprise",
            _ => string.Empty,
        };
        if (!content.Images.Any(image =>
                string.Equals(image.EditionId, requestedEdition, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(image.Architecture, "amd64", StringComparison.OrdinalIgnoreCase)))
        {
            errors.Add(Error("usb.iso.target.edition.missing", "target.edition", "ISO does not contain the selected amd64 Windows edition."));
        }
    }

    private static IsoInspectionResult Result(
        string isoPath,
        long? sizeBytes,
        string? hash,
        IsoContentReport? content,
        IReadOnlyList<DeploymentValidationError> errors) =>
        new(isoPath, sizeBytes, hash, content, errors);

    private static DeploymentValidationError Error(string code, string fieldPath, string message) => new(code, fieldPath, message);
}

public sealed class WindowsIsoContentReader : IIsoContentReader
{
    private const string ProbeScript = "& { $ErrorActionPreference='Stop'; $isoPath=$env:EASYALLER_ISO_PATH; if([string]::IsNullOrWhiteSpace($isoPath)){throw 'Easyaller ISO path was not supplied.'}; $mounted=$false; try { $image=Mount-DiskImage -ImagePath $isoPath -Access ReadOnly -PassThru -ErrorAction Stop; $mounted=$true; $volume=$image | Get-Volume | Where-Object { $null -ne $_.DriveLetter } | Select-Object -First 1; if($null -eq $volume){throw 'Mounted ISO has no drive letter.'}; $root=([string]$volume.DriveLetter)+':\\'; $relativePaths=@('setup.exe','sources/setup.exe','sources/boot.wim','sources/install.wim','sources/install.esd') | Where-Object { Test-Path -LiteralPath (Join-Path $root $_) -PathType Leaf }; $wim=Join-Path $root 'sources/install.wim'; $esd=Join-Path $root 'sources/install.esd'; $install=if(Test-Path -LiteralPath $wim -PathType Leaf){$wim}elseif(Test-Path -LiteralPath $esd -PathType Leaf){$esd}else{$null}; $images=@(); if($null -ne $install){ $images=@(Get-WindowsImage -ImagePath $install -ErrorAction Stop | ForEach-Object { $architecture=[string]$_.Architecture; if($architecture -eq '9'){$architecture='amd64'}; [pscustomobject]@{ Index=[int]$_.ImageIndex; Name=[string]$_.ImageName; EditionId=[string]$_.EditionId; Architecture=$architecture; Version=[string]$_.Version } }) }; [pscustomobject]@{ RelativePaths=$relativePaths; PayloadFormat=if(Test-Path -LiteralPath $wim -PathType Leaf){'Wim'}elseif(Test-Path -LiteralPath $esd -PathType Leaf){'Esd'}else{$null}; Images=$images } | ConvertTo-Json -Compress -Depth 4 } finally { if($mounted){ Dismount-DiskImage -ImagePath $isoPath -ErrorAction Stop } } }";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public IsoContentReadResult Read(string isoPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Failure("usb.iso.platform.unsupported", "ISO inspection is available only on Windows.");
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
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                },
            };
            process.StartInfo.Environment["EASYALLER_ISO_PATH"] = isoPath;
            process.StartInfo.ArgumentList.Add("-NoLogo");
            process.StartInfo.ArgumentList.Add("-NoProfile");
            process.StartInfo.ArgumentList.Add("-NonInteractive");
            process.StartInfo.ArgumentList.Add("-Command");
            process.StartInfo.ArgumentList.Add(ProbeScript);
            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                // Get-WindowsImage (DISM) fails this way when the process is not elevated, even
                // though the read-only Mount-DiskImage step right before it does not need elevation.
                if (!string.IsNullOrWhiteSpace(error) && error.Contains("requires elevation", StringComparison.OrdinalIgnoreCase))
                {
                    return Failure(
                        "usb.iso.read.administrator.required",
                        "Reading Windows image contents from the ISO requires administrator rights. Restart Easyaller as an administrator and try again.");
                }

                return Failure("usb.iso.read.failed", string.IsNullOrWhiteSpace(error) ? "Windows ISO inspection failed." : error.Trim());
            }

            var probe = JsonSerializer.Deserialize<IsoProbe>(output, JsonOptions);
            if (probe is null)
            {
                return Failure("usb.iso.read.failed", "Windows ISO inspection produced no report.");
            }

            return new IsoContentReadResult(probe.ToContentReport(), []);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or JsonException)
        {
            return Failure("usb.iso.read.failed", exception.Message);
        }
    }

    private static IsoContentReadResult Failure(string code, string message) =>
        new(null, [new DeploymentValidationError(code, "isoInspection", message)]);

    private sealed record IsoProbe(
        IReadOnlyList<string>? RelativePaths,
        string? PayloadFormat,
        IReadOnlyList<IsoImageProbe>? Images)
    {
        public IsoContentReport ToContentReport() => new(
            RelativePaths ?? [],
            PayloadFormat switch
            {
                "Wim" => IsoPayloadFormat.Wim,
                "Esd" => IsoPayloadFormat.Esd,
                _ => null,
            },
            (Images ?? []).Select(image => new IsoInstallImage(
                image.Index,
                image.Name ?? string.Empty,
                image.EditionId ?? string.Empty,
                image.Architecture ?? string.Empty,
                image.Version)).ToArray());
    }

    private sealed record IsoImageProbe(int Index, string? Name, string? EditionId, string? Architecture, string? Version);
}
