using Easyaller.Core.Profiles;

namespace Easyaller.App;

public sealed class ProfileEditorController(IProfileRepository repository)
{
    private readonly IProfileRepository _repository = repository ?? throw new ArgumentNullException(nameof(repository));

    public ProfileRepositoryWriteResult Save(ProvisioningProfile original, string? name, string? description)
    {
        ArgumentNullException.ThrowIfNull(original);

        var updated = original with
        {
            Metadata = new ProfileMetadata(
                name?.Trim() ?? string.Empty,
                string.IsNullOrWhiteSpace(description) ? null : description.Trim()),
        };
        return _repository.Update(updated, original.Revision);
    }
}
