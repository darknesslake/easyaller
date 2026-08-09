using System.Text.Json;
using Easyaller.Core.Profiles;

namespace Easyaller.Deployment;

public sealed class DeploymentProfileValidator(
    ProvisioningProfileValidator? profileValidator = null,
    IDeploymentCompatibilityValidator? compatibilityValidator = null) : IDeploymentProfileValidator
{
    private static readonly HashSet<string> ForbiddenCredentialFields = new(StringComparer.Ordinal)
    {
        "password", "passwd", "credential", "credentials", "token", "accesstoken",
        "refreshtoken", "apitoken", "apikey", "secret", "clientsecret", "secretkey", "privatekey",
    };

    private readonly ProvisioningProfileValidator _profileValidator = profileValidator ?? new ProvisioningProfileValidator();
    private readonly IDeploymentCompatibilityValidator _compatibilityValidator = compatibilityValidator ?? new CatalogDeploymentCompatibilityValidator();

    public DeploymentProfileValidationResult Validate(DeploymentPreparationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Profile);
        ArgumentNullException.ThrowIfNull(request.Target);

        var errors = _profileValidator.Validate(request.Profile).Errors
            .Select(static error => new DeploymentValidationError(error.Code, error.FieldPath, error.Message))
            .ToList();

        ValidatePrivacyPreferences(request.Profile.Windows.Privacy, errors);
        ValidateDeploymentBoundary(request.Profile, errors);

        var compatibility = _compatibilityValidator.Validate(request.Target, request.Profile);
        errors.AddRange(compatibility.Errors);

        return new DeploymentProfileValidationResult(errors, compatibility.Warnings, compatibility.State);
    }

    public DeploymentProfileValidationResult ValidateSerializedProfile(
        ReadOnlyMemory<byte> profileJson,
        WindowsDeploymentTarget target,
        ProfileJsonSerializer? serializer = null)
    {
        ArgumentNullException.ThrowIfNull(target);

        var credentialErrors = ScanForbiddenCredentialFields(profileJson);
        if (credentialErrors.Count > 0)
        {
            return new DeploymentProfileValidationResult(
                credentialErrors,
                [],
                DeploymentCompatibilityState.Unsupported);
        }

        var parsed = (serializer ?? new ProfileJsonSerializer()).Read(profileJson.Span);
        if (!parsed.IsValid)
        {
            return new DeploymentProfileValidationResult(
                parsed.Errors.Select(static error => new DeploymentValidationError(error.Code, error.FieldPath, error.Message)).ToArray(),
                [],
                DeploymentCompatibilityState.Unsupported);
        }

        return Validate(new DeploymentPreparationRequest(parsed.Profile!, target));
    }

    private static void ValidateDeploymentBoundary(
        ProvisioningProfile profile,
        ICollection<DeploymentValidationError> errors)
    {
        if (profile.Domain.Mode == DomainMode.Required)
        {
            errors.Add(new DeploymentValidationError(
                "deployment.profile.domainJoin.required.forbidden",
                "domain.mode",
                "A deployment profile cannot require a domain join. Collect domain settings and credentials at runtime."));
        }
    }

    private static void ValidatePrivacyPreferences(
        PrivacySettings privacy,
        ICollection<DeploymentValidationError> errors)
    {
        var preferences = new (string FieldPath, PrivacyPreference Value)[]
        {
            ("windows.privacy.locationServices", privacy.LocationServices),
            ("windows.privacy.advertisingId", privacy.AdvertisingId),
            ("windows.privacy.diagnosticData", privacy.DiagnosticData),
            ("windows.privacy.tailoredExperiences", privacy.TailoredExperiences),
            ("windows.privacy.onlineSpeechRecognition", privacy.OnlineSpeechRecognition),
            ("windows.privacy.findMyDevice", privacy.FindMyDevice),
            ("windows.privacy.inkingAndTypingPersonalization", privacy.InkingAndTypingPersonalization),
        };

        foreach (var preference in preferences)
        {
            if (!Enum.IsDefined(preference.Value))
            {
                errors.Add(new DeploymentValidationError(
                    "deployment.profile.privacy.invalid",
                    preference.FieldPath,
                    "Privacy preference must be a supported value."));
            }
        }
    }

    private static IReadOnlyList<DeploymentValidationError> ScanForbiddenCredentialFields(ReadOnlyMemory<byte> profileJson)
    {
        try
        {
            using var document = JsonDocument.Parse(profileJson);
            var errors = new List<DeploymentValidationError>();
            ScanElement(document.RootElement, "$", errors);
            return errors;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static void ScanElement(
        JsonElement element,
        string path,
        ICollection<DeploymentValidationError> errors)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                var propertyPath = path + "." + property.Name;
                var normalizedName = NormalizeFieldName(property.Name);
                if (ForbiddenCredentialFields.Contains(normalizedName) && propertyPath != "$.domain.credentials")
                {
                    errors.Add(new DeploymentValidationError(
                        "deployment.profile.credential.forbidden",
                        propertyPath,
                        "Deployment profile contains a forbidden credential field."));
                }

                ScanElement(property.Value, propertyPath, errors);
            }

            return;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            ScanElement(item, $"{path}[{index}]", errors);
            index++;
        }
    }

    private static string NormalizeFieldName(string value) => new(
        value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
