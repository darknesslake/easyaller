using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Easyaller.Core.Profiles;

namespace Easyaller.App;

public sealed partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly FileProfileRepository _repository;
    private readonly ProfileImportExportService _profileImportExportService;
    private readonly ProfileEditorController _profileEditorController;
    private readonly ObservableCollection<ProfileListItem> _profiles = [];
    private ProfileListItem? _selectedProfile;
    private byte[]? _pendingImportSource;
    private ProvisioningProfile? _pendingExportProfile;
    private string _selectedProfileName = "Select a profile";
    private string _selectedProfileDescription = "Choose a saved profile to inspect its local state.";
    private string _selectedProfileRevision = "No profile selected";
    private event PropertyChangedEventHandler? ViewModelPropertyChanged;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        _repository = new FileProfileRepository(GetLocalProfileDirectory());
        _profileImportExportService = new ProfileImportExportService(_repository);
        _profileEditorController = new ProfileEditorController(_repository);
        ProfilesList.ItemsSource = _profiles;
        StoragePathText.Text = _repository.RootDirectory;
        RefreshProfiles();
    }

    event PropertyChangedEventHandler? INotifyPropertyChanged.PropertyChanged
    {
        add => ViewModelPropertyChanged += value;
        remove => ViewModelPropertyChanged -= value;
    }

    public string SelectedProfileName
    {
        get => _selectedProfileName;
        private set => SetField(ref _selectedProfileName, value);
    }

    public string SelectedProfileDescription
    {
        get => _selectedProfileDescription;
        private set => SetField(ref _selectedProfileDescription, value);
    }

    public string SelectedProfileRevision
    {
        get => _selectedProfileRevision;
        private set => SetField(ref _selectedProfileRevision, value);
    }

    public bool HasSelectedProfile => _selectedProfile is not null;

    private void CreateProfile_Click(object? sender, RoutedEventArgs e)
    {
        var profile = ProvisioningProfileFactory.CreateDefault(GetNextProfileName());
        var result = _repository.Create(profile);
        if (result.Status != ProfileRepositoryStatus.Success)
        {
            SetStatus($"Could not create profile: {GetMessage(result.Errors)}");
            return;
        }

        RefreshProfiles(profile.ProfileId);
        SetStatus($"Created {profile.Metadata.Name}.");
    }

    private void CloneProfile_Click(object? sender, RoutedEventArgs e)
    {
        if (_selectedProfile is null)
        {
            return;
        }

        var sourceName = _selectedProfile.Name;
        var result = _repository.Clone(_selectedProfile.Profile.ProfileId);
        if (result.Status != ProfileRepositoryStatus.Success)
        {
            SetStatus($"Could not clone profile: {GetMessage(result.Errors)}");
            return;
        }

        RefreshProfiles(result.Profile!.ProfileId);
        SetStatus($"Created a copy of {sourceName}.");
    }

    private void SaveProfile_Click(object? sender, RoutedEventArgs e)
    {
        if (_selectedProfile is null)
        {
            return;
        }

        var result = _profileEditorController.SaveSettings(
            _selectedProfile.Profile,
            CreateProfileSettingsEdit(_selectedProfile.Profile));
        if (result.Status != ProfileRepositoryStatus.Success)
        {
            SetStatus($"Could not save profile: {GetMessage(result.Errors)}");
            return;
        }

        RefreshProfiles(result.Profile!.ProfileId);
        SetStatus("Profile changes saved.");
    }

    private async void ImportProfile_Click(object? sender, RoutedEventArgs e)
    {
        if (!StorageProvider.CanOpen)
        {
            SetStatus("File import is not available on this platform.");
            return;
        }

        HideImportConflict();
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import Easyaller profile",
            AllowMultiple = false,
            FileTypeFilter = [ProfileFileType],
        });
        var file = files.FirstOrDefault();
        if (file is null)
        {
            SetStatus("Profile import cancelled.");
            return;
        }

        var source = await ReadFileWithinLimitAsync(file, ProfileImportExportService.DefaultMaximumImportBytes);
        if (source is null)
        {
            SetStatus("Profile import exceeds the 1 MiB limit.");
            return;
        }

        var preview = _profileImportExportService.PreviewImport(source);
        if (preview.Status == ProfileImportPreviewStatus.Invalid)
        {
            SetStatus($"Import rejected: {GetMessage(preview.Errors)}");
            return;
        }

        if (preview.Status == ProfileImportPreviewStatus.IoFailure)
        {
            SetStatus($"Could not inspect import: {GetMessage(preview.Errors)}");
            return;
        }

        _pendingImportSource = source;
        if (preview.Status == ProfileImportPreviewStatus.Conflict)
        {
            ImportConflictPanel.IsVisible = true;
            SetStatus($"Import preview: {preview.Profile!.Metadata.Name}. Review the conflict choice below.");
            return;
        }

        SaveImportedProfile(ProfileImportConflictResolution.Create);
    }

    private void ImportCreateCopy_Click(object? sender, RoutedEventArgs e) =>
        SaveImportedProfile(ProfileImportConflictResolution.CreateCopy);

    private void ImportReplace_Click(object? sender, RoutedEventArgs e) =>
        SaveImportedProfile(ProfileImportConflictResolution.Replace);

    private void ImportCancel_Click(object? sender, RoutedEventArgs e)
    {
        HideImportConflict();
        SetStatus("Profile import cancelled. No local files changed.");
    }

    private void ExportProfile_Click(object? sender, RoutedEventArgs e)
    {
        if (_selectedProfile is null)
        {
            return;
        }

        try
        {
            var preview = _profileImportExportService.PreviewExport(_selectedProfile.Profile);
            _pendingExportProfile = preview.Profile;
            ExportWarningText.Text = preview.ConfidentialFields.Count == 0
                ? "Export contains no fields marked as confidential."
                : $"Export review: {preview.ConfidentialFields.Count} field(s) may contain organization-specific information.";
            ExportConfirmationPanel.IsVisible = true;
        }
        catch (ProfileJsonException exception)
        {
            SetStatus($"Export rejected: {exception.Message}");
        }
    }

    private async void ConfirmExport_Click(object? sender, RoutedEventArgs e)
    {
        if (_pendingExportProfile is null || !StorageProvider.CanSave)
        {
            SetStatus("File export is not available on this platform.");
            return;
        }

        var destination = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Easyaller profile",
            SuggestedFileName = ToExportFileName(_pendingExportProfile.Metadata.Name),
            DefaultExtension = "wpprofile.json",
            FileTypeChoices = [ProfileFileType],
            ShowOverwritePrompt = true,
        });
        if (destination is null)
        {
            SetStatus("Profile export cancelled.");
            return;
        }

        var result = _profileImportExportService.ExportToFile(_pendingExportProfile, destination.Path.LocalPath);
        HideExportConfirmation();
        SetStatus(result.IsSuccess
            ? "Profile exported successfully."
            : $"Could not export profile: {GetMessage(result.Errors)}");
    }

    private void CancelExport_Click(object? sender, RoutedEventArgs e)
    {
        HideExportConfirmation();
        SetStatus("Profile export cancelled.");
    }

    private void DeleteProfile_Click(object? sender, RoutedEventArgs e)
    {
        if (_selectedProfile is null)
        {
            return;
        }

        if (ConfirmDeleteCheckBox.IsChecked != true)
        {
            SetStatus("Confirm local profile removal before deleting it.");
            return;
        }

        var selectedProfile = _selectedProfile.Profile;
        var result = _repository.Delete(selectedProfile.ProfileId, selectedProfile.Revision);
        ConfirmDeleteCheckBox.IsChecked = false;
        if (result.Status != ProfileRepositoryStatus.Success)
        {
            SetStatus($"Could not delete profile: {GetMessage(result.Errors)}");
            return;
        }

        RefreshProfiles();
        SetStatus($"Deleted {selectedProfile.Metadata.Name}. A local backup remains available.");
    }

    private void Refresh_Click(object? sender, RoutedEventArgs e)
    {
        RefreshProfiles(_selectedProfile?.Profile.ProfileId);
        SetStatus("Profile list refreshed.");
    }

    private void ProfilesList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        _selectedProfile = ProfilesList.SelectedItem as ProfileListItem;
        UpdateSelectionDetails();
    }

    private void RefreshProfiles(Guid? selectedProfileId = null)
    {
        var list = _repository.List();
        _profiles.Clear();
        foreach (var profile in list.Profiles)
        {
            _profiles.Add(new ProfileListItem(profile));
        }

        var selected = _profiles.FirstOrDefault(item => item.Profile.ProfileId == selectedProfileId)
            ?? _profiles.FirstOrDefault();
        ProfilesList.SelectedItem = selected;
        _selectedProfile = selected;
        UpdateSelectionDetails();

        if (list.Issues.Count > 0)
        {
            SetStatus($"{list.Issues.Count} invalid local profile file(s) moved to Corrupted.");
        }
    }

    private void UpdateSelectionDetails()
    {
        if (_selectedProfile is null)
        {
            SelectedProfileName = "Select a profile";
            SelectedProfileDescription = "Choose a saved profile to inspect its local state.";
            SelectedProfileRevision = "No profile selected";
        }
        else
        {
            SelectedProfileName = _selectedProfile.Name;
            SelectedProfileDescription = _selectedProfile.Profile.Metadata.Description ?? "No description provided.";
            SelectedProfileRevision = $"Revision {_selectedProfile.Profile.Revision}";
        }

        ProfileNameTextBox.Text = _selectedProfile?.Profile.Metadata.Name ?? string.Empty;
        ProfileDescriptionTextBox.Text = _selectedProfile?.Profile.Metadata.Description ?? string.Empty;
        PopulateSettingsControls(_selectedProfile?.Profile);

        OnPropertyChanged(nameof(HasSelectedProfile));
    }

    private string GetNextProfileName() => _profiles.Count == 0
        ? "New workstation profile"
        : $"New workstation profile {_profiles.Count + 1}";

    private static string GetLocalProfileDirectory() => OperatingSystem.IsWindows()
        ? FileProfileRepository.GetDefaultRootDirectory()
        : Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Easyaller",
            "Profiles");

    private static string GetMessage(IReadOnlyList<ProfileValidationError> errors) =>
        errors.FirstOrDefault()?.Message ?? "Please try again.";

    private void SetStatus(string message) => StatusText.Text = message;

    private ProfileSettingsEdit CreateProfileSettingsEdit(ProvisioningProfile original) => new(
        ProfileNameTextBox.Text,
        ProfileDescriptionTextBox.Text,
        GetSelectedEditions(),
        UiLanguageTextBox.Text,
        InputLocaleTextBox.Text,
        SystemLocaleTextBox.Text,
        UserLocaleTextBox.Text,
        TimeZoneTextBox.Text,
        OfflineInitialSetupCheckBox.IsChecked == true,
        GetOptionalBoolean(HideWirelessSetupComboBox),
        GetOptionalBoolean(HideOnlineAccountComboBox),
        ComputerNamePrefixTextBox.Text,
        GetSelectedEnum(ProxyModeComboBox, original.Machine.Proxy.Mode),
        GetSelectedEnum(DomainModeComboBox, original.Domain.Mode),
        GetSelectedEnum(LaunchModeComboBox, original.Deployment.LaunchMode),
        GetSelectedEnum(CleanupModeComboBox, original.Cleanup.ProvisioningAccount));

    private IReadOnlyList<WindowsEdition> GetSelectedEditions()
    {
        var editions = new List<WindowsEdition>();
        if (ProfessionalEditionCheckBox.IsChecked == true)
        {
            editions.Add(WindowsEdition.Professional);
        }

        if (EnterpriseEditionCheckBox.IsChecked == true)
        {
            editions.Add(WindowsEdition.Enterprise);
        }

        return editions;
    }

    private void PopulateSettingsControls(ProvisioningProfile? profile)
    {
        var windows = profile?.Windows;
        ProfessionalEditionCheckBox.IsChecked = windows?.SupportedEditions.Contains(WindowsEdition.Professional) == true;
        EnterpriseEditionCheckBox.IsChecked = windows?.SupportedEditions.Contains(WindowsEdition.Enterprise) == true;
        UiLanguageTextBox.Text = windows?.Locale.UiLanguage ?? string.Empty;
        InputLocaleTextBox.Text = windows?.Locale.InputLocale ?? string.Empty;
        SystemLocaleTextBox.Text = windows?.Locale.SystemLocale ?? string.Empty;
        UserLocaleTextBox.Text = windows?.Locale.UserLocale ?? string.Empty;
        TimeZoneTextBox.Text = windows?.TimeZone ?? string.Empty;
        OfflineInitialSetupCheckBox.IsChecked = windows?.Oobe.OfflineInitialSetup == true;
        SetOptionalBoolean(HideWirelessSetupComboBox, windows?.Oobe.HideWirelessSetup);
        SetOptionalBoolean(HideOnlineAccountComboBox, windows?.Oobe.HideOnlineAccountScreens);
        ComputerNamePrefixTextBox.Text = profile?.Machine.ComputerName.Prefix ?? string.Empty;
        SetSelectedEnum(ProxyModeComboBox, profile?.Machine.Proxy.Mode);
        SetSelectedEnum(DomainModeComboBox, profile?.Domain.Mode);
        SetSelectedEnum(LaunchModeComboBox, profile?.Deployment.LaunchMode);
        SetSelectedEnum(CleanupModeComboBox, profile?.Cleanup.ProvisioningAccount);
    }

    private static bool? GetOptionalBoolean(ComboBox comboBox) =>
        (comboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() switch
        {
            "true" => true,
            "false" => false,
            _ => null,
        };

    private static void SetOptionalBoolean(ComboBox comboBox, bool? value)
    {
        var target = value switch
        {
            true => "true",
            false => "false",
            null => "unset",
        };
        comboBox.SelectedItem = comboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), target, StringComparison.Ordinal));
    }

    private static T GetSelectedEnum<T>(ComboBox comboBox, T fallback)
        where T : struct, Enum =>
        Enum.TryParse<T>((comboBox.SelectedItem as ComboBoxItem)?.Content?.ToString(), out var selected)
            ? selected
            : fallback;

    private static void SetSelectedEnum<T>(ComboBox comboBox, T? value)
        where T : struct, Enum
    {
        comboBox.SelectedItem = comboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Content?.ToString(), value?.ToString(), StringComparison.Ordinal));
    }

    private void SaveImportedProfile(ProfileImportConflictResolution resolution)
    {
        if (_pendingImportSource is null)
        {
            return;
        }

        var result = _profileImportExportService.Import(_pendingImportSource, resolution);
        HideImportConflict();
        if (result.Status != ProfileImportStatus.Saved)
        {
            SetStatus($"Could not import profile: {GetMessage(result.Errors)}");
            return;
        }

        RefreshProfiles(result.Profile!.ProfileId);
        SetStatus("Profile imported successfully.");
    }

    private void HideImportConflict()
    {
        _pendingImportSource = null;
        ImportConflictPanel.IsVisible = false;
    }

    private void HideExportConfirmation()
    {
        _pendingExportProfile = null;
        ExportConfirmationPanel.IsVisible = false;
    }

    private static async Task<byte[]?> ReadFileWithinLimitAsync(IStorageFile file, int maximumBytes)
    {
        await using var source = await file.OpenReadAsync();
        await using var destination = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = await source.ReadAsync(buffer);
            if (read == 0)
            {
                return destination.ToArray();
            }

            if (destination.Length + read > maximumBytes)
            {
                return null;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read));
        }
    }

    private static string ToExportFileName(string profileName)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var normalizedName = new string(profileName
            .Select(character => invalidCharacters.Contains(character) ? '-' : character)
            .ToArray())
            .Trim();
        return string.IsNullOrEmpty(normalizedName) ? "easyaller-profile" : normalizedName;
    }

    private static readonly FilePickerFileType ProfileFileType = new("Easyaller profile")
    {
        Patterns = ["*.wpprofile.json"],
        MimeTypes = ["application/json"],
        AppleUniformTypeIdentifiers = ["public.json"],
    };

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        OnPropertyChanged(propertyName);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        ViewModelPropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed record ProfileListItem(ProvisioningProfile Profile)
{
    public string Name => Profile.Metadata.Name;

    public string Detail => $"Revision {Profile.Revision}  ·  {Profile.Windows.Architecture}  ·  {Profile.Windows.TimeZone}";
}
