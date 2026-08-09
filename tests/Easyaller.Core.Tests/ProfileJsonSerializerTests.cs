using System.Text;
using System.Text.Json;
using Easyaller.Core.Profiles;

namespace Easyaller.Core.Tests;

public sealed class ProfileJsonSerializerTests
{
    private readonly ProfileJsonSerializer _serializer = new();

    [Fact]
    public void Serialize_SameProfileTwice_ProducesByteIdenticalUtf8JsonWithTrailingNewline()
    {
        var profile = ProvisioningProfileFactory.CreateDefault();

        var first = _serializer.Serialize(profile);
        var second = _serializer.Serialize(profile);

        Assert.Equal(first, second);
        Assert.EndsWith("\n", Encoding.UTF8.GetString(first));
        Assert.StartsWith("{\n  \"schemaVersion\": 1,\n  \"profileId\":", Encoding.UTF8.GetString(first));
        Assert.True(_serializer.Read(first).IsValid);
    }

    [Fact]
    public void Read_ValidFixture_ReturnsProfile()
    {
        var result = _serializer.Read(ReadFixture("valid-profile.wpprofile.json"));

        Assert.True(result.IsValid);
        Assert.NotNull(result.Profile);
        Assert.Equal("Example workstation", result.Profile.Metadata.Name);
        Assert.Equal([WindowsEdition.Professional, WindowsEdition.Enterprise], result.Profile.Windows.SupportedEditions);
    }

    [Fact]
    public void Read_PublicNeutralExample_ReturnsValidProfile()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Examples", "neutral-workstation.wpprofile.json");

        var result = _serializer.Read(File.ReadAllBytes(path));

        Assert.True(result.IsValid);
        Assert.NotNull(result.Profile);
        Assert.Equal("Neutral workstation", result.Profile.Metadata.Name);
        Assert.Equal(CredentialHandling.PromptAtRuntime, result.Profile.Domain.Credentials);
    }

    [Theory]
    [InlineData("invalid-duplicate-property.wpprofile.json", "profile.json.duplicateProperty")]
    [InlineData("invalid-future-schema.wpprofile.json", "profile.schemaVersion.unsupported")]
    [InlineData("invalid-forbidden-password.wpprofile.json", "profile.json.invalid")]
    public void Read_InvalidFixture_ReturnsStableError(string fixtureName, string errorCode)
    {
        var result = _serializer.Read(ReadFixture(fixtureName));

        Assert.False(result.IsValid);
        Assert.Null(result.Profile);
        Assert.Contains(result.Errors, error => error.Code == errorCode);
    }

    [Fact]
    public void Read_MissingRequiredNestedProperty_ReturnsError()
    {
        var json = """
        {
          "schemaVersion": 1,
          "profileId": "ef0cceae-1662-4ad2-95d5-c105f3274853",
          "revision": 1,
          "metadata": { "name": "Incomplete profile" },
          "windows": {},
          "machine": {},
          "domain": {},
          "applications": [],
          "instructions": [],
          "deployment": {},
          "cleanup": {}
        }
        """;

        var result = _serializer.Read(Encoding.UTF8.GetBytes(json));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code == "profile.json.invalid");
    }

    [Fact]
    public void Read_UnknownTopLevelProperty_ReturnsErrorWithJsonPath()
    {
        var json = """
        {
          "schemaVersion": 1,
          "unexpected": true
        }
        """;

        var result = _serializer.Read(Encoding.UTF8.GetBytes(json));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code == "profile.json.invalid");
    }

    [Fact]
    public void Schema_IsDraft202012AndRejectsAdditionalProperties()
    {
        using var schema = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "provisioning-profile.schema.json")));

        Assert.Equal("https://json-schema.org/draft/2020-12/schema", schema.RootElement.GetProperty("$schema").GetString());
        Assert.False(schema.RootElement.GetProperty("additionalProperties").GetBoolean());
        Assert.True(schema.RootElement.GetProperty("$defs").TryGetProperty("domain", out var domain));
        Assert.False(domain.GetProperty("additionalProperties").GetBoolean());
    }

    private static byte[] ReadFixture(string fileName) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Profiles", fileName));
}
