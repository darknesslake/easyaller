using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Easyaller.Core.Profiles;

public sealed class ProfileJsonSerializer
{
    private static readonly IJsonTypeInfoResolver TypeInfoResolver = CreateTypeInfoResolver();

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        NewLine = "\n",
        TypeInfoResolver = TypeInfoResolver,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    private readonly ProvisioningProfileValidator _validator;

    public ProfileJsonSerializer(ProvisioningProfileValidator? validator = null)
    {
        _validator = validator ?? new ProvisioningProfileValidator();
    }

    public byte[] Serialize(ProvisioningProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var validation = _validator.Validate(profile);
        if (!validation.IsValid)
        {
            throw new ProfileJsonException(
                "profile.serialization.invalid",
                "profile",
                "Only a valid profile can be exported.");
        }

        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(profile, SerializerOptions) + "\n");
    }

    public ProfileReadResult Read(ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            EnsureNoDuplicateProperties(utf8Json);

            using var document = JsonDocument.Parse(utf8Json.ToArray());
            ValidateSchemaVersion(document.RootElement);

            var profile = JsonSerializer.Deserialize<ProvisioningProfile>(utf8Json, SerializerOptions);
            if (profile is null)
            {
                return ProfileReadResult.FromError(new ProfileValidationError(
                    "profile.json.empty",
                    "$",
                    "Profile JSON must contain an object."));
            }

            return new ProfileReadResult(profile, _validator.Validate(profile).Errors);
        }
        catch (ProfileJsonException exception)
        {
            return ProfileReadResult.FromError(new ProfileValidationError(
                exception.Code,
                exception.FieldPath,
                exception.Message));
        }
        catch (JsonException exception)
        {
            return ProfileReadResult.FromError(new ProfileValidationError(
                "profile.json.invalid",
                exception.Path ?? "$",
                exception.Message));
        }
    }

    private static void ValidateSchemaVersion(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new ProfileJsonException(
                "profile.json.root.invalid",
                "$",
                "Profile JSON root must be an object.");
        }

        if (!root.TryGetProperty("schemaVersion", out var schemaVersion))
        {
            throw new ProfileJsonException(
                "profile.schemaVersion.required",
                "schemaVersion",
                "Schema version is required.");
        }

        if (!schemaVersion.TryGetInt32(out var value))
        {
            throw new ProfileJsonException(
                "profile.schemaVersion.invalid",
                "schemaVersion",
                "Schema version must be an integer.");
        }

        if (value != ProvisioningProfile.CurrentSchemaVersion)
        {
            throw new ProfileJsonException(
                "profile.schemaVersion.unsupported",
                "schemaVersion",
                $"Schema version {value} is not supported.");
        }
    }

    private static void EnsureNoDuplicateProperties(ReadOnlySpan<byte> utf8Json)
    {
        var reader = new Utf8JsonReader(utf8Json);
        var propertyNames = new Stack<HashSet<string>>();

        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    propertyNames.Push(new HashSet<string>(StringComparer.Ordinal));
                    break;

                case JsonTokenType.EndObject:
                    propertyNames.Pop();
                    break;

                case JsonTokenType.PropertyName:
                    var propertyName = reader.GetString()!;
                    if (!propertyNames.Peek().Add(propertyName))
                    {
                        throw new ProfileJsonException(
                            "profile.json.duplicateProperty",
                            "$",
                            $"Property '{propertyName}' occurs more than once in the same object.");
                    }

                    break;
            }
        }
    }

    private static IJsonTypeInfoResolver CreateTypeInfoResolver()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(static typeInfo =>
        {
            if (typeInfo.Kind != JsonTypeInfoKind.Object)
            {
                return;
            }

            var properties = typeInfo.Properties.OrderBy(static property => property.Name, StringComparer.Ordinal).ToArray();
            for (var index = 0; index < properties.Length; index++)
            {
                properties[index].Order = typeInfo.Type == typeof(ProvisioningProfile)
                    ? GetRootPropertyOrder(properties[index].Name)
                    : index + 1;
            }
        });

        return resolver;
    }

    private static int GetRootPropertyOrder(string propertyName) => propertyName switch
    {
        "schemaVersion" or "SchemaVersion" => 1,
        "profileId" or "ProfileId" => 2,
        "revision" or "Revision" => 3,
        "metadata" or "Metadata" => 4,
        "windows" or "Windows" => 5,
        "machine" or "Machine" => 6,
        "domain" or "Domain" => 7,
        "applications" or "Applications" => 8,
        "instructions" or "Instructions" => 9,
        "deployment" or "Deployment" => 10,
        "cleanup" or "Cleanup" => 11,
        _ => int.MaxValue,
    };
}

public sealed record ProfileReadResult(ProvisioningProfile? Profile, IReadOnlyList<ProfileValidationError> Errors)
{
    public bool IsValid => Profile is not null && Errors.Count == 0;

    public static ProfileReadResult FromError(ProfileValidationError error) => new(null, [error]);
}

public sealed class ProfileJsonException(string code, string fieldPath, string message) : Exception(message)
{
    public string Code { get; } = code;

    public string FieldPath { get; } = fieldPath;
}
