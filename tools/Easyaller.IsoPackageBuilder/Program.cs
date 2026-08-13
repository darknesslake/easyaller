using Easyaller.Core.Profiles;
using Easyaller.Deployment;

if (args.Length != 4)
{
    Console.Error.WriteLine("Usage: <profile.json> <published-app-directory> <package-directory> <password-output-file>");
    return 2;
}

var profilePath = Path.GetFullPath(args[0]);
var applicationDirectory = Path.GetFullPath(args[1]);
var packageDirectory = Path.GetFullPath(args[2]);
var passwordOutputPath = Path.GetFullPath(args[3]);

var profileRead = new ProfileJsonSerializer().Read(await File.ReadAllBytesAsync(profilePath));
if (!profileRead.IsValid || profileRead.Profile is null)
{
    foreach (var error in profileRead.Errors)
    {
        Console.Error.WriteLine($"{error.FieldPath}: {error.Message}");
    }

    return 3;
}

if (!Directory.Exists(applicationDirectory))
{
    Console.Error.WriteLine("Published application directory does not exist.");
    return 4;
}

using var temporaryAccount = new TemporaryLocalAccountCredentialFactory().Create();
var preparationRequest = new DeploymentPreparationRequest(
    profileRead.Profile,
    new WindowsDeploymentTarget(WindowsEdition.Professional, WindowsArchitecture.Amd64, "25H2", 26200),
    temporaryAccount.Credential,
    EnableFirstLogonBootstrap: true);
var dryRunResult = new DeploymentDryRunService().CreateDryRun(preparationRequest);
if (!dryRunResult.IsValid || dryRunResult.DryRun is null)
{
    foreach (var error in dryRunResult.Errors)
    {
        Console.Error.WriteLine($"{error.FieldPath}: {error.Message}");
    }

    return 5;
}

var assets = Directory.EnumerateFiles(applicationDirectory, "*", SearchOption.AllDirectories)
    .Select(path => new DeploymentPackageAsset(
        DeploymentPackageAssetKind.LocalPayload,
        path,
        ConfigurationSetPayloadLayout.RootRelativePath + "/payload/" + Path.GetRelativePath(applicationDirectory, path).Replace('\\', '/')))
    .ToList();
assets.Add(new DeploymentPackageAsset(
    DeploymentPackageAssetKind.LocalPayload,
    profilePath,
    ConfigurationSetPayloadLayout.RootRelativePath + "/payload/selected-profile.wpprofile.json"));

var export = await new DeploymentPackageExporter().ExportAsync(
    new DeploymentPackageExportRequest(dryRunResult.DryRun, packageDirectory, assets));
if (!export.IsSuccess)
{
    foreach (var error in export.Errors)
    {
        Console.Error.WriteLine($"{error.FieldPath}: {error.Message}");
    }

    return 6;
}

var password = temporaryAccount.RevealPasswordOnce();
await File.WriteAllTextAsync(
    passwordOutputPath,
    $"Easyaller Windows installation temporary account{Environment.NewLine}" +
    $"Username: {TemporaryLocalAccountCredentialFactory.DefaultAccountName}{Environment.NewLine}" +
    $"Password: {password}{Environment.NewLine}" +
    "This password is not stored in the Easyaller profile. Delete this file after the installation is verified." + Environment.NewLine);

Console.WriteLine($"Package={export.DestinationDirectory}");
Console.WriteLine($"Files={export.Manifest?.Files.Count ?? 0}");
Console.WriteLine($"PasswordFile={passwordOutputPath}");
return 0;
