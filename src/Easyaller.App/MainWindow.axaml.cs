using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Easyaller.Core.Profiles;

namespace Easyaller.App;

public sealed partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly FileProfileRepository _repository;
    private readonly ProfileImportExportService _profileImportExportService;
    private readonly ProfileEditorController _profileEditorController;
    private readonly List<ProfileListItem> _allProfiles = [];
    private readonly ObservableCollection<ProfileListItem> _profiles = [];
    private readonly ObservableCollection<ApplicationListItem> _applications = [];
    private readonly ObservableCollection<InstructionListItem> _instructions = [];
    private ProfileListItem? _selectedProfile;
    private byte[]? _pendingImportSource;
    private ProvisioningProfile? _pendingExportProfile;
    private string _selectedProfileName = "Выберите профиль";
    private string _selectedProfileDescription = "Выберите сохранённый профиль, чтобы посмотреть его локальное состояние.";
    private string _selectedProfileRevision = "Профиль не выбран";
    private string _profileListCountText = "0 профилей";
    private bool _hasUnsavedChanges;
    private bool _isPopulatingEditor;
    private bool _isChangingProfileSelection;
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
        AttachEditorChangeHandlers();
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

    public string ProfileListCountText
    {
        get => _profileListCountText;
        private set => SetField(ref _profileListCountText, value);
    }

    public bool HasSelectedProfile => _selectedProfile is not null;
    public bool HasUnsavedChanges => _hasUnsavedChanges;
    public bool CanSaveProfile => HasSelectedProfile && HasUnsavedChanges;
    public bool CanUseSavedProfileActions => HasSelectedProfile && !HasUnsavedChanges;
    public bool CanSearchProfiles => _allProfiles.Count > 0 && !HasUnsavedChanges;
    public bool HasNoVisibleProfiles => _profiles.Count == 0;

    private void MainWindow_Closing(object? sender, WindowClosingEventArgs e)
    {
        if (!HasUnsavedChanges)
        {
            return;
        }

        e.Cancel = true;
        SetStatus("Окно не закрыто: сохраните изменения или нажмите «Сбросить».");
    }

    private void CreateProfile_Click(object? sender, RoutedEventArgs e)
    {
        if (!CanLeaveCurrentProfile())
        {
            return;
        }

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

    private void OpenSetup_Click(object? sender, RoutedEventArgs e)
    {
        if (CanLeaveCurrentProfile())
        {
            new SetupWindow(_repository).Show(this);
        }
    }

    private void OpenUsbCreator_Click(object? sender, RoutedEventArgs e) => new UsbCreatorWindow().Show(this);

    private void CloneProfile_Click(object? sender, RoutedEventArgs e)
    {
        if (_selectedProfile is null || !CanLeaveCurrentProfile())
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
            RefreshEditorFeedback();
            SetStatus($"Не удалось сохранить профиль: {GetMessage(result.Errors)}");
            return;
        }

        SetHasUnsavedChanges(false);
        RefreshProfiles(result.Profile!.ProfileId);
        SetStatus("Изменения профиля сохранены.");
    }

    private void ResetProfile_Click(object? sender, RoutedEventArgs e)
    {
        if (_selectedProfile is null)
        {
            return;
        }

        ConfirmDeleteCheckBox.IsChecked = false;
        UpdateSelectionDetails();
        SetStatus("Несохранённые изменения сброшены. Показана сохранённая версия профиля.");
    }

    private void ToggleWindowsSection_Click(object? sender, RoutedEventArgs e) =>
        ToggleProfileSection(WindowsSectionContent, WindowsSectionChevron);

    private void ToggleNetworkSection_Click(object? sender, RoutedEventArgs e) =>
        ToggleProfileSection(NetworkSectionContent, NetworkSectionChevron);

    private void ToggleDomainSection_Click(object? sender, RoutedEventArgs e) =>
        ToggleProfileSection(DomainSectionContent, DomainSectionChevron);

    private void ToggleApplicationsSection_Click(object? sender, RoutedEventArgs e) =>
        ToggleProfileSection(ApplicationsSectionContent, ApplicationsSectionChevron);

    private void ToggleProfileSection(Border targetContent, TextBlock targetChevron)
    {
        var shouldOpen = !targetContent.IsVisible;
        WindowsSectionContent.IsVisible = false;
        NetworkSectionContent.IsVisible = false;
        DomainSectionContent.IsVisible = false;
        ApplicationsSectionContent.IsVisible = false;
        WindowsSectionChevron.Text = "⌄";
        NetworkSectionChevron.Text = "⌄";
        DomainSectionChevron.Text = "⌄";
        ApplicationsSectionChevron.Text = "⌄";

        if (shouldOpen)
        {
            targetContent.IsVisible = true;
            targetChevron.Text = "⌃";
        }
    }

    private async void ImportProfile_Click(object? sender, RoutedEventArgs e)
    {
        if (!CanLeaveCurrentProfile())
        {
            return;
        }

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
        if (_selectedProfile is null || !CanLeaveCurrentProfile())
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
        if (_selectedProfile is null || !CanLeaveCurrentProfile())
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

    private void ConfirmDeleteCheckBox_IsCheckedChanged(object? sender, RoutedEventArgs e) => UpdateDeleteButtonState();

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
        if (!CanLeaveCurrentProfile())
        {
            return;
        }

        RefreshProfiles(_selectedProfile?.Profile.ProfileId);
        SetStatus("Список профилей обновлён.");
    }

    private void ProfileSearchTextBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_isChangingProfileSelection)
        {
            return;
        }

        ApplyProfileFilter(_selectedProfile?.Profile.ProfileId, forceEditorRefresh: false);
        SetStatus(HasNoVisibleProfiles
            ? "По этому запросу профили не найдены."
            : $"Показано профилей: {_profiles.Count}.");
    }

    private void ProfilesList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isChangingProfileSelection)
        {
            return;
        }

        var requestedProfile = ProfilesList.SelectedItem as ProfileListItem;
        if (HasUnsavedChanges
            && requestedProfile?.Profile.ProfileId != _selectedProfile?.Profile.ProfileId)
        {
            _isChangingProfileSelection = true;
            ProfilesList.SelectedItem = _selectedProfile;
            _isChangingProfileSelection = false;
            SetStatus("Сначала сохраните или сбросьте изменения текущего профиля.");
            return;
        }

        _selectedProfile = requestedProfile;
        UpdateSelectionDetails();
    }

    private void RefreshProfiles(Guid? selectedProfileId = null)
    {
        var list = _repository.List();
        _allProfiles.Clear();
        foreach (var profile in list.Profiles)
        {
            _allProfiles.Add(new ProfileListItem(profile));
        }

        if (selectedProfileId is { } requestedId
            && _allProfiles.Any(item => item.Profile.ProfileId == requestedId)
            && !_allProfiles.Where(item => ProfileListFilter.Matches(item, ProfileSearchTextBox.Text))
                .Any(item => item.Profile.ProfileId == requestedId))
        {
            _isChangingProfileSelection = true;
            ProfileSearchTextBox.Text = string.Empty;
            _isChangingProfileSelection = false;
        }

        ApplyProfileFilter(selectedProfileId, forceEditorRefresh: true);

        if (list.Issues.Count > 0)
        {
            SetStatus($"Повреждённых локальных файлов профиля: {list.Issues.Count}. Они перемещены в Corrupted.");
        }
        else if (_selectedProfile is not null)
        {
            SetStatus($"Выбран профиль «{_selectedProfile.Name}». Измените настройки или откройте экран применения.");
        }
    }

    private void ApplyProfileFilter(Guid? selectedProfileId, bool forceEditorRefresh)
    {
        var previousProfileId = _selectedProfile?.Profile.ProfileId;
        var matchingProfiles = _allProfiles
            .Where(item => ProfileListFilter.Matches(item, ProfileSearchTextBox.Text))
            .ToArray();

        _isChangingProfileSelection = true;
        try
        {
            _profiles.Clear();
            foreach (var item in matchingProfiles)
            {
                _profiles.Add(item);
            }

            var selected = _profiles.FirstOrDefault(item => item.Profile.ProfileId == selectedProfileId)
                ?? _profiles.FirstOrDefault();
            ProfilesList.SelectedItem = selected;
            _selectedProfile = selected;
        }
        finally
        {
            _isChangingProfileSelection = false;
        }

        UpdateProfileListCount();
        if (forceEditorRefresh || previousProfileId != _selectedProfile?.Profile.ProfileId)
        {
            UpdateSelectionDetails();
        }
        else
        {
            OnPropertyChanged(nameof(HasSelectedProfile));
            OnPropertyChanged(nameof(CanUseSavedProfileActions));
        }
    }

    private void UpdateProfileListCount()
    {
        ProfileListCountText = string.IsNullOrWhiteSpace(ProfileSearchTextBox.Text)
            ? GetProfileCountText(_profiles.Count)
            : $"{_profiles.Count} из {_allProfiles.Count}";
        OnPropertyChanged(nameof(HasNoVisibleProfiles));
        OnPropertyChanged(nameof(CanSearchProfiles));
    }

    private static string GetProfileCountText(int count)
    {
        var lastTwoDigits = count % 100;
        var lastDigit = count % 10;
        var noun = lastTwoDigits is >= 11 and <= 14
            ? "профилей"
            : lastDigit switch
            {
                1 => "профиль",
                2 or 3 or 4 => "профиля",
                _ => "профилей",
            };
        return $"{count} {noun}";
    }

    private void UpdateSelectionDetails()
    {
        _isPopulatingEditor = true;
        try
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
            ConfirmDeleteCheckBox.IsChecked = false;
        }
        finally
        {
            _isPopulatingEditor = false;
        }

        SetHasUnsavedChanges(false);
        RefreshEditorFeedback();
        UpdateDeleteButtonState();
        ProfileEditorScrollViewer.Offset = default;
        Dispatcher.UIThread.Post(
            () => ProfileEditorScrollViewer.Offset = default,
            DispatcherPriority.Background);

        OnPropertyChanged(nameof(HasSelectedProfile));
        OnPropertyChanged(nameof(CanSaveProfile));
        OnPropertyChanged(nameof(CanUseSavedProfileActions));
    }

    private void UpdateDeleteButtonState() => DeleteProfileButton.IsEnabled =
        _selectedProfile is not null && !HasUnsavedChanges && ConfirmDeleteCheckBox.IsChecked == true;

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
        errors.FirstOrDefault() is { } error ? GetValidationMessage(error) : "Повторите попытку.";

    private void SetStatus(string message) => StatusText.Text = message;

    private void AttachEditorChangeHandlers()
    {
        foreach (var textBox in new[]
        {
            ProfileNameTextBox,
            ProfileDescriptionTextBox,
            ComputerNamePrefixTextBox,
            StaticIpv4AddressTextBox,
            StaticIpv4SubnetMaskTextBox,
            StaticIpv4DefaultGatewayTextBox,
            StaticIpv4DnsServersTextBox,
            ProxyBypassListTextBox,
        })
        {
            textBox.TextChanged += (_, _) => EditorValueChanged();
        }

        foreach (var comboBox in new[]
        {
            UiLanguageComboBox,
            SystemLocaleComboBox,
            UserLocaleComboBox,
            TimeZoneComboBox,
            HideWirelessSetupComboBox,
            HideOnlineAccountComboBox,
            PrivacyPreferenceComboBox,
            NetworkModeComboBox,
            ProxyModeComboBox,
            DomainModeComboBox,
            LaunchModeComboBox,
            CleanupModeComboBox,
        })
        {
            comboBox.SelectionChanged += (_, _) => EditorValueChanged();
        }

        foreach (var checkBox in new[]
        {
            ProfessionalEditionCheckBox,
            EnterpriseEditionCheckBox,
            OfflineInitialSetupCheckBox,
        })
        {
            checkBox.IsCheckedChanged += (_, _) => EditorValueChanged();
        }

        _applications.CollectionChanged += (_, _) => EditorValueChanged();
        _instructions.CollectionChanged += (_, _) => EditorValueChanged();
    }

    private void EditorValueChanged()
    {
        if (_isPopulatingEditor || _selectedProfile is null)
        {
            return;
        }

        var original = _selectedProfile.Profile;
        SetHasUnsavedChanges(_profileEditorController.HasChanges(
            original,
            CreateProfileSettingsEdit(original),
            _applications.Select(static item => item.Application).ToArray(),
            _instructions.Select(static item => item.Instruction).ToArray()));
        RefreshEditorFeedback();
    }

    private void SetHasUnsavedChanges(bool value)
    {
        if (_hasUnsavedChanges == value)
        {
            return;
        }

        _hasUnsavedChanges = value;
        OnPropertyChanged(nameof(HasUnsavedChanges));
        OnPropertyChanged(nameof(CanSaveProfile));
        OnPropertyChanged(nameof(CanUseSavedProfileActions));
        OnPropertyChanged(nameof(CanSearchProfiles));
        UpdateDeleteButtonState();
    }

    private bool CanLeaveCurrentProfile()
    {
        if (!HasUnsavedChanges)
        {
            return true;
        }

        SetStatus("Есть несохранённые изменения. Сохраните их или нажмите «Сбросить».");
        return false;
    }

    private void RefreshEditorFeedback()
    {
        ClearInlineErrors();
        UpdateProfileSummary();
        if (_selectedProfile is null)
        {
            SetSectionStatus(WindowsSectionStatusText, hasErrors: false, isOptional: true);
            SetSectionStatus(NetworkSectionStatusText, hasErrors: false, isOptional: true);
            SetSectionStatus(DomainSectionStatusText, hasErrors: false, isOptional: true);
            SetSectionStatus(ApplicationsSectionStatusText, hasErrors: false, isOptional: true);
            return;
        }

        var original = _selectedProfile.Profile;
        var staticNetworkEnabled = GetSelectedEnum(NetworkModeComboBox, original.Machine.Network.Mode)
            == NetworkConfigurationMode.StaticIpv4;
        StaticIpv4AddressTextBox.IsEnabled = staticNetworkEnabled;
        StaticIpv4SubnetMaskTextBox.IsEnabled = staticNetworkEnabled;
        StaticIpv4DefaultGatewayTextBox.IsEnabled = staticNetworkEnabled;
        StaticIpv4DnsServersTextBox.IsEnabled = staticNetworkEnabled;
        ProxyBypassListTextBox.IsEnabled = GetSelectedEnum(ProxyModeComboBox, original.Machine.Proxy.Mode)
            == ProxyConfigurationMode.PromptAtRuntime;
        var validation = _profileEditorController.ValidateComplete(
            original,
            CreateProfileSettingsEdit(original),
            _applications.Select(static item => item.Application).ToArray(),
            _instructions.Select(static item => item.Instruction).ToArray());
        var errors = validation.Errors;

        ShowInlineError(ProfileNameErrorText, FindError(errors, "metadata.name"));
        ShowInlineError(WindowsEditionErrorText, FindError(errors, "windows.supportedEditions"));
        ShowInlineError(
            WindowsSettingsErrorText,
            errors.FirstOrDefault(error =>
                error.FieldPath.StartsWith("windows.", StringComparison.Ordinal)
                && error.FieldPath != "windows.supportedEditions"));
        ShowInlineError(ComputerNamePrefixErrorText, FindError(errors, "machine.computerName.prefix"));
        ShowInlineError(StaticIpv4AddressErrorText, FindError(errors, "machine.network.staticIpv4.address"));
        ShowInlineError(StaticIpv4SubnetMaskErrorText, FindError(errors, "machine.network.staticIpv4.subnetMask"));
        ShowInlineError(StaticIpv4DefaultGatewayErrorText, FindError(errors, "machine.network.staticIpv4.defaultGateway"));
        ShowInlineError(StaticIpv4DnsServersErrorText, FindError(errors, "machine.network.staticIpv4.dnsServers"));
        ShowInlineError(ProxyBypassListErrorText, FindError(errors, "machine.proxy.bypassList"));
        ShowInlineError(
            ApplicationsErrorText,
            errors.FirstOrDefault(error => error.FieldPath.StartsWith("applications", StringComparison.Ordinal)));

        SetSectionStatus(
            WindowsSectionStatusText,
            errors.Any(error => error.FieldPath.StartsWith("windows.", StringComparison.Ordinal)),
            isOptional: false);
        SetSectionStatus(
            NetworkSectionStatusText,
            errors.Any(error => error.FieldPath.StartsWith("machine.", StringComparison.Ordinal)),
            GetSelectedEnum(NetworkModeComboBox, original.Machine.Network.Mode) == NetworkConfigurationMode.PromptAtRuntime
                && GetSelectedEnum(ProxyModeComboBox, original.Machine.Proxy.Mode) == ProxyConfigurationMode.NotConfigured
                && string.IsNullOrWhiteSpace(ComputerNamePrefixTextBox.Text));
        SetSectionStatus(
            DomainSectionStatusText,
            errors.Any(error => error.FieldPath.StartsWith("domain.", StringComparison.Ordinal)),
            GetSelectedEnum(DomainModeComboBox, original.Domain.Mode) == DomainMode.NotConfigured
                && GetSelectedEnum(LaunchModeComboBox, original.Deployment.LaunchMode) == ProvisionerLaunchMode.Manual);
        SetSectionStatus(
            ApplicationsSectionStatusText,
            errors.Any(error => error.FieldPath.StartsWith("applications", StringComparison.Ordinal)),
            _applications.Count == 0 && _instructions.Count == 0);
    }

    private void UpdateProfileSummary()
    {
        if (_selectedProfile is null)
        {
            ProfileSummaryText.Text = "Выберите профиль, чтобы увидеть его основные настройки.";
            return;
        }

        var original = _selectedProfile.Profile;
        var editions = GetSelectedEditions();
        var editionText = editions.Count == 0
            ? "Редакция не выбрана"
            : string.Join(" + ", editions.Select(static edition => edition == WindowsEdition.Professional ? "Windows 11 Pro" : "Windows 11 Enterprise"));
        var language = GetSelectedTag(UiLanguageComboBox, original.Windows.Locale.UiLanguage) == "ru-RU" ? "Русский" : "English";
        var timeZone = GetSelectedTag(TimeZoneComboBox, original.Windows.TimeZone) switch
        {
            "West Asia Standard Time" => "UTC+05",
            "Central Asia Standard Time" => "UTC+06",
            "Russian Standard Time" => "UTC+03",
            var value => value,
        };
        var network = GetSelectedEnum(NetworkModeComboBox, original.Machine.Network.Mode) == NetworkConfigurationMode.StaticIpv4
            ? "Статический IPv4"
            : "Сеть при применении";
        var proxy = GetSelectedEnum(ProxyModeComboBox, original.Machine.Proxy.Mode) == ProxyConfigurationMode.PromptAtRuntime
            ? "Прокси при применении"
            : "Без настройки прокси";
        var domain = GetSelectedEnum(DomainModeComboBox, original.Domain.Mode) switch
        {
            DomainMode.Required => "Домен обязателен",
            DomainMode.Optional => "Домен по выбору",
            _ => "Без домена",
        };
        var repeatedSteps = _applications.Count == 0 && _instructions.Count == 0
            ? "Без приложений и инструкций"
            : $"Приложений: {_applications.Count}, инструкций: {_instructions.Count}";

        ProfileSummaryText.Text = string.Join(" · ", editionText, language, timeZone, network, proxy, domain, repeatedSteps);
    }

    private void ClearInlineErrors()
    {
        foreach (var textBlock in new[]
        {
            ProfileNameErrorText,
            WindowsEditionErrorText,
            WindowsSettingsErrorText,
            ComputerNamePrefixErrorText,
            StaticIpv4AddressErrorText,
            StaticIpv4SubnetMaskErrorText,
            StaticIpv4DefaultGatewayErrorText,
            StaticIpv4DnsServersErrorText,
            ProxyBypassListErrorText,
            ApplicationsErrorText,
        })
        {
            textBlock.Text = string.Empty;
            textBlock.IsVisible = false;
        }
    }

    private static ProfileValidationError? FindError(
        IReadOnlyList<ProfileValidationError> errors,
        string fieldPathPrefix) =>
        errors.FirstOrDefault(error => error.FieldPath.StartsWith(fieldPathPrefix, StringComparison.Ordinal));

    private static void ShowInlineError(TextBlock textBlock, ProfileValidationError? error)
    {
        textBlock.Text = error is null ? string.Empty : GetValidationMessage(error);
        textBlock.IsVisible = error is not null;
    }

    private static void SetSectionStatus(TextBlock textBlock, bool hasErrors, bool isOptional)
    {
        textBlock.Text = hasErrors ? "Ошибка" : isOptional ? "Необязательно" : "Готово";
        textBlock.Foreground = Brush.Parse(hasErrors ? "#FCA5A5" : isOptional ? "#AFC4DF" : "#86E1B5");
    }

    private static string GetValidationMessage(ProfileValidationError error) => error.Code switch
    {
        "profile.name.required" => "Укажите название профиля.",
        "windows.editions.required" => "Выберите хотя бы одну редакцию Windows 11.",
        "windows.locale.required" => "Выберите язык из списка.",
        "windows.locale.unknown" => "Выбранный язык не поддерживается.",
        "windows.timeZone.required" => "Выберите часовой пояс.",
        "windows.oobe.offline.requiresWirelessHide" => "Для автономного первого запуска скройте экран подключения к сети.",
        "windows.oobe.offline.requiresOnlineAccountHide" => "Для автономного первого запуска скройте экран онлайн-учётной записи.",
        "machine.computerName.prefix.invalid" => "Префикс: до 15 латинских букв, цифр или дефисов.",
        "machine.network.staticIpv4.ipv4.invalid" => "Введите корректный IPv4-адрес.",
        "machine.network.staticIpv4.ipv4.unusable" => "Этот IPv4-адрес нельзя использовать для рабочей настройки.",
        "machine.network.staticIpv4.subnetMask.invalid" => "Введите корректную маску подсети.",
        "machine.network.staticIpv4.subnetMask.unsupported" => "Эта маска подсети не поддерживается.",
        "machine.network.staticIpv4.gateway.outsideSubnet" => "Шлюз должен находиться в той же подсети.",
        "machine.network.staticIpv4.address.host.invalid" => "Укажите адрес компьютера, а не адрес сети или broadcast.",
        "machine.network.staticIpv4.gateway.host.invalid" => "Укажите отдельный допустимый адрес шлюза.",
        "machine.network.staticIpv4.dnsServers.count.invalid" => "Укажите от одного до трёх DNS-серверов.",
        "machine.network.staticIpv4.dnsServers.duplicate" => "DNS-серверы не должны повторяться.",
        "machine.proxy.bypassList.unexpected" => "Исключения доступны только когда прокси запрашивается при применении.",
        "machine.proxy.bypassList.entry.invalid" => "Проверьте формат исключения прокси.",
        "machine.proxy.bypassList.entry.duplicate" => "Исключения прокси не должны повторяться.",
        "applications.id.required" => "У приложения должен быть ID.",
        "applications.displayName.required" => "У приложения должно быть отображаемое название.",
        "applications.packagePath.required" => "Укажите путь приложения внутри пакета.",
        "applications.packagePath.unsafe" => "Путь должен находиться внутри пакета и не содержать переходов вверх.",
        "applications.externalManual.path.forbidden" => "Для ручной установки путь внутри пакета не используется.",
        "applications.arguments.invalid" => "Проверьте аргументы приложения.",
        _ => error.Message,
    };

    private ProfileSettingsEdit CreateProfileSettingsEdit(ProvisioningProfile original) => new(
        ProfileNameTextBox.Text,
        ProfileDescriptionTextBox.Text,
        GetSelectedEditions(),
        GetSelectedTag(UiLanguageComboBox, original.Windows.Locale.UiLanguage),
        ProfileEditorController.RequiredInputLocales,
        GetSelectedTag(SystemLocaleComboBox, original.Windows.Locale.SystemLocale),
        GetSelectedTag(UserLocaleComboBox, original.Windows.Locale.UserLocale),
        GetSelectedTag(TimeZoneComboBox, original.Windows.TimeZone),
        OfflineInitialSetupCheckBox.IsChecked == true,
        GetOptionalBoolean(HideWirelessSetupComboBox),
        GetOptionalBoolean(HideOnlineAccountComboBox),
        GetPrivacyPreference(),
        ComputerNamePrefixTextBox.Text,
        GetSelectedEnum(ProxyModeComboBox, original.Machine.Proxy.Mode),
        GetSelectedEnum(DomainModeComboBox, original.Domain.Mode),
        GetSelectedEnum(LaunchModeComboBox, original.Deployment.LaunchMode),
        GetSelectedEnum(CleanupModeComboBox, original.Cleanup.ProvisioningAccount),
        GetSelectedEnum(NetworkModeComboBox, original.Machine.Network.Mode),
        StaticIpv4AddressTextBox.Text,
        StaticIpv4SubnetMaskTextBox.Text,
        StaticIpv4DefaultGatewayTextBox.Text,
        StaticIpv4DnsServersTextBox.Text,
        ProxyBypassListTextBox.Text);

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
        SetSelectedTag(UiLanguageComboBox, windows?.Locale.UiLanguage);
        SetSelectedTag(SystemLocaleComboBox, windows?.Locale.SystemLocale);
        SetSelectedTag(UserLocaleComboBox, windows?.Locale.UserLocale);
        SetSelectedTag(TimeZoneComboBox, windows?.TimeZone);
        OfflineInitialSetupCheckBox.IsChecked = windows?.Oobe.OfflineInitialSetup == true;
        SetOptionalBoolean(HideWirelessSetupComboBox, windows?.Oobe.HideWirelessSetup);
        SetOptionalBoolean(HideOnlineAccountComboBox, windows?.Oobe.HideOnlineAccountScreens);
        SetPrivacyPreference(windows?.Privacy);
        ComputerNamePrefixTextBox.Text = profile?.Machine.ComputerName.Prefix ?? string.Empty;
        SetSelectedEnum(NetworkModeComboBox, profile?.Machine.Network.Mode);
        StaticIpv4AddressTextBox.Text = profile?.Machine.Network.StaticIpv4?.Address ?? string.Empty;
        StaticIpv4SubnetMaskTextBox.Text = profile?.Machine.Network.StaticIpv4?.SubnetMask ?? string.Empty;
        StaticIpv4DefaultGatewayTextBox.Text = profile?.Machine.Network.StaticIpv4?.DefaultGateway ?? string.Empty;
        StaticIpv4DnsServersTextBox.Text = profile?.Machine.Network.StaticIpv4 is { } staticIpv4
            ? string.Join(", ", staticIpv4.DnsServers)
            : string.Empty;
        SetSelectedEnum(ProxyModeComboBox, profile?.Machine.Proxy.Mode);
        ProxyBypassListTextBox.Text = profile is null
            ? string.Empty
            : string.Join(", ", profile.Machine.Proxy.BypassList ?? []);
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

    private static string GetSelectedTag(ComboBox comboBox, string fallback) =>
        (comboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? fallback;

    private static void SetSelectedTag(ComboBox comboBox, string? value)
    {
        comboBox.SelectedItem = comboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), value, StringComparison.Ordinal));
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

    public string RevisionLabel => $"v{Profile.Revision}";

    public string Detail
    {
        get
        {
            var editions = string.Join(" + ", Profile.Windows.SupportedEditions.Select(static edition => edition switch
            {
                WindowsEdition.Professional => "Pro",
                WindowsEdition.Enterprise => "Enterprise",
                _ => edition.ToString(),
            }));
            var language = Profile.Windows.Locale.UiLanguage == "ru-RU" ? "Русский" : "English";
            var timeZone = Profile.Windows.TimeZone switch
            {
                "West Asia Standard Time" => "UTC+05",
                "Central Asia Standard Time" => "UTC+06",
                "Russian Standard Time" => "UTC+03",
                var value => value,
            };
            return string.Join(" · ", editions, language, timeZone);
        }
    }
}

public static class ProfileListFilter
{
    public static bool Matches(ProfileListItem item, string? query)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        var normalizedQuery = query.Trim();
        return item.Name.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase)
            || (item.Profile.Metadata.Description?.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase) ?? false)
            || item.Detail.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase);
    }
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
