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
    private readonly ObservableCollection<ApplicationListItem> _applications = [];
    private readonly ObservableCollection<InstructionListItem> _instructions = [];
    private ProfileListItem? _selectedProfile;
    private byte[]? _pendingImportSource;
    private ProvisioningProfile? _pendingExportProfile;
    private string _selectedProfileName = "Выберите профиль";
    private string _selectedProfileDescription = "Выберите сохранённый профиль, чтобы посмотреть его локальное состояние.";
    private string _selectedProfileRevision = "Профиль не выбран";
    private event PropertyChangedEventHandler? ViewModelPropertyChanged;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        _repository = new FileProfileRepository(GetLocalProfileDirectory());
        _profileImportExportService = new ProfileImportExportService(_repository);
        _profileEditorController = new ProfileEditorController(_repository);
        ProfilesList.ItemsSource = _profiles;
        ApplicationsList.ItemsSource = _applications;
        InstructionsList.ItemsSource = _instructions;
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
        var defaultProfile = ProvisioningProfileFactory.CreateDefault(GetNextProfileName());
        var profile = defaultProfile with
        {
            Metadata = defaultProfile.Metadata with { Description = "Нейтральный профиль Easyaller" },
        };
        var result = _repository.Create(profile);
        if (result.Status != ProfileRepositoryStatus.Success)
        {
            SetStatus($"Не удалось создать профиль: {GetMessage(result.Errors)}");
            return;
        }

        RefreshProfiles(profile.ProfileId);
        SetStatus($"Профиль «{profile.Metadata.Name}» создан.");
    }

    private void OpenSetup_Click(object? sender, RoutedEventArgs e) => new SetupWindow(_repository).Show(this);

    private void OpenUsbCreator_Click(object? sender, RoutedEventArgs e) => new UsbCreatorWindow().Show(this);

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
            SetStatus($"Не удалось клонировать профиль: {GetMessage(result.Errors)}");
            return;
        }

        RefreshProfiles(result.Profile!.ProfileId);
        SetStatus($"Создана копия профиля «{sourceName}».");
    }

    private void SaveProfile_Click(object? sender, RoutedEventArgs e)
    {
        if (_selectedProfile is null)
        {
            return;
        }

        var result = _profileEditorController.SaveComplete(
            _selectedProfile.Profile,
            CreateProfileSettingsEdit(_selectedProfile.Profile),
            _applications.Select(static item => item.Application).ToArray(),
            _instructions.Select(static item => item.Instruction).ToArray());
        if (result.Status != ProfileRepositoryStatus.Success)
        {
            SetStatus($"Не удалось сохранить профиль: {GetMessage(result.Errors)}");
            return;
        }

        RefreshProfiles(result.Profile!.ProfileId);
        SetStatus("Изменения профиля сохранены.");
    }

    private async void ImportProfile_Click(object? sender, RoutedEventArgs e)
    {
        if (!StorageProvider.CanOpen)
        {
            SetStatus("Импорт файлов недоступен на этой платформе.");
            return;
        }

        HideImportConflict();
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Импорт профиля Easyaller",
            AllowMultiple = false,
            FileTypeFilter = [ProfileFileType],
        });
        var file = files.FirstOrDefault();
        if (file is null)
        {
            SetStatus("Импорт профиля отменён.");
            return;
        }

        var source = await ReadFileWithinLimitAsync(file, ProfileImportExportService.DefaultMaximumImportBytes);
        if (source is null)
        {
            SetStatus("Размер импортируемого профиля превышает лимит 1 МиБ.");
            return;
        }

        var preview = _profileImportExportService.PreviewImport(source);
        if (preview.Status == ProfileImportPreviewStatus.Invalid)
        {
            SetStatus($"Импорт отклонён: {GetMessage(preview.Errors)}");
            return;
        }

        if (preview.Status == ProfileImportPreviewStatus.IoFailure)
        {
            SetStatus($"Не удалось проверить импорт: {GetMessage(preview.Errors)}");
            return;
        }

        _pendingImportSource = source;
        if (preview.Status == ProfileImportPreviewStatus.Conflict)
        {
            ImportConflictPanel.IsVisible = true;
            SetStatus($"Предпросмотр импорта: «{preview.Profile!.Metadata.Name}». Выберите действие для конфликта ниже.");
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
        SetStatus("Импорт профиля отменён. Локальные файлы не изменены.");
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
                ? "Экспорт не содержит полей, помеченных как конфиденциальные."
                : $"Проверьте экспорт: {preview.ConfidentialFields.Count} полей могут содержать данные организации.";
            ExportConfirmationPanel.IsVisible = true;
        }
        catch (ProfileJsonException exception)
        {
            SetStatus($"Экспорт отклонён: {exception.Message}");
        }
    }

    private async void ConfirmExport_Click(object? sender, RoutedEventArgs e)
    {
        if (_pendingExportProfile is null || !StorageProvider.CanSave)
        {
            SetStatus("Экспорт файлов недоступен на этой платформе.");
            return;
        }

        var destination = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Экспорт профиля Easyaller",
            SuggestedFileName = ToExportFileName(_pendingExportProfile.Metadata.Name),
            DefaultExtension = "wpprofile.json",
            FileTypeChoices = [ProfileFileType],
            ShowOverwritePrompt = true,
        });
        if (destination is null)
        {
            SetStatus("Экспорт профиля отменён.");
            return;
        }

        var result = _profileImportExportService.ExportToFile(_pendingExportProfile, destination.Path.LocalPath);
        HideExportConfirmation();
        SetStatus(result.IsSuccess
            ? "Профиль успешно экспортирован."
            : $"Не удалось экспортировать профиль: {GetMessage(result.Errors)}");
    }

    private void CancelExport_Click(object? sender, RoutedEventArgs e)
    {
        HideExportConfirmation();
        SetStatus("Экспорт профиля отменён.");
    }

    private void DeleteProfile_Click(object? sender, RoutedEventArgs e)
    {
        if (_selectedProfile is null)
        {
            return;
        }

        if (ConfirmDeleteCheckBox.IsChecked != true)
        {
            SetStatus("Подтвердите удаление локального профиля.");
            return;
        }

        var selectedProfile = _selectedProfile.Profile;
        var result = _repository.Delete(selectedProfile.ProfileId, selectedProfile.Revision);
        ConfirmDeleteCheckBox.IsChecked = false;
        if (result.Status != ProfileRepositoryStatus.Success)
        {
            SetStatus($"Не удалось удалить профиль: {GetMessage(result.Errors)}");
            return;
        }

        RefreshProfiles();
        SetStatus($"Профиль «{selectedProfile.Metadata.Name}» удалён. Локальная резервная копия сохранена.");
    }

    private void AddApplication_Click(object? sender, RoutedEventArgs e)
    {
        var application = new ApplicationProfile(
            ApplicationIdTextBox.Text?.Trim() ?? string.Empty,
            ApplicationDisplayNameTextBox.Text?.Trim() ?? string.Empty,
            GetSelectedEnum(ApplicationSourceComboBox, ApplicationSourceKind.PackageRelative),
            string.IsNullOrWhiteSpace(ApplicationPathTextBox.Text) ? null : ApplicationPathTextBox.Text.Trim(),
            (ApplicationArgumentsTextBox.Text ?? string.Empty)
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        _applications.Add(new ApplicationListItem(application));
        ApplicationsList.SelectedItem = _applications[^1];
        ApplicationIdTextBox.Text = string.Empty;
        ApplicationDisplayNameTextBox.Text = string.Empty;
        ApplicationPathTextBox.Text = string.Empty;
        ApplicationArgumentsTextBox.Text = string.Empty;
        SetStatus("Приложение добавлено. Сохраните изменения профиля.");
    }

    private void RemoveApplication_Click(object? sender, RoutedEventArgs e)
    {
        if (ApplicationsList.SelectedItem is not ApplicationListItem selected)
        {
            SetStatus("Выберите приложение для удаления.");
            return;
        }

        _applications.Remove(selected);
        SetStatus("Приложение удалено. Сохраните изменения профиля.");
    }

    private void AddInstruction_Click(object? sender, RoutedEventArgs e)
    {
        var instruction = new InstructionProfile(
            InstructionIdTextBox.Text?.Trim() ?? string.Empty,
            InstructionTitleTextBox.Text?.Trim() ?? string.Empty,
            InstructionContentTextBox.Text?.Trim() ?? string.Empty);
        _instructions.Add(new InstructionListItem(instruction));
        InstructionsList.SelectedItem = _instructions[^1];
        InstructionIdTextBox.Text = string.Empty;
        InstructionTitleTextBox.Text = string.Empty;
        InstructionContentTextBox.Text = string.Empty;
        SetStatus("Инструкция добавлена. Сохраните изменения профиля.");
    }

    private void RemoveInstruction_Click(object? sender, RoutedEventArgs e)
    {
        if (InstructionsList.SelectedItem is not InstructionListItem selected)
        {
            SetStatus("Выберите инструкцию для удаления.");
            return;
        }

        _instructions.Remove(selected);
        SetStatus("Инструкция удалена. Сохраните изменения профиля.");
    }

    private void Refresh_Click(object? sender, RoutedEventArgs e)
    {
        RefreshProfiles(_selectedProfile?.Profile.ProfileId);
        SetStatus("Список профилей обновлён.");
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
            SetStatus($"Повреждённых локальных файлов профиля: {list.Issues.Count}. Они перемещены в Corrupted.");
        }
    }

    private void UpdateSelectionDetails()
    {
        if (_selectedProfile is null)
        {
            SelectedProfileName = "Выберите профиль";
            SelectedProfileDescription = "Выберите сохранённый профиль, чтобы посмотреть его локальное состояние.";
            SelectedProfileRevision = "Профиль не выбран";
        }
        else
        {
            SelectedProfileName = _selectedProfile.Name;
            SelectedProfileDescription = _selectedProfile.Profile.Metadata.Description ?? "Описание не указано.";
            SelectedProfileRevision = $"Версия {_selectedProfile.Profile.Revision}";
        }

        ProfileNameTextBox.Text = _selectedProfile?.Profile.Metadata.Name ?? string.Empty;
        ProfileDescriptionTextBox.Text = _selectedProfile?.Profile.Metadata.Description ?? string.Empty;
        PopulateSettingsControls(_selectedProfile?.Profile);

        OnPropertyChanged(nameof(HasSelectedProfile));
    }

    private string GetNextProfileName() => _profiles.Count == 0
        ? "Новый профиль компьютера"
        : $"Новый профиль компьютера {_profiles.Count + 1}";

    private static string GetLocalProfileDirectory() => OperatingSystem.IsWindows()
        ? FileProfileRepository.GetDefaultRootDirectory()
        : Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Easyaller",
            "Profiles");

    private static string GetMessage(IReadOnlyList<ProfileValidationError> errors) =>
        errors.FirstOrDefault()?.Message ?? "Повторите попытку.";

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
        GetPrivacyPreference(),
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
        SetPrivacyPreference(windows?.Privacy);
        ComputerNamePrefixTextBox.Text = profile?.Machine.ComputerName.Prefix ?? string.Empty;
        SetSelectedEnum(ProxyModeComboBox, profile?.Machine.Proxy.Mode);
        SetSelectedEnum(DomainModeComboBox, profile?.Domain.Mode);
        SetSelectedEnum(LaunchModeComboBox, profile?.Deployment.LaunchMode);
        SetSelectedEnum(CleanupModeComboBox, profile?.Cleanup.ProvisioningAccount);
        ApplicationSourceComboBox.SelectedItem = ApplicationSourceComboBox.Items.OfType<ComboBoxItem>().FirstOrDefault();
        _applications.Clear();
        _instructions.Clear();
        if (profile is not null)
        {
            foreach (var application in profile.Applications)
            {
                _applications.Add(new ApplicationListItem(application));
            }

            foreach (var instruction in profile.Instructions)
            {
                _instructions.Add(new InstructionListItem(instruction));
            }
        }
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

    private PrivacyPreference? GetPrivacyPreference() =>
        Enum.TryParse<PrivacyPreference>((PrivacyPreferenceComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var value)
            ? value
            : null;

    private void SetPrivacyPreference(PrivacySettings? privacy)
    {
        var values = privacy is null
            ? []
            : new[]
            {
                privacy.LocationServices,
                privacy.AdvertisingId,
                privacy.DiagnosticData,
                privacy.TailoredExperiences,
                privacy.OnlineSpeechRecognition,
                privacy.FindMyDevice,
                privacy.InkingAndTypingPersonalization,
            };
        var tag = values.Distinct().Count() == 1 ? values[0].ToString() : "retain";
        PrivacyPreferenceComboBox.SelectedItem = PrivacyPreferenceComboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), tag, StringComparison.Ordinal));
    }

    private static T GetSelectedEnum<T>(ComboBox comboBox, T fallback)
        where T : struct, Enum =>
        Enum.TryParse<T>((comboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var selected)
            ? selected
            : fallback;

    private static void SetSelectedEnum<T>(ComboBox comboBox, T? value)
        where T : struct, Enum
    {
        comboBox.SelectedItem = comboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), value?.ToString(), StringComparison.Ordinal));
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
            SetStatus($"Не удалось импортировать профиль: {GetMessage(result.Errors)}");
            return;
        }

        RefreshProfiles(result.Profile!.ProfileId);
        SetStatus("Профиль успешно импортирован.");
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

    private static readonly FilePickerFileType ProfileFileType = new("Профиль Easyaller")
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

    public string Detail => $"Версия {Profile.Revision}  ·  {Profile.Windows.Architecture}  ·  {Profile.Windows.TimeZone}";
}

public sealed record ApplicationListItem(ApplicationProfile Application)
{
    public string DisplayName => Application.DisplayName;

    public string Detail => Application.SourceKind == ApplicationSourceKind.PackageRelative
        ? "Из пакета"
        : "Внешняя ручная установка";
}

public sealed record InstructionListItem(InstructionProfile Instruction)
{
    public string Id => Instruction.Id;

    public string Title => Instruction.Title;
}
