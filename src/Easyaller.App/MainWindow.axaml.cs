using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Easyaller.Core.Profiles;

namespace Easyaller.App;

public sealed partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly FileProfileRepository _repository;
    private readonly ObservableCollection<ProfileListItem> _profiles = [];
    private ProfileListItem? _selectedProfile;
    private string _selectedProfileName = "Select a profile";
    private string _selectedProfileDescription = "Choose a saved profile to inspect its local state.";
    private string _selectedProfileRevision = "No profile selected";
    private event PropertyChangedEventHandler? ViewModelPropertyChanged;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        _repository = new FileProfileRepository(GetLocalProfileDirectory());
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
