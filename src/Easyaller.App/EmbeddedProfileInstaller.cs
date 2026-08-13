using System.Reflection;
using Easyaller.Core.Profiles;

namespace Easyaller.App;

internal static class EmbeddedProfileInstaller
{
    private const string ResourceName = "Easyaller.EmbeddedProfile.wpprofile.json";

    public static void InstallIfMissing(FileProfileRepository repository)
    {
        ArgumentNullException.ThrowIfNull(repository);

        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            return;
        }

        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var result = new ProfileJsonSerializer().Read(memory.ToArray());
        if (!result.IsValid || result.Profile is null)
        {
            return;
        }

        if (repository.Read(result.Profile.ProfileId).Status == ProfileRepositoryStatus.NotFound)
        {
            repository.Create(result.Profile);
        }
    }
}
