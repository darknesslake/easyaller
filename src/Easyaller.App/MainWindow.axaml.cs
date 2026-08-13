using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Easyaller.Core.Profiles;
using Easyaller.Core.Provisioning;
using Easyaller.Deployment;

namespace Easyaller.App;

public enum EditorTab
{
    Profile,
    Action,
    Verify,
}

public sealed partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly FileProfileRepository _repository;
    private readonly ProfileImportExportService _profileImportExportService;
    private readonly ProfileEditorController _profileEditorController;
    private readonly ProvisioningPlanBuilder _planBuilder = new();
    private readonly RuntimeProvisioningInputValidator _inputValidator = new();
    private readonly DeploymentPreparationController _deploymentController = new();
    private readonly ProvisioningExecutionService _executionService = new(
        new WindowsProvisioningSystemAdapter(),
        new FileProvisioningExecutionStateStore(),
        new WindowsProvisioningResumeLauncher());
    private readonly List<ProfileListItem> _allProfiles = [];
    private readonly ObservableCollection<ProfileListItem> _profiles = [];
    private readonly ObservableCollection<ApplicationListItem> _applications = [];
    private readonly ObservableCollection<InstructionListItem> _instructions = [];
    private readonly ObservableCollection<string> _dnsServers = [];
    private ProfileListItem? _selectedProfile;
    private byte[]? _pendingImportSource;
    private ProvisioningProfile? _pendingExportProfile;
    private ProvisioningPlan? _plan;
    private DeploymentDryRun? _deploymentDryRun;
    private const string ComputerNameSitePrefix = "URA01";

    /// <summary>
    /// Windows names the first wired adapter "Ethernet" by default, which covers the common case.
    /// It is only a starting value: the operator can edit it, and applying still needs confirmation.
    /// </summary>
    private const string DefaultNetworkAdapterName = "Ethernet";
    private readonly CurrentMachineInspector _machineInspector = new();
    private readonly InstalledApplicationInspector _installedApplicationInspector = new();
    private readonly ObservableCollection<ComplianceCheckListItem> _complianceChecks = [];
    private readonly ProfileComplianceChecker _complianceChecker = new();
    private readonly ProvisioningJournal _journal = new();
    private readonly RuntimeProfileEligibilityService _eligibilityService = new();
    private readonly PrivacyConfigurationService _privacyService = new();
    private readonly ApplicationInstallationService _applicationInstaller = new();
    private readonly DesktopShortcutService _desktopShortcutService = new();
    private readonly MaintenanceSettingsStore _maintenanceSettingsStore = MaintenanceSettingsStore.CreateDefault();
    private readonly OutlookArchiveService _outlookArchiveService = new();
    private ApplicationInstallPlan? _applicationInstallPlan;
    private ComplianceReport? _complianceReport;
    private string? _selectedIsoPath;
    private EditorTab _activeTab = EditorTab.Profile;
    private bool _isPrepareInstallMode;
    private IReadOnlyList<string> _shortcutPreview = [];
    private StandardOutlookFolders? _standardOutlookFolders;
    private IReadOnlyList<OutlookArchivePreview>? _outlookArchivePreviews;
    private string _selectedProfileName = "Выберите профиль";
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
        MigrateLegacyMachineWideProfiles(_repository.RootDirectory);
        EmbeddedProfileInstaller.InstallIfMissing(_repository);
        _profileImportExportService = new ProfileImportExportService(_repository);
        _profileEditorController = new ProfileEditorController(_repository);
        ProfilesList.ItemsSource = _profiles;
        ApplicationsList.ItemsSource = _applications;
        InstructionsList.ItemsSource = _instructions;
        DnsServersList.ItemsSource = _dnsServers;
        ComplianceList.ItemsSource = _complianceChecks;
        RefreshMaintenanceUsers();
        InitializeShortcutSource();
        InitializeOutlookArchivePath();
        InitializeDeploymentTargetDefaults();
        StoragePathText.Text = _repository.RootDirectory;
        AttachEditorChangeHandlers();
        SetMode(prepareInstallMode: false);
        RefreshProfiles();
    }

    /// <summary>
    /// Preselects the deployment target from the currently running Windows, since that is the
    /// common case. The operator can still change it when preparing a package for a different
    /// edition, version, or build than the PC Easyaller happens to be running on.
    /// </summary>
    private void InitializeDeploymentTargetDefaults()
    {
        DeploymentEditionComboBox.SelectedIndex = 0;
        DeploymentVersionComboBox.SelectedIndex = 0;
        DeploymentBuildTextBox.Text = "26100";

        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var detection = new WindowsRuntimeInfoProvider().Detect();
        if (!detection.IsDetected)
        {
            return;
        }

        var runtime = detection.Runtime!;
        if (runtime.Edition is RuntimeWindowsEdition.Professional or RuntimeWindowsEdition.Enterprise)
        {
            SetSelectedTag(DeploymentEditionComboBox, runtime.Edition.ToString());
        }

        if (DeploymentVersionComboBox.Items.OfType<ComboBoxItem>()
            .Any(item => string.Equals(item.Tag?.ToString(), runtime.DisplayVersion, StringComparison.OrdinalIgnoreCase)))
        {
            SetSelectedTag(DeploymentVersionComboBox, runtime.DisplayVersion);
        }

        DeploymentBuildTextBox.Text = runtime.Build.ToString(System.Globalization.CultureInfo.InvariantCulture);
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
    public bool HasNoDnsServers => _dnsServers.Count == 0;

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

    private void SelectThisPcMode_Click(object? sender, RoutedEventArgs e) => SetMode(prepareInstallMode: false);

    private void SelectNewInstallMode_Click(object? sender, RoutedEventArgs e) => SetMode(prepareInstallMode: true);

    private void SelectMaintenanceMode_Click(object? sender, RoutedEventArgs e)
    {
        if (!CanLeaveCurrentProfile())
        {
            return;
        }

        SetActiveNavClass(ThisPcModeButton, false);
        SetActiveNavClass(NewInstallModeButton, false);
        SetActiveNavClass(MaintenanceModeButton, true);
        ProfileWorkspaceGrid.IsVisible = false;
        MaintenanceWorkspace.IsVisible = true;
        RefreshMaintenanceUsers();
        UpdateShortcutPreview();
        SetStatus("Открыто обслуживание ПК. Эти действия не изменяют и не используют профиль настройки Windows.");
    }

    private void SetMode(bool prepareInstallMode)
    {
        _isPrepareInstallMode = prepareInstallMode;
        SetActiveNavClass(ThisPcModeButton, !prepareInstallMode);
        SetActiveNavClass(NewInstallModeButton, prepareInstallMode);
        SetActiveNavClass(MaintenanceModeButton, false);
        ProfileWorkspaceGrid.IsVisible = true;
        MaintenanceWorkspace.IsVisible = false;
        ApplyTabButton.Content = prepareInstallMode ? "USB Install" : "Применить на этом ПК";

        // Answer-file settings only reach a Windows Setup run, so they stay hidden while configuring a live PC.
        InstallOnlyWindowsSettings.IsVisible = prepareInstallMode;
        InstallOnlyLaunchSettings.IsVisible = prepareInstallMode;
        InstallOnlyCleanupSettings.IsVisible = prepareInstallMode;
        DomainSectionTitleText.Text = prepareInstallMode ? "Домен и запуск настройки" : "Присоединение к домену";
        DomainSectionNoteText.Text = prepareInstallMode
            ? "Эти правила определяют, когда Easyaller запускается и требуется ли присоединение к домену. Пароль всегда вводится позднее и не сохраняется."
            : "Профиль хранит домен и учётную запись, под которой выполняется присоединение. Пароль вводится только для действия ниже и не сохраняется.";
        WindowsSectionTitleText.Text = prepareInstallMode ? "Windows и первый запуск" : "Часовой пояс и конфиденциальность";
        WindowsSectionNoteText.Text = prepareInstallMode
            ? "Это стандарт для устанавливаемой Windows. Он не меняет уже работающий компьютер."
            : "К уже установленной Windows из этого раздела применяются часовой пояс и параметры конфиденциальности. Языки, редакции и экраны первичной настройки задаются файлом ответов и видны в режиме «New USB Install».";
        SetActiveTab(_activeTab);
    }

    private void InitializeShortcutSource()
    {
        var savedSource = _maintenanceSettingsStore.LoadShortcutSource();
        ShortcutSourceTextBox.Text = savedSource ?? string.Empty;
    }

    private static string GetUsersRoot()
    {
        var currentProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Directory.GetParent(currentProfile)?.FullName
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System).Substring(0, 3), "Users");
    }

    private void RefreshMaintenanceUsers_Click(object? sender, RoutedEventArgs e) => RefreshMaintenanceUsers();

    private void RefreshMaintenanceUsers()
    {
        var comboBox = MaintenanceUserComboBox;
        if (comboBox is null)
        {
            return;
        }

        var currentName = (comboBox.SelectedItem as LocalWindowsUser)?.Name;
        var users = _desktopShortcutService.GetUsers(GetUsersRoot());
        comboBox.ItemsSource = users;
        comboBox.SelectedItem = users.FirstOrDefault(user =>
                string.Equals(user.Name, currentName, StringComparison.OrdinalIgnoreCase))
            ?? users.FirstOrDefault(user =>
                string.Equals(user.ProfileDirectory, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), StringComparison.OrdinalIgnoreCase))
            ?? users.FirstOrDefault();
        UpdateShortcutTarget();
    }

    private void MaintenanceUserComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        UpdateShortcutTarget();
        UpdateShortcutPreview();
    }

    private void UpdateShortcutTarget()
    {
        ShortcutTargetDesktopTextBox.Text = (MaintenanceUserComboBox.SelectedItem as LocalWindowsUser)?.DesktopDirectory ?? string.Empty;
    }

    private async void ChooseShortcutSource_Click(object? sender, RoutedEventArgs e)
    {
        if (!StorageProvider.CanOpen)
        {
            SetStatus("Выбор папки недоступен на этой платформе.");
            return;
        }

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Выберите папку «Ярлыки»",
            AllowMultiple = false,
        });
        if (folders.Count == 0)
        {
            return;
        }

        ShortcutSourceTextBox.Text = folders[0].Path.LocalPath;
        _maintenanceSettingsStore.SaveShortcutSource(folders[0].Path.LocalPath);
        UpdateShortcutPreview();
    }

    private void PreviewShortcuts_Click(object? sender, RoutedEventArgs e) => UpdateShortcutPreview(showStatus: true);

    private void UpdateShortcutPreview(bool showStatus = false)
    {
        var source = ShortcutSourceTextBox?.Text?.Trim() ?? string.Empty;
        var user = MaintenanceUserComboBox?.SelectedItem as LocalWindowsUser;
        _shortcutPreview = _desktopShortcutService.Discover(source);
        CopyShortcutsButton.IsEnabled = user is not null && _shortcutPreview.Count > 0;

        if (user is null)
        {
            ShortcutPreviewText.Text = "Выберите пользователя Windows.";
        }
        else if (!Directory.Exists(source))
        {
            if (ShortcutSourceTextBox is not null)
            {
                ShortcutSourceTextBox.Text = string.Empty;
            }
            _maintenanceSettingsStore.Clear();
            ShortcutPreviewText.Text = "Выберите существующую папку «Ярлыки».";
        }
        else if (_shortcutPreview.Count == 0)
        {
            ShortcutPreviewText.Text = "Поддерживаемые ярлыки не найдены. Ожидаются файлы .lnk, .url или .website.";
        }
        else
        {
            var names = _shortcutPreview.Select(Path.GetFileName).Take(12).ToArray();
            var remainder = _shortcutPreview.Count - names.Length;
            ShortcutPreviewText.Text = $"Будет скопировано пользователю «{user.Name}»: {_shortcutPreview.Count}.\n"
                + string.Join("\n", names.Select(static name => "• " + name))
                + (remainder > 0 ? $"\n…и ещё {remainder}" : string.Empty);
        }

        if (showStatus)
        {
            SetStatus(ShortcutPreviewText.Text ?? string.Empty);
        }
    }

    private async void CopyShortcuts_Click(object? sender, RoutedEventArgs e)
    {
        var user = MaintenanceUserComboBox.SelectedItem as LocalWindowsUser;
        var source = ShortcutSourceTextBox.Text?.Trim() ?? string.Empty;
        UpdateShortcutPreview();
        if (user is null || _shortcutPreview.Count == 0)
        {
            SetStatus("Сначала выберите пользователя, папку и проверьте список ярлыков.");
            return;
        }

        var replace = (ShortcutConflictComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "Replace";
        var confirmed = await ConfirmActionWindow.AskAsync(
            this,
            "Скопировать ярлыки на рабочий стол?",
            $"Пользователь: {user.Name}\nРабочий стол: {user.DesktopDirectory}\nЯрлыков: {_shortcutPreview.Count}",
            replace ? "Совпадающие ярлыки будут заменены." : "Совпадающие ярлыки останутся без изменений.",
            "Скопировать",
            "Будут изменены файлы рабочего стола выбранного пользователя.");
        if (!confirmed)
        {
            SetStatus("Копирование ярлыков отменено.");
            return;
        }

        var result = await Task.Run(() => _desktopShortcutService.Copy(
            source,
            user.DesktopDirectory,
            replace ? ShortcutConflictBehavior.Replace : ShortcutConflictBehavior.Skip));
        var message = $"Ярлыки обработаны для «{user.Name}»: добавлено {result.Copied}, заменено {result.Replaced}, пропущено {result.Skipped}.";
        if (result.Errors.Count > 0)
        {
            message += "\nОшибки:\n" + string.Join("\n", result.Errors.Select(static error => "• " + error));
        }

        SetStatus(message);
        ShortcutPreviewText.Text = message;
    }

    private async void LoadOutlookFolders_Click(object? sender, RoutedEventArgs e)
    {
        if (!_outlookArchiveService.IsAvailable)
        {
            OutlookArchivePreviewText.Text = "Классический Microsoft Outlook не найден. Новый Outlook не предоставляет совместимый интерфейс автоматизации.";
            SetStatus(OutlookArchivePreviewText.Text);
            return;
        }

        LoadOutlookFoldersButton.IsEnabled = false;
        OutlookArchivePreviewText.Text = "Outlook читает список почтовых папок…";
        try
        {
            _standardOutlookFolders = await Task.Run(_outlookArchiveService.GetStandardMailFolders);
            OutlookStandardFoldersText.Text = $"{_standardOutlookFolders.Inbox.Name} и {_standardOutlookFolders.SentItems.Name}";
            OutlookArchivePreviewText.Text = "Стандартные папки подключены. Выберите срок, файл PST и посчитайте письма.";
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException or PlatformNotSupportedException)
        {
            OutlookArchivePreviewText.Text = "Не удалось прочитать Outlook: " + exception.Message;
        }
        finally
        {
            LoadOutlookFoldersButton.IsEnabled = true;
            ResetOutlookArchivePreview();
        }
    }

    private void InitializeOutlookArchivePath()
    {
        OutlookArchivePathTextBox.Text = OutlookArchiveService.GetDefaultArchivePath(
            DateTime.Today,
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
    }

    private async void ChooseOutlookArchivePath_Click(object? sender, RoutedEventArgs e)
    {
        if (!StorageProvider.CanSave)
        {
            SetStatus("Выбор файла недоступен на этой платформе.");
            return;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Выберите файл архива Outlook",
            SuggestedFileName = $"{DateTime.Today:dd.MM.yyyy}.pst",
            DefaultExtension = "pst",
            ShowOverwritePrompt = false,
            FileTypeChoices =
            [
                new FilePickerFileType("Файл данных Outlook") { Patterns = ["*.pst"] },
            ],
        });
        if (file is null)
        {
            return;
        }

        OutlookArchivePathTextBox.Text = file.Path.LocalPath;
        ResetOutlookArchivePreview();
    }

    private void OutlookArchiveInput_Changed(object? sender, SelectionChangedEventArgs e) => ResetOutlookArchivePreview();

    private void ResetOutlookArchivePreview()
    {
        _outlookArchivePreviews = null;
        var archiveAge = GetOutlookArchiveAge();
        if (RunOutlookArchiveButton is not null)
        {
            RunOutlookArchiveButton.IsEnabled = false;
        }

        if (PreviewOutlookArchiveButton is not null)
        {
            PreviewOutlookArchiveButton.IsEnabled = _standardOutlookFolders is not null;
        }
    }

    private async void PreviewOutlookArchive_Click(object? sender, RoutedEventArgs e)
    {
        if (_standardOutlookFolders is null)
        {
            SetStatus("Сначала подключите Outlook.");
            return;
        }

        var cutoff = OutlookArchiveService.CalculateCutoff(DateTime.Now, GetOutlookArchiveAge());
        PreviewOutlookArchiveButton.IsEnabled = false;
        RunOutlookArchiveButton.IsEnabled = false;
        OutlookArchivePreviewText.Text = "Подсчитываются письма. Outlook и почта не изменяются…";
        try
        {
            var standardFolders = _standardOutlookFolders;
            _outlookArchivePreviews = await Task.Run(() => standardFolders.All
                .Select(folder => _outlookArchiveService.Preview(folder, cutoff))
                .ToArray());
            var inboxPreview = _outlookArchivePreviews[0];
            var sentPreview = _outlookArchivePreviews[1];
            var periodText = GetOutlookArchiveAge() == OutlookArchiveAge.AllTime
                ? "Письма за всё время"
                : $"Письма старше {cutoff:dd.MM.yyyy}";
            OutlookArchivePreviewText.Text = $"{periodText}:\n"
                + $"• {standardFolders.Inbox.Name}: {inboxPreview.MatchingMessages}\n"
                + $"• {standardFolders.SentItems.Name}: {sentPreview.MatchingMessages}\n"
                + $"Всего: {_outlookArchivePreviews.Sum(static preview => preview.MatchingMessages)}.";
            RunOutlookArchiveButton.IsEnabled = _outlookArchivePreviews.Sum(static preview => preview.MatchingMessages) > 0
                && IsValidOutlookArchivePath(OutlookArchivePathTextBox.Text);
            SetStatus(OutlookArchivePreviewText.Text);
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException or PlatformNotSupportedException)
        {
            OutlookArchivePreviewText.Text = "Не удалось проверить Outlook: " + exception.Message;
            SetStatus(OutlookArchivePreviewText.Text);
        }
        finally
        {
            PreviewOutlookArchiveButton.IsEnabled = true;
        }
    }

    private async void RunOutlookArchive_Click(object? sender, RoutedEventArgs e)
    {
        if (_outlookArchivePreviews is null || _standardOutlookFolders is null)
        {
            SetStatus("Сначала выполните актуальный подсчёт писем.");
            return;
        }

        var archivePath = OutlookArchivePathTextBox.Text?.Trim() ?? string.Empty;
        if (!IsValidOutlookArchivePath(archivePath))
        {
            SetStatus("Выберите файл архива с расширением .pst.");
            return;
        }

        var confirmed = await ConfirmActionWindow.AskAsync(
            this,
            "Переместить письма в архив Outlook?",
            $"Папки: {_standardOutlookFolders.Inbox.Name} и {_standardOutlookFolders.SentItems.Name}\n"
                + $"Писем: {_outlookArchivePreviews.Sum(static preview => preview.MatchingMessages)}\n"
                + $"Старше: {_outlookArchivePreviews[0].OlderThan:dd.MM.yyyy}\nАрхив: {archivePath}",
            "Письма будут перемещены из исходной папки в PST. Операцию нельзя отменить одной кнопкой Easyaller.",
            "Переместить в PST",
            "Это действие изменит почтовый ящик текущего пользователя.");
        if (!confirmed)
        {
            SetStatus("Архивация Outlook отменена.");
            return;
        }

        RunOutlookArchiveButton.IsEnabled = false;
        PreviewOutlookArchiveButton.IsEnabled = false;
        OutlookArchivePreviewText.Text = "Outlook перемещает письма в PST. Не закрывайте Outlook и Easyaller…";
        try
        {
            var previews = _outlookArchivePreviews;
            var standardFolders = _standardOutlookFolders;
            var results = await Task.Run(() => new[]
            {
                _outlookArchiveService.Archive(standardFolders.Inbox, previews[0].OlderThan, archivePath, "Входящие"),
                _outlookArchiveService.Archive(standardFolders.SentItems, previews[0].OlderThan, archivePath, "Отправленные"),
            });
            var moved = results.Sum(static result => result.MovedMessages);
            var errors = results.SelectMany(static result => result.Errors).ToArray();
            var message = $"Архивация Outlook завершена: перемещено {moved}. PST: {archivePath}";
            if (errors.Length > 0)
            {
                message += $"\nОшибок: {errors.Length}.\n" + string.Join("\n", errors.Take(10));
            }

            OutlookArchivePreviewText.Text = message;
            SetStatus(message);
            _outlookArchivePreviews = null;
        }
        catch (Exception exception) when (exception is COMException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            OutlookArchivePreviewText.Text = "Архивация Outlook остановлена: " + exception.Message;
            SetStatus(OutlookArchivePreviewText.Text);
        }
        finally
        {
            PreviewOutlookArchiveButton.IsEnabled = true;
        }
    }

    private OutlookArchiveAge GetOutlookArchiveAge() =>
        Enum.TryParse<OutlookArchiveAge>(
            (OutlookAgeComboBox?.SelectedItem as ComboBoxItem)?.Tag?.ToString(),
            out var age)
            ? age
            : OutlookArchiveAge.AllTime;

    private static bool IsValidOutlookArchivePath(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && string.Equals(Path.GetExtension(path), ".pst", StringComparison.OrdinalIgnoreCase);

    private void ShowEditTab_Click(object? sender, RoutedEventArgs e) => SetActiveTab(EditorTab.Profile);

    private void ShowApplyTab_Click(object? sender, RoutedEventArgs e) => SetActiveTab(EditorTab.Action);

    private void ShowVerifyTab_Click(object? sender, RoutedEventArgs e) => SetActiveTab(EditorTab.Verify);

    private void SetActiveTab(EditorTab tab)
    {
        // Verification only makes sense for a running Windows, so it disappears in install-preparation mode.
        if (tab == EditorTab.Verify && _isPrepareInstallMode)
        {
            tab = EditorTab.Action;
        }

        _activeTab = tab;
        SetActiveNavClass(EditTabButton, tab == EditorTab.Profile);
        SetActiveNavClass(ApplyTabButton, tab == EditorTab.Action);
        SetActiveNavClass(VerifyTabButton, tab == EditorTab.Verify);
        VerifyTabButton.IsVisible = !_isPrepareInstallMode;

        var showEditor = tab == EditorTab.Profile;
        var showPrepare = tab == EditorTab.Action && _isPrepareInstallMode;
        var showApply = tab == EditorTab.Action && !_isPrepareInstallMode;
        var showVerify = tab == EditorTab.Verify;

        ProfileEditorScrollViewer.IsVisible = showEditor;
        PrepareInstallScrollViewer.IsVisible = showPrepare;
        ApplyTabScrollViewer.IsVisible = showApply;
        VerifyTabScrollViewer.IsVisible = showVerify;
        PrepareBottomBar.IsVisible = showPrepare;
        ApplyBottomBar.IsVisible = showApply;
        VerifyBottomBar.IsVisible = showVerify;
    }

    private static void SetActiveNavClass(Button button, bool isActive)
    {
        if (isActive)
        {
            if (!button.Classes.Contains("active"))
            {
                button.Classes.Add("active");
            }
        }
        else
        {
            button.Classes.Remove("active");
        }
    }

    private void OpenUsbCreator_Click(object? sender, RoutedEventArgs e) =>
        new UsbCreatorWindow(new UsbCreationController(), _selectedIsoPath).Show(this);

    /// <summary>
    /// Right-click actions on a list row may target a profile other than the one open in the
    /// editor, so both handlers select it first — reusing the normal selection guard that blocks
    /// switching away from unsaved changes — before delegating to the existing action.
    /// </summary>
    private void ProfileListItemClone_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: ProfileListItem item })
        {
            return;
        }

        ProfilesList.SelectedItem = item;
        if (_selectedProfile?.Profile.ProfileId == item.Profile.ProfileId)
        {
            CloneProfile_Click(sender, e);
        }
    }

    private void ProfileListItemDelete_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: ProfileListItem item })
        {
            return;
        }

        ProfilesList.SelectedItem = item;
        if (_selectedProfile?.Profile.ProfileId == item.Profile.ProfileId)
        {
            DeleteProfile_Click(sender, e);
        }
    }

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

    private async void ApplyTimeZoneToCurrentPc_Click(object? sender, RoutedEventArgs e)
    {
        if (_selectedProfile is null)
        {
            SetStatus("Сначала выберите профиль.");
            return;
        }

        var timeZone = GetSelectedTag(TimeZoneComboBox, _selectedProfile.Profile.Windows.TimeZone);
        ApplyTimeZoneToCurrentPcButton.IsEnabled = false;
        try
        {
            SetStatus($"Применяется только часовой пояс Windows: {timeZone}.");
            var result = await Task.Run(() => new WindowsProvisioningSystemAdapter().SetTimeZone(timeZone));
            SetStatus(result.IsSuccess
                ? $"Часовой пояс Windows установлен и проверен: {timeZone}. Другие настройки не изменялись."
                : "Не удалось применить часовой пояс Windows: " + (result.ErrorCode ?? "неизвестная ошибка") + ".");
        }
        finally
        {
            ApplyTimeZoneToCurrentPcButton.IsEnabled = true;
        }
    }

    private async void ApplyComputerNameToCurrentPc_Click(object? sender, RoutedEventArgs e)
    {
        var number = ComputerNameNumberTextBox.Text?.Trim() ?? string.Empty;
        var computerName = GetComputerNamePrefix() + number;
        if (number.Length is < 2 or > 3 || !number.All(char.IsAsciiDigit) || computerName.Length > 15)
        {
            SetStatus("Введите 2 или 3 цифры; итоговое имя не должно превышать 15 символов.");
            return;
        }

        if (!await ConfirmActionWindow.AskAsync(
            this,
            "Переименовать этот компьютер?",
            $"Текущее имя «{Environment.MachineName}» будет заменено на «{computerName}».",
            "Потребуется перезагрузка Windows. Если компьютер состоит в домене, переименование может нарушить доверительные отношения.",
            "Переименовать"))
        {
            SetStatus("Переименование отменено. Компьютер не изменён.");
            return;
        }

        ApplyComputerNameToCurrentPcButton.IsEnabled = false;
        try
        {
            var result = await Task.Run(() => new WindowsProvisioningSystemAdapter().RenameComputer(computerName));
            SetStatus(result.IsSuccess ? $"Имя компьютера изменено на {computerName}. Перезагрузите Windows для завершения." : "Не удалось изменить имя компьютера: " + (result.ErrorCode ?? "неизвестная ошибка") + ".");
            WriteJournalEntry("Быстрое действие: имя компьютера", result.IsSuccess ? "Применено" : "Ошибка", [computerName]);
        }
        finally { ApplyComputerNameToCurrentPcButton.IsEnabled = true; }
    }

    private async void ApplyProxyToCurrentPc_Click(object? sender, RoutedEventArgs e)
    {
        var address = ProxyAddressTextBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(address))
        {
            SetStatus("Введите адрес WinHTTP-прокси перед применением.");
            return;
        }

        var bypassList = (ProxyBypassListTextBox.Text ?? string.Empty)
            .Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        ApplyProxyToCurrentPcButton.IsEnabled = false;
        try
        {
            var result = await Task.Run(() => new WindowsProvisioningSystemAdapter().SetWinHttpProxy(address, bypassList));
            SetStatus(result.IsSuccess
                ? "WinHTTP-прокси применён только на этом ПК."
                : "Не удалось применить WinHTTP-прокси: " + (result.ErrorCode ?? "неизвестная ошибка") + ".");
        }
        finally
        {
            RefreshEditorFeedback();
        }
    }

    private async void ApplyStaticIpv4ToCurrentPc_Click(object? sender, RoutedEventArgs e)
    {
        var adapterId = StaticIpv4AdapterIdTextBox.Text?.Trim();
        var configuration = new StaticIpv4Configuration(
            StaticIpv4AddressTextBox.Text?.Trim() ?? string.Empty,
            StaticIpv4SubnetMaskTextBox.Text?.Trim() ?? string.Empty,
            StaticIpv4DefaultGatewayTextBox.Text?.Trim() ?? string.Empty,
            _dnsServers.ToArray(),
            adapterId);
        if (string.IsNullOrWhiteSpace(adapterId))
        {
            SetStatus("Укажите сетевой адаптер, к которому применить настройки.");
            return;
        }

        if (string.IsNullOrWhiteSpace(configuration.Address)
            || ProvisioningProfileValidator.GetPrefixLength(configuration.SubnetMask) is null)
        {
            SetStatus("Укажите корректные IPv4-адрес и маску подсети. Шлюз и DNS-серверы указывать необязательно.");
            return;
        }

        if (!await ConfirmActionWindow.AskAsync(
            this,
            "Изменить сеть этого компьютера?",
            $"Адаптеру «{adapterId}» будет назначен адрес {configuration.Address} с маской {configuration.SubnetMask}.",
            "DHCP на этом адаптере будет отключён, а его текущие адреса заменены. Если вы подключены удалённо через этот адаптер, соединение оборвётся.",
            "Применить сеть"))
        {
            SetStatus("Изменение сети отменено. Адаптер не изменён.");
            return;
        }

        ApplyStaticIpv4ToCurrentPcButton.IsEnabled = false;
        try
        {
            var result = await Task.Run(() => new WindowsProvisioningSystemAdapter().ConfigureStaticIpv4(adapterId, configuration));
            SetStatus(result.IsSuccess
                ? "Сеть применена к выбранному адаптеру."
                    + (string.IsNullOrWhiteSpace(configuration.DefaultGateway) ? " Шлюз не задавался." : string.Empty)
                    + (configuration.DnsServers.Count == 0 ? " DNS-серверы адаптера не изменялись." : string.Empty)
                : "Не удалось применить сеть: " + (result.ErrorCode ?? "неизвестная ошибка") + ".");
            WriteJournalEntry(
                "Быстрое действие: статический IPv4",
                result.IsSuccess ? "Применено" : "Ошибка",
                [$"{adapterId}: {configuration.Address}/{configuration.SubnetMask}"]);
        }
        finally { ApplyStaticIpv4ToCurrentPcButton.IsEnabled = true; }
    }

    private async void RunComplianceCheck_Click(object? sender, RoutedEventArgs e)
    {
        if (_selectedProfile is null)
        {
            SetStatus("Сначала выберите профиль, с которым нужно сверить компьютер.");
            return;
        }

        RunComplianceCheckButton.IsEnabled = false;
        try
        {
            if (await ReadCurrentMachineAsync() is not { } snapshot)
            {
                return;
            }

            InstalledSoftwareSnapshot? installedSoftware = null;
            if (_selectedProfile.Profile.Applications.Count > 0)
            {
                SetStatus("Читается список установленных программ и ярлыков…");
                // Only applications that declare a reference footprint need their folder measured.
                var measureFor = _selectedProfile.Profile.Applications
                    .Where(static application => application.Footprint is not null)
                    .Select(static application => application.DisplayName)
                    .ToArray();
                installedSoftware = await Task.Run(() => _installedApplicationInspector.Read()?.ToCoreSnapshot(measureFor));
            }

            var report = _complianceChecker.Check(
                _selectedProfile.Profile,
                snapshot.ToMachineState(),
                DateTimeOffset.Now,
                installedSoftware);
            ShowComplianceReport(report);
        }
        finally
        {
            RunComplianceCheckButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Records what happened locally. Runtime-only values - passwords, the proxy address, and
    /// domain credentials - never reach the journal.
    /// </summary>
    private void WriteJournalEntry(string action, string outcome, IReadOnlyList<string> details)
    {
        if (_selectedProfile is null)
        {
            return;
        }

        try
        {
            _journal.Append(new JournalEntry(
                DateTimeOffset.Now,
                Environment.MachineName,
                _selectedProfile.Profile.ProfileId,
                _selectedProfile.Profile.Revision,
                _selectedProfile.Profile.Metadata.Name,
                action,
                outcome,
                details));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A journal failure must never hide or block the operation the operator just ran.
            SetStatus(StatusText.Text + " Журнал записать не удалось.");
        }
    }

    private void ShowComplianceReport(ComplianceReport report)
    {
        _complianceReport = report;
        _complianceChecks.Clear();
        foreach (var check in report.Checks)
        {
            _complianceChecks.Add(new ComplianceCheckListItem(check));
        }

        ComplianceSummaryPanel.IsVisible = true;
        ComplianceEmptyText.IsVisible = false;
        SaveComplianceReportButton.IsEnabled = true;

        ComplianceSummaryText.Text = report.IsCompliant
            ? "Компьютер соответствует профилю."
            : report.MismatchCount > 0
                ? $"Расхождений с профилем: {report.MismatchCount}."
                : "Часть значений прочитать не удалось.";
        ComplianceSummaryText.Foreground = report.IsCompliant
            ? Brushes.LightGreen
            : report.MismatchCount > 0
                ? Brushes.Salmon
                : Brushes.Khaki;
        ComplianceSummaryDetailText.Text =
            $"Совпало: {report.MatchCount} · Расходится: {report.MismatchCount} · Не прочитано: {report.UnknownCount} · "
            + $"Профиль «{report.ProfileName}», версия {report.ProfileRevision}, проверено {report.CheckedUtc:dd.MM.yyyy HH:mm}.";

        SetStatus(report.IsCompliant
            ? "Сверка завершена: расхождений нет. Windows не изменялась."
            : $"Сверка завершена: расхождений {report.MismatchCount}. Windows не изменялась.");
        WriteJournalEntry(
            "Проверка соответствия",
            report.IsCompliant ? "Соответствует" : "Расхождения",
            report.Checks
                .Where(static check => check.Status == ComplianceStatus.Mismatch)
                .Select(static check => $"{check.Title}: ожидалось {check.Expected}, фактически {check.Actual}")
                .ToArray());
    }

    private async void SaveComplianceReport_Click(object? sender, RoutedEventArgs e)
    {
        if (_complianceReport is not { } report)
        {
            SetStatus("Сначала выполните проверку.");
            return;
        }

        if (!StorageProvider.CanSave)
        {
            SetStatus("Сохранение файлов недоступно на этой платформе.");
            return;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Сохранить отчёт проверки",
            SuggestedFileName = $"easyaller-check-{DateTime.Now:yyyyMMdd-HHmm}.txt",
            DefaultExtension = "txt",
        });
        if (file is null)
        {
            SetStatus("Сохранение отчёта отменено.");
            return;
        }

        try
        {
            await File.WriteAllTextAsync(file.Path.LocalPath, BuildComplianceReportText(report));
            SetStatus($"Отчёт сохранён: {file.Path.LocalPath}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SetStatus("Не удалось сохранить отчёт: " + exception.Message);
        }
    }

    private static string BuildComplianceReportText(ComplianceReport report)
    {
        var text = new StringBuilder();
        text.AppendLine("Easyaller — отчёт проверки соответствия профилю");
        text.AppendLine($"Профиль: {report.ProfileName} (версия {report.ProfileRevision})");
        text.AppendLine($"Идентификатор профиля: {report.ProfileId}");
        text.AppendLine($"Проверено: {report.CheckedUtc:yyyy-MM-dd HH:mm:ss zzz}");
        text.AppendLine($"Итог: совпало {report.MatchCount}, расходится {report.MismatchCount}, не прочитано {report.UnknownCount}");
        text.AppendLine();
        foreach (var check in report.Checks)
        {
            text.AppendLine($"[{DescribeComplianceStatus(check.Status)}] {check.Title}");
            text.AppendLine($"    ожидается: {check.Expected}");
            text.AppendLine($"    фактически: {check.Actual}");
        }

        text.AppendLine();
        text.AppendLine("Проверка выполняется только чтением. Windows не изменялась. Пароли не читаются и не сохраняются.");
        return text.ToString();
    }

    private static string DescribeComplianceStatus(ComplianceStatus status) => status switch
    {
        ComplianceStatus.Match => "СОВПАДАЕТ",
        ComplianceStatus.Mismatch => "РАСХОЖДЕНИЕ",
        ComplianceStatus.Unknown => "НЕ ПРОЧИТАНО",
        _ => "НЕ ЗАДАНО",
    };

    private async Task<CurrentMachineSnapshot?> ReadCurrentMachineAsync()
    {
        SetStatus("Чтение текущих настроек Windows…");
        var snapshot = await Task.Run(_machineInspector.Read);
        if (snapshot is null)
        {
            SetStatus("Не удалось прочитать настройки этого компьютера. На не-Windows платформе это ожидаемо.");
        }

        return snapshot;
    }

    private async void TakeNetworkFromCurrentPc_Click(object? sender, RoutedEventArgs e)
    {
        if (await ReadCurrentMachineAsync() is not { } snapshot)
        {
            return;
        }

        SetStatus(TakeNetworkFromSnapshot(snapshot)
            ? $"Сеть взята с этого ПК — {snapshot.DescribeNetwork()}. Проверьте значения и сохраните профиль."
            : "Активный адаптер с IPv4-адресом не найден, брать нечего.");
    }

    private async void TakeComputerNameFromCurrentPc_Click(object? sender, RoutedEventArgs e)
    {
        if (await ReadCurrentMachineAsync() is not { } snapshot)
        {
            return;
        }

        if (!TakeComputerNameFromSnapshot(snapshot))
        {
            SetStatus("Имя этого компьютера прочитать не удалось.");
            return;
        }

        var name = snapshot.ComputerName.Trim();
        SetStatus(name.StartsWith(ComputerNameSitePrefix, StringComparison.OrdinalIgnoreCase)
            ? $"Имя взято с этого ПК: {name}. Проверьте шаблон и сохраните профиль."
            : $"Имя взято с этого ПК: {name}. Оно не начинается с {ComputerNameSitePrefix}, поэтому записано как свой вариант.");
    }

    private async void TakeDomainFromCurrentPc_Click(object? sender, RoutedEventArgs e)
    {
        if (await ReadCurrentMachineAsync() is not { } snapshot)
        {
            return;
        }

        SetStatus(TakeDomainFromSnapshot(snapshot)
            ? $"Домен взят с этого ПК: {snapshot.Domain}. Учётные данные не читались и не сохраняются."
            : "Этот компьютер не состоит в домене, брать нечего.");
    }

    private async void TakeProxyFromCurrentPc_Click(object? sender, RoutedEventArgs e)
    {
        if (await ReadCurrentMachineAsync() is not { } snapshot)
        {
            return;
        }

        SetStatus(TakeProxyFromSnapshot(snapshot)
            ? $"WinHTTP-прокси взят с этого ПК: {snapshot.ProxyAddress}. Проверьте значения и сохраните профиль."
            : "WinHTTP-прокси на этом ПК не настроен, брать нечего.");
    }

    private async void TakeEverythingFromCurrentPc_Click(object? sender, RoutedEventArgs e)
    {
        if (await ReadCurrentMachineAsync() is not { } snapshot)
        {
            return;
        }

        var taken = new List<string>();
        if (TakeComputerNameFromSnapshot(snapshot)) taken.Add("имя компьютера");
        if (TakeNetworkFromSnapshot(snapshot)) taken.Add("сеть");
        if (TakeDomainFromSnapshot(snapshot)) taken.Add("домен");
        if (TakeProxyFromSnapshot(snapshot)) taken.Add("прокси");

        SetStatus(taken.Count == 0
            ? "С этого компьютера нечего взять: активная сеть, домен и прокси не найдены."
            : $"Перенесено в профиль: {string.Join(", ", taken)}. Проверьте разделы и сохраните профиль.");
    }

    private bool TakeNetworkFromSnapshot(CurrentMachineSnapshot snapshot)
    {
        if (!snapshot.HasNetwork)
        {
            return false;
        }

        SetSelectedEnum<NetworkConfigurationMode>(NetworkModeComboBox, NetworkConfigurationMode.StaticIpv4);
        StaticIpv4AdapterIdTextBox.Text = snapshot.AdapterId;
        StaticIpv4AddressTextBox.Text = snapshot.Address;
        StaticIpv4SubnetMaskTextBox.Text = snapshot.SubnetMask;
        StaticIpv4DefaultGatewayTextBox.Text = snapshot.DefaultGateway;
        _dnsServers.Clear();
        foreach (var dnsServer in snapshot.DnsServers)
        {
            _dnsServers.Add(dnsServer);
        }

        return true;
    }

    private bool TakeComputerNameFromSnapshot(CurrentMachineSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot.ComputerName))
        {
            return false;
        }

        SetComputerNameTemplateFromFullName(snapshot.ComputerName.Trim());
        return true;
    }

    private bool TakeDomainFromSnapshot(CurrentMachineSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot.Domain))
        {
            return false;
        }

        DomainNameTextBox.Text = snapshot.Domain;
        if (GetSelectedEnum(DomainModeComboBox, DomainMode.NotConfigured) == DomainMode.NotConfigured)
        {
            SetSelectedEnum<DomainMode>(DomainModeComboBox, DomainMode.Optional);
        }

        return true;
    }

    private bool TakeProxyFromSnapshot(CurrentMachineSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot.ProxyAddress))
        {
            return false;
        }

        SetSelectedEnum<ProxyConfigurationMode>(ProxyModeComboBox, ProxyConfigurationMode.PromptAtRuntime);
        ProxyAddressTextBox.Text = snapshot.ProxyAddress;
        ProxyBypassListTextBox.Text = snapshot.ProxyBypassList;
        return true;
    }

    /// <summary>Splits a full Windows computer name into the profile's template prefix and its trailing number.</summary>
    private void SetComputerNameTemplateFromFullName(string computerName)
    {
        var prefixLength = computerName.Length;
        while (prefixLength > 0 && char.IsAsciiDigit(computerName[prefixLength - 1]))
        {
            prefixLength--;
        }

        PopulateComputerNameTemplate(computerName[..prefixLength]);
        ComputerNameNumberTextBox.Text = computerName[prefixLength..];
    }

    private async void FillRuntimeFromCurrentPc_Click(object? sender, RoutedEventArgs e)
    {
        if (await ReadCurrentMachineAsync() is not { } snapshot)
        {
            return;
        }

        RuntimeComputerNameTextBox.Text = snapshot.ComputerName;
        RuntimeNetworkAdapterTextBox.Text = snapshot.AdapterId;
        RuntimeProxyAddressTextBox.Text = snapshot.ProxyAddress;
        RuntimeDomainNameTextBox.Text = snapshot.Domain;
        SetStatus("Поля заполнены текущими значениями этого ПК. Пароль домена нужно ввести вручную.");
    }

    private void FillRuntimeFromProfile_Click(object? sender, RoutedEventArgs e)
    {
        if (_selectedProfile is null)
        {
            SetStatus("Сначала выберите профиль.");
            return;
        }

        var profile = _selectedProfile.Profile;
        RuntimeNetworkAdapterTextBox.Text = DefaultIfBlank(profile.Machine.Network.StaticIpv4?.AdapterId, DefaultNetworkAdapterName);
        RuntimeProxyAddressTextBox.Text = profile.Machine.Proxy.Address ?? string.Empty;
        RuntimeDomainNameTextBox.Text = profile.Domain.DomainName ?? string.Empty;
        RuntimeDomainUserNameTextBox.Text = profile.Domain.UserName ?? string.Empty;

        // The profile keeps only the name prefix, so the operator still adds this machine's number.
        var prefix = profile.Machine.ComputerName.Prefix;
        if (!string.IsNullOrWhiteSpace(prefix))
        {
            RuntimeComputerNameTextBox.Text = prefix;
        }

        SetStatus(string.IsNullOrWhiteSpace(prefix)
            ? "Поля заполнены значениями профиля. Имя компьютера и пароль домена вводятся вручную."
            : $"Поля заполнены значениями профиля. К имени «{prefix}» допишите 2–3 цифры этой машины. Пароль домена вводится вручную.");
    }

    private async void ApplyDomainToCurrentPc_Click(object? sender, RoutedEventArgs e)
    {
        var domainName = DomainNameTextBox.Text?.Trim();
        var userName = DomainUserNameTextBox.Text?.Trim();
        var password = DomainPasswordTextBox.Text;
        if (string.IsNullOrWhiteSpace(domainName) || string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(password))
        {
            SetStatus("Для домена укажите имя домена, учётную запись и пароль.");
            return;
        }

        if (!await ConfirmActionWindow.AskAsync(
            this,
            "Присоединить этот компьютер к домену?",
            $"Компьютер «{Environment.MachineName}» будет присоединён к домену «{domainName}» под учётной записью «{userName}».",
            "Членство в домене меняет вход, политики и права на этой машине. Для завершения потребуется перезагрузка. Пароль используется только для этой операции и не сохраняется.",
            "Присоединить"))
        {
            SetStatus("Присоединение к домену отменено. Компьютер не изменён.");
            return;
        }

        using var credential = new RuntimeDomainCredential(userName, password.AsSpan());
        DomainPasswordTextBox.Text = string.Empty;
        ApplyDomainToCurrentPcButton.IsEnabled = false;
        try
        {
            var result = await Task.Run(() => new WindowsProvisioningSystemAdapter().JoinDomain(domainName, credential));
            SetStatus(result.IsSuccess ? "Компьютер присоединён к домену. Для завершения потребуется перезагрузка." : "Не удалось присоединить к домену: " + (result.ErrorCode ?? "неизвестная ошибка") + ".");
            WriteJournalEntry("Быстрое действие: домен", result.IsSuccess ? "Применено" : "Ошибка", [domainName]);
        }
        finally { RefreshEditorFeedback(); }
    }

    /// <summary>
    /// Shows the exact name the quick action would set, so the template and the typed number
    /// are checked before Windows is renamed rather than after.
    /// </summary>
    private void UpdateComputerNamePreview()
    {
        var number = ComputerNameNumberTextBox.Text?.Trim() ?? string.Empty;
        if (number.Length == 0)
        {
            ComputerNamePreviewText.Text = "«Применить» переименовывает этот компьютер прямо сейчас и требует перезагрузку.";
            return;
        }

        var computerName = GetComputerNamePrefix() + number;
        ComputerNamePreviewText.Text = number.Length is < 2 or > 3 || !number.All(char.IsAsciiDigit)
            ? "Номер должен состоять из 2 или 3 цифр."
            : computerName.Length > 15
                ? $"«{computerName}» — {computerName.Length} символов, Windows допускает не более 15."
                : $"Итоговое имя: {computerName}. Переименование требует перезагрузки.";
    }

    private static string DefaultIfBlank(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;

    private static string DescribeProfileDescription(string? description) =>
        string.IsNullOrWhiteSpace(description)
            ? "Описание не указано — двойной клик, чтобы добавить."
            : description;

    private void ProfileNameDisplayText_DoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (_selectedProfile is null)
        {
            return;
        }

        ProfileNameDisplayText.IsVisible = false;
        ProfileNameTextBox.IsVisible = true;
        ProfileNameTextBox.Focus();
        ProfileNameTextBox.SelectAll();
    }

    private void ProfileNameTextBox_LostFocus(object? sender, RoutedEventArgs e)
    {
        ProfileNameTextBox.IsVisible = false;
        ProfileNameDisplayText.IsVisible = true;
    }

    private void ProfileNameTextBox_KeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key is Avalonia.Input.Key.Enter or Avalonia.Input.Key.Escape)
        {
            ProfileNameTextBox.IsVisible = false;
            ProfileNameDisplayText.IsVisible = true;
            e.Handled = true;
        }
    }

    private void ProfileDescriptionDisplayText_DoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (_selectedProfile is null)
        {
            return;
        }

        ProfileDescriptionDisplayText.IsVisible = false;
        ProfileDescriptionTextBox.IsVisible = true;
        ProfileDescriptionTextBox.Focus();
        ProfileDescriptionTextBox.SelectAll();
    }

    private void ProfileDescriptionTextBox_LostFocus(object? sender, RoutedEventArgs e)
    {
        ProfileDescriptionTextBox.IsVisible = false;
        ProfileDescriptionDisplayText.IsVisible = true;
    }

    private void ProfileDescriptionTextBox_KeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        // Enter must stay a newline here: the description is multi-line, unlike the single-line name.
        if (e.Key == Avalonia.Input.Key.Escape)
        {
            ProfileDescriptionTextBox.IsVisible = false;
            ProfileDescriptionDisplayText.IsVisible = true;
            e.Handled = true;
        }
    }

    private void ResetProfile_Click(object? sender, RoutedEventArgs e)
    {
        if (_selectedProfile is null)
        {
            return;
        }

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

    private async void DeleteProfile_Click(object? sender, RoutedEventArgs e)
    {
        if (_selectedProfile is null || !CanLeaveCurrentProfile())
        {
            return;
        }

        var selectedProfile = _selectedProfile;
        if (!await ConfirmActionWindow.AskAsync(
            this,
            "Удалить профиль?",
            $"Профиль «{selectedProfile.Name}» будет удалён с этого компьютера.",
            "Локальная резервная копия останется на диске, но восстановить профиль через приложение нельзя — только вручную из файла резервной копии.",
            "Удалить",
            "Профиль будет удалён локально."))
        {
            return;
        }

        var result = _repository.Delete(selectedProfile.Profile.ProfileId, selectedProfile.Profile.Revision);
        if (result.Status != ProfileRepositoryStatus.Success)
        {
            SetStatus($"Не удалось удалить профиль: {GetMessage(result.Errors)}");
            return;
        }

        RefreshProfiles();
        SetStatus($"Профиль «{selectedProfile.Name}» удалён. Локальная резервная копия сохранена.");
    }

    /// <summary>
    /// Fills the application list from one folder: the operator points at the software share and
    /// the installers found inside become profile entries.
    /// </summary>
    private async void ScanApplicationFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (_selectedProfile is null)
        {
            SetStatus("Сначала выберите профиль.");
            return;
        }

        if (!StorageProvider.CanOpen)
        {
            SetStatus("Выбор папки недоступен на этой платформе.");
            return;
        }

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Выберите папку с установщиками",
            AllowMultiple = false,
        });
        if (folders.FirstOrDefault()?.Path.LocalPath is not { } folder)
        {
            return;
        }

        var sourceFolder = Path.TrimEndingDirectorySeparator(folder);
        var discovered = await Task.Run(() => ApplicationInstallationService.DiscoverInstallers(sourceFolder));
        if (discovered.Count == 0)
        {
            SetStatus("В этой папке не найдено установщиков .exe или .msi.");
            return;
        }

        var currentSource = ApplicationSourcePathTextBox.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(currentSource)
            && !string.Equals(Path.TrimEndingDirectorySeparator(currentSource), sourceFolder, StringComparison.OrdinalIgnoreCase)
            && !await ConfirmActionWindow.AskAsync(
                this,
                "Заменить папку с установщиками?",
                $"Сейчас в профиле указано «{currentSource}», а выбрана «{sourceFolder}».",
                "Профиль хранит одну папку. Уже добавленные приложения будут искаться в новой папке.",
                "Заменить"))
        {
            SetStatus("Сбор отменён. Папка в профиле не изменена.");
            return;
        }

        ApplicationSourcePathTextBox.Text = sourceFolder;

        // Reading a signature out of every file is disk I/O, so it runs off the UI thread —
        // otherwise adding a dozen large installers would visibly freeze the window.
        SetStatus("Определяются типы установщиков…");
        var detections = await Task.Run(() => discovered
            .Select(installer => InstallerFrameworkDetector.Detect(Path.Combine(sourceFolder, installer.RelativePath)))
            .ToArray());

        var added = 0;
        var skipped = 0;
        var detected = 0;
        for (var index = 0; index < discovered.Count; index++)
        {
            var installer = discovered[index];
            var id = CreateApplicationId(installer.SuggestedName);
            if (_applications.Any(item => string.Equals(item.Application.Id, id, StringComparison.OrdinalIgnoreCase)))
            {
                skipped++;
                continue;
            }

            var detection = detections[index];
            if (detection.SuggestedArguments.Count > 0)
            {
                detected++;
            }

            _applications.Add(new ApplicationListItem(new ApplicationProfile(
                id,
                installer.SuggestedName,
                ApplicationSourceKind.PackageRelative,
                installer.RelativePath,
                detection.SuggestedArguments,
                Architecture: DetectArchitecture(installer.RelativePath))));
            added++;
        }

        SetStatus($"Найдено установщиков: {discovered.Count}, добавлено: {added}"
            + (skipped > 0 ? $", уже были в списке: {skipped}" : string.Empty)
            + (detected > 0 ? $". Для {detected} по сигнатуре файла подставлены вероятные тихие ключи — это эвристика, проверьте каждый." : ". Тип установщика не распознан ни у одного файла — задайте тихие ключи вручную.")
            + " Проверьте разрядность и сохраните профиль.");
    }

    /// <summary>
    /// Adds installers by picking the files themselves: the display name, id and package path all
    /// come from the file, and the folder they live in becomes the profile's installer source.
    /// </summary>
    private async void AddApplicationFiles_Click(object? sender, RoutedEventArgs e)
    {
        if (_selectedProfile is null)
        {
            SetStatus("Сначала выберите профиль.");
            return;
        }

        if (!StorageProvider.CanOpen)
        {
            SetStatus("Выбор файлов недоступен на этой платформе.");
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Выберите установщики (.exe, .msi)",
            AllowMultiple = true,
            FileTypeFilter = [InstallerFileType],
        });
        if (files.Count == 0)
        {
            return;
        }

        var paths = files.Select(static file => file.Path.LocalPath).ToArray();
        var currentSource = ApplicationSourcePathTextBox.Text?.Trim();

        // Installers often live in subfolders (Office has its own), so the source folder is the
        // common root of everything selected and the stored path keeps the subfolder.
        string sourceFolder;
        if (!string.IsNullOrWhiteSpace(currentSource) && paths.All(path => IsInsideFolder(path, currentSource)))
        {
            sourceFolder = Path.TrimEndingDirectorySeparator(currentSource);
        }
        else
        {
            sourceFolder = GetCommonRoot(paths);
            if (string.IsNullOrEmpty(sourceFolder))
            {
                SetStatus("У выбранных файлов нет общей папки. Выберите установщики из одного каталога.");
                return;
            }

            if (!string.IsNullOrWhiteSpace(currentSource)
                && !string.Equals(Path.TrimEndingDirectorySeparator(currentSource), sourceFolder, StringComparison.OrdinalIgnoreCase)
                && !await ConfirmActionWindow.AskAsync(
                    this,
                    "Заменить папку с установщиками?",
                    $"Сейчас в профиле указано «{currentSource}», а выбранные файлы лежат в «{sourceFolder}».",
                    "Профиль хранит одну папку. Уже добавленные приложения будут искаться в новой папке.",
                    "Заменить"))
            {
                SetStatus("Добавление отменено. Папка в профиле не изменена.");
                return;
            }
        }

        ApplicationSourcePathTextBox.Text = sourceFolder;

        SetStatus("Определяются типы установщиков…");
        var detections = await Task.Run(() => paths.Select(InstallerFrameworkDetector.Detect).ToArray());

        var added = 0;
        var detected = 0;
        var skipped = new List<string>();
        for (var index = 0; index < paths.Length; index++)
        {
            var path = paths[index];
            var relativePath = Path.GetRelativePath(sourceFolder, path);
            var displayName = GetInstallerDisplayName(sourceFolder, path);
            var id = CreateApplicationId(displayName);
            if (_applications.Any(item => string.Equals(item.Application.Id, id, StringComparison.OrdinalIgnoreCase)))
            {
                skipped.Add(Path.GetFileName(path));
                continue;
            }

            var detection = detections[index];
            if (detection.SuggestedArguments.Count > 0)
            {
                detected++;
            }

            _applications.Add(new ApplicationListItem(new ApplicationProfile(
                id,
                displayName,
                ApplicationSourceKind.PackageRelative,
                relativePath,
                detection.SuggestedArguments,
                Architecture: DetectArchitecture(relativePath))));
            added++;
        }

        var suggestionNote = detected > 0
            ? $" Для {detected} по сигнатуре файла подставлены вероятные тихие ключи — это эвристика, проверьте каждый."
            : string.Empty;
        SetStatus(skipped.Count == 0
            ? $"Добавлено установщиков: {added}.{suggestionNote} Сохраните профиль."
            : $"Добавлено: {added}.{suggestionNote} Пропущены как уже добавленные: {string.Join(", ", skipped)}.");
    }

    /// <summary>
    /// Records what each listed application looks like when correctly installed on this machine,
    /// so a later check can spot a folder that lost files.
    /// </summary>
    private async void CaptureApplicationFootprint_Click(object? sender, RoutedEventArgs e)
    {
        if (_applications.Count == 0)
        {
            SetStatus("Сначала добавьте приложения в список.");
            return;
        }

        CaptureFootprintButton.IsEnabled = false;
        try
        {
            SetStatus("Измеряются папки установки. Это может занять время…");
            var names = _applications.Select(static item => item.Application.DisplayName).ToArray();
            var snapshot = await Task.Run(() => _installedApplicationInspector.Read()?.ToCoreSnapshot(names));
            if (snapshot is null)
            {
                SetStatus("Не удалось прочитать список установленных программ.");
                return;
            }

            var captured = 0;
            var notInstalled = new List<string>();
            var noLocation = new List<string>();
            for (var index = 0; index < _applications.Count; index++)
            {
                var application = _applications[index].Application;
                var match = snapshot.Applications.FirstOrDefault(entry =>
                    entry.DisplayName.Contains(application.DisplayName, StringComparison.OrdinalIgnoreCase)
                    || application.DisplayName.Contains(entry.DisplayName, StringComparison.OrdinalIgnoreCase));
                if (match is null)
                {
                    notInstalled.Add(application.DisplayName);
                    continue;
                }

                if (match.FileCount == 0)
                {
                    // Plenty of installers never record an install location in the registry.
                    noLocation.Add(application.DisplayName);
                    continue;
                }

                _applications[index] = new ApplicationListItem(application with
                {
                    Footprint = new ApplicationFootprint(match.InstallLocation, match.SizeBytes, match.FileCount),
                });
                captured++;
            }

            var message = new StringBuilder($"Эталон записан для приложений: {captured}.");
            if (notInstalled.Count > 0)
            {
                message.Append($" Не установлены на этом ПК: {string.Join(", ", notInstalled)}.");
            }

            if (noLocation.Count > 0)
            {
                message.Append($" Папка установки не указана в реестре: {string.Join(", ", noLocation)}.");
            }

            if (captured > 0)
            {
                message.Append(" Сохраните профиль.");
            }

            SetStatus(message.ToString());
        }
        finally
        {
            CaptureFootprintButton.IsEnabled = true;
        }
    }

    public bool HasSelectedApplication => ApplicationsList?.SelectedItem is ApplicationListItem;

    /// <summary>
    /// Whether this process is elevated. Renaming, domain join, network changes and most installers
    /// need it, so the answer is shown up front instead of surfacing as a failure mid-run.
    /// </summary>
    public static bool IsRunningAsAdministrator
    {
        get
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public bool IsMissingAdministratorRights => OperatingSystem.IsWindows() && !IsRunningAsAdministrator;

    /// <summary>
    /// Restarts Easyaller elevated. Windows cannot add privileges to a running process, so the only
    /// documented route is a new process with the runas verb, which shows the standard UAC prompt.
    /// </summary>
    private async void RestartElevated_Click(object? sender, RoutedEventArgs e)
    {
        if (HasUnsavedChanges)
        {
            SetStatus("Сначала сохраните или сбросьте изменения профиля — перезапуск закроет это окно.");
            return;
        }

        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            SetStatus("Не удалось определить путь к программе для перезапуска.");
            return;
        }

        if (!await ConfirmActionWindow.AskAsync(
            this,
            "Перезапустить с правами администратора?",
            "Easyaller закроется и откроется заново с запросом прав.",
            "Windows покажет запрос контроля учётных записей. Несохранённые данные в полях применения будут потеряны.",
            "Перезапустить"))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(executablePath) { UseShellExecute = true, Verb = "runas" });
            Close();
        }
        catch (Win32Exception)
        {
            // The user dismissed the UAC prompt, or policy blocks elevation.
            SetStatus("Перезапуск с правами администратора отменён или запрещён политикой.");
        }
    }

    private void ApplicationsList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        RefreshApplicationQueueNumbers();
        OnPropertyChanged(nameof(HasSelectedApplication));
        if (ApplicationsList.SelectedItem is not ApplicationListItem selected)
        {
            return;
        }

        SelectedApplicationNameText.Text = "Название в отчётах и проверке";
        SelectedApplicationDisplayNameTextBox.Text = selected.Application.DisplayName;
        SetSelectedEnum<ApplicationArchitecture>(SelectedApplicationArchitectureComboBox, selected.Application.Architecture);
        SelectedApplicationArgumentsTextBox.Text = string.Join(Environment.NewLine, selected.Application.Arguments);
        SelectedApplicationPathText.Text = selected.Application.Footprint is { } footprint
            ? $"{selected.Application.PackageRelativePath} · эталон записан"
            : selected.Application.PackageRelativePath ?? string.Empty;
        _ = UpdateSelectedApplicationFrameworkHintAsync(selected.Application);
    }

    /// <summary>
    /// Re-reads the file's signature on selection so the hint stays correct even for an
    /// application added through the manual form, which never ran detection.
    /// </summary>
    private async Task UpdateSelectedApplicationFrameworkHintAsync(ApplicationProfile application)
    {
        SelectedApplicationFrameworkHintText.Text = string.Empty;
        var sourceFolder = ApplicationSourcePathTextBox.Text?.Trim();
        if (application.SourceKind != ApplicationSourceKind.PackageRelative
            || string.IsNullOrWhiteSpace(sourceFolder)
            || string.IsNullOrWhiteSpace(application.PackageRelativePath))
        {
            return;
        }

        var filePath = Path.Combine(sourceFolder, application.PackageRelativePath);
        if (!File.Exists(filePath))
        {
            return;
        }

        var detection = await Task.Run(() => InstallerFrameworkDetector.Detect(filePath));
        if (!ReferenceEquals(ApplicationsList.SelectedItem, null)
            && ApplicationsList.SelectedItem is ApplicationListItem current
            && current.Application.Id == application.Id
            && detection.SuggestedArguments.Count > 0)
        {
            SelectedApplicationFrameworkHintText.Text =
                $"По сигнатуре файла похоже на {detection.FrameworkName}. Обычный тихий ключ: {string.Join(" ", detection.SuggestedArguments)} — это эвристика, не гарантия.";
        }
    }

    /// <summary>
    /// Lets an operator set silent-install switches after adding installers by file, which is the
    /// only way the queue can run unattended.
    /// </summary>
    private void UpdateApplicationArguments_Click(object? sender, RoutedEventArgs e)
    {
        if (ApplicationsList.SelectedItem is not ApplicationListItem selected)
        {
            SetStatus("Выберите приложение в списке.");
            return;
        }

        var displayName = SelectedApplicationDisplayNameTextBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(displayName))
        {
            SetStatus("Название не может быть пустым — по нему приложение ищется при проверке.");
            return;
        }

        var index = _applications.IndexOf(selected);
        var arguments = (SelectedApplicationArgumentsTextBox.Text ?? string.Empty)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        _applications[index] = new ApplicationListItem(selected.Application with
        {
            DisplayName = displayName,
            Arguments = arguments,
            Architecture = GetSelectedEnum(SelectedApplicationArchitectureComboBox, selected.Application.Architecture),
        });
        ApplicationsList.SelectedItem = _applications[index];
        SetStatus(arguments.Length == 0
            ? $"«{displayName}» сохранено. Аргументов нет — установщик покажет окна и остановит очередь. Сохраните профиль."
            : $"«{displayName}» сохранено: аргументов {arguments.Length}. Не забудьте сохранить профиль.");
    }

    /// <summary>
    /// Guesses the target architecture from the installer path, which is how vendors normally mark
    /// it. The guess is only a starting value — it is shown in the editor and can be corrected.
    /// </summary>
    private static ApplicationArchitecture DetectArchitecture(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/').ToLowerInvariant();
        if (normalized.Contains("x86") || normalized.Contains("x32") || normalized.Contains("win32") || normalized.Contains("32bit"))
        {
            return ApplicationArchitecture.X86;
        }

        return normalized.Contains("x64") || normalized.Contains("amd64") || normalized.Contains("win64") || normalized.Contains("64bit")
            ? ApplicationArchitecture.X64
            : ApplicationArchitecture.Any;
    }

    private static bool IsInsideFolder(string path, string folder)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        return Path.GetFullPath(path).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Deepest folder that contains every selected file, so subfolders stay in the stored path.</summary>
    private static string GetCommonRoot(IReadOnlyList<string> paths)
    {
        var directories = paths
            .Select(static path => Path.GetDirectoryName(Path.GetFullPath(path)) ?? string.Empty)
            .Where(static directory => directory.Length > 0)
            .ToArray();
        if (directories.Length == 0)
        {
            return string.Empty;
        }

        var common = directories[0];
        foreach (var directory in directories.Skip(1))
        {
            while (!directory.Equals(common, StringComparison.OrdinalIgnoreCase)
                && !directory.StartsWith(common + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                var parent = Path.GetDirectoryName(common);
                if (string.IsNullOrEmpty(parent))
                {
                    return string.Empty;
                }

                common = parent;
            }
        }

        return Path.TrimEndingDirectorySeparator(common);
    }

    /// <summary>
    /// Names the entry after its subfolder when the file itself is unhelpful: a bare "setup.exe"
    /// tells an operator nothing, while the folder it sits in usually names the product.
    /// </summary>
    private static string GetInstallerDisplayName(string sourceFolder, string path)
    {
        var fileName = Path.GetFileNameWithoutExtension(path);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        var isGenericName = fileName.Equals("setup", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("install", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("installer", StringComparison.OrdinalIgnoreCase);
        if (!isGenericName || string.IsNullOrEmpty(directory))
        {
            return fileName;
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceFolder));
        return directory.Equals(root, StringComparison.OrdinalIgnoreCase)
            ? fileName
            : new DirectoryInfo(directory).Name;
    }

    /// <summary>Builds a stable profile id from a file name, keeping only characters the contract allows.</summary>
    private static string CreateApplicationId(string fileNameWithoutExtension)
    {
        var characters = fileNameWithoutExtension
            .Select(static character => char.IsAsciiLetterOrDigit(character) ? char.ToLowerInvariant(character) : '-')
            .ToArray();
        var id = new string(characters).Trim('-');
        while (id.Contains("--", StringComparison.Ordinal))
        {
            id = id.Replace("--", "-", StringComparison.Ordinal);
        }

        return string.IsNullOrEmpty(id) ? "app" : id;
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

    private void MoveApplicationUp_Click(object? sender, RoutedEventArgs e) => MoveSelectedApplication(-1);

    private void MoveApplicationDown_Click(object? sender, RoutedEventArgs e) => MoveSelectedApplication(1);

    private void MoveSelectedApplication(int offset)
    {
        if (ApplicationsList.SelectedItem is not ApplicationListItem selected)
        {
            return;
        }

        var currentIndex = _applications.IndexOf(selected);
        var targetIndex = currentIndex + offset;
        if (targetIndex < 0 || targetIndex >= _applications.Count)
        {
            SetStatus(offset < 0
                ? "Программа уже первая в очереди."
                : "Программа уже последняя в очереди.");
            return;
        }

        _applications.Move(currentIndex, targetIndex);
        RefreshApplicationQueueNumbers();
        ApplicationsList.SelectedItem = selected;
        ApplicationsList.ScrollIntoView(selected);
        SetStatus($"«{selected.DisplayName}» перемещено на позицию {targetIndex + 1}. Сохраните профиль.");
    }

    private void RefreshApplicationQueueNumbers()
    {
        for (var index = 0; index < _applications.Count; index++)
        {
            _applications[index].QueueNumber = index + 1;
        }
    }

    private void AddDnsServer_Click(object? sender, RoutedEventArgs e) => AddDnsServerFromInput();

    private void DnsServerInputTextBox_KeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key == Avalonia.Input.Key.Enter)
        {
            AddDnsServerFromInput();
            e.Handled = true;
        }
    }

    private void AddDnsServerFromInput()
    {
        var input = DnsServerInputTextBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(input))
        {
            return;
        }

        if (_dnsServers.Count >= 3)
        {
            SetStatus("Можно указать не более трёх DNS-серверов.");
            return;
        }

        if (!IPAddress.TryParse(input, out var parsed) || parsed.AddressFamily != AddressFamily.InterNetwork)
        {
            SetStatus("Введите корректный IPv4-адрес DNS, например 10.0.0.1.");
            return;
        }

        var address = parsed.ToString();
        if (_dnsServers.Contains(address, StringComparer.OrdinalIgnoreCase))
        {
            SetStatus("Такой DNS-адрес уже есть в списке.");
            return;
        }

        _dnsServers.Add(address);
        DnsServersList.SelectedItem = address;
        DnsServerInputTextBox.Text = string.Empty;
        SetStatus($"DNS-адрес добавлен ({_dnsServers.Count} из 3). Сохраните изменения профиля.");
    }

    private void DnsServersList_SelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        RemoveDnsServerButton.IsEnabled = DnsServersList.IsEnabled && DnsServersList.SelectedItem is not null;

    private void RemoveDnsServer_Click(object? sender, RoutedEventArgs e)
    {
        if (DnsServersList.SelectedItem is not string selected)
        {
            SetStatus("Выберите DNS-адрес для удаления.");
            return;
        }

        _dnsServers.Remove(selected);
        SetStatus("DNS-адрес удалён. Сохраните изменения профиля.");
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

    /// <summary>
    /// Marks each runtime-only field as required or skippable for the current profile's plan,
    /// instead of a separate list that just repeats the field names above the form.
    /// </summary>
    private void UpdateRuntimeFieldRequirementTags()
    {
        var hasProxyPrompt = _plan?.RuntimePrompts.Any(static prompt => prompt.Kind == RuntimePromptKind.ProxyConfiguration) == true;
        ProxyRuntimeTag.IsVisible = hasProxyPrompt;

        var domainPrompt = _plan?.RuntimePrompts.FirstOrDefault(static prompt => prompt.Kind == RuntimePromptKind.DomainJoin);
        DomainRuntimeTag.IsVisible = domainPrompt is not null;
        if (domainPrompt is not null)
        {
            DomainRuntimeTag.Classes.Set("requiredTag", domainPrompt.IsRequired);
            DomainRuntimeTag.Classes.Set("tag", !domainPrompt.IsRequired);
            DomainRuntimeTagText.Classes.Set("requiredTagText", domainPrompt.IsRequired);
            DomainRuntimeTagText.Classes.Set("tagText", !domainPrompt.IsRequired);
            DomainRuntimeTagText.Text = domainPrompt.IsRequired ? "Обязательно" : "Можно пропустить";
        }
    }

    private void RefreshApplyTab()
    {
        ClearDeploymentDryRun();
        if (_selectedProfile is null)
        {
            _plan = null;
            PlanSummaryText.Text = "Выберите профиль в списке слева.";
            TimeZoneActionText.Text = "Выберите профиль, чтобы увидеть его часовой пояс.";
            UpdateRuntimeFieldRequirementTags();
            ManualInstructionsPanel.IsVisible = false;
            UpdateApplyPlanSummary();
            return;
        }

        var result = _planBuilder.Create(_selectedProfile.Profile);
        if (!result.IsValid)
        {
            _plan = null;
            PlanSummaryText.Text = "Выбранный профиль содержит ошибки.";
            UpdateRuntimeFieldRequirementTags();
            UpdateApplyPlanSummary();
            return;
        }

        _plan = result.Plan;

        // Runtime fields that are already part of the saved profile must be ready to use
        // immediately. Only machine-specific suffixes and secrets still require input.
        var profile = _selectedProfile.Profile;
        RuntimeNetworkAdapterTextBox.Text = DefaultIfBlank(
            profile.Machine.Network.StaticIpv4?.AdapterId,
            DefaultNetworkAdapterName);
        RuntimeProxyAddressTextBox.Text = profile.Machine.Proxy.Address ?? string.Empty;
        RuntimeDomainNameTextBox.Text = profile.Domain.DomainName ?? string.Empty;
        RuntimeDomainUserNameTextBox.Text = profile.Domain.UserName ?? string.Empty;
        RuntimeComputerNameTextBox.Text = profile.Machine.ComputerName.Prefix ?? string.Empty;

        // The runtime field must show the profile's own naming rule, not an unrelated example.
        var namePrefix = _selectedProfile.Profile.Machine.ComputerName.Prefix;
        RuntimeComputerNameTextBox.PlaceholderText = string.IsNullOrWhiteSpace(namePrefix)
            ? "Имя компьютера"
            : $"{namePrefix} + 2–3 цифры, например {namePrefix}01";

        TimeZoneActionText.Text = $"Часовой пояс профиля: {result.Plan!.TimeZone}. Он будет применён вместе с остальными настройками профиля.";
        PlanSummaryText.Text = $"Запланировано шагов: {_plan!.Steps.Count}. Запросов при настройке: {_plan.RuntimePrompts.Count}.";
        UpdateRuntimeFieldRequirementTags();

        var instructions = _selectedProfile.Profile.Instructions
            .Select(static instruction => new InstructionListItem(instruction))
            .ToArray();
        ManualInstructionsList.ItemsSource = instructions;
        ManualInstructionsPanel.IsVisible = instructions.Length > 0;
        UpdateApplicationInstallPanel();

        UpdateApplyPlanSummary();
    }

    private void DeploymentEdition_SelectionChanged(object? sender, SelectionChangedEventArgs e) => ClearDeploymentDryRun();

    private void DeploymentVersion_SelectionChanged(object? sender, SelectionChangedEventArgs e) => ClearDeploymentDryRun();

    private void DeploymentBuild_TextChanged(object? sender, TextChangedEventArgs e) => ClearDeploymentDryRun();

    private async void ChooseTargetIso_Click(object? sender, RoutedEventArgs e)
    {
        if (!StorageProvider.CanOpen)
        {
            SetStatus("Выбор файла недоступен на этой платформе.");
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Выберите ISO Windows 11",
            AllowMultiple = false,
            FileTypeFilter = [WindowsIsoFileType],
        });
        var file = files.FirstOrDefault();
        if (file is null)
        {
            return;
        }

        var isoPath = file.Path.LocalPath;
        _selectedIsoPath = isoPath;
        TargetIsoPathTextBox.Text = isoPath;
        ClearDeploymentDryRun();
        TargetIsoStatusText.IsVisible = true;
        TargetIsoStatusText.Text = "Читается ISO — определяются редакции и сборка. Это может занять до минуты…";
        ChooseTargetIsoButton.IsEnabled = false;
        try
        {
            var readResult = await Task.Run(() => new WindowsIsoContentReader().Read(isoPath));
            if (!readResult.IsAvailable)
            {
                TargetIsoStatusText.Text = "Не удалось прочитать ISO: "
                    + (readResult.Errors.FirstOrDefault()?.Message ?? "неизвестная ошибка")
                    + ". Редакция, версия и сборка ниже не изменены.";
                return;
            }

            ApplyIsoTarget(readResult.Report!);
        }
        finally
        {
            ChooseTargetIsoButton.IsEnabled = true;
        }
    }

    private void ApplyIsoTarget(IsoContentReport report)
    {
        var amd64Images = report.Images
            .Where(static image => string.Equals(image.Architecture, "amd64", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        // Prefer an edition the selected profile actually allows, so the ISO and the profile do not
        // disagree only later, during the compatibility check.
        var allowedEditions = _selectedProfile?.Profile.Windows.SupportedEditions ?? [];
        var currentEditionTag = (DeploymentEditionComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        var selectedImage = amd64Images.FirstOrDefault(image => IsAllowedEdition(image.EditionId, allowedEditions)
                && string.Equals(image.EditionId, currentEditionTag, StringComparison.OrdinalIgnoreCase))
            ?? amd64Images.FirstOrDefault(image => IsAllowedEdition(image.EditionId, allowedEditions))
            ?? amd64Images.FirstOrDefault(image => string.Equals(image.EditionId, currentEditionTag, StringComparison.OrdinalIgnoreCase))
            ?? amd64Images.FirstOrDefault(static image => string.Equals(image.EditionId, "Enterprise", StringComparison.OrdinalIgnoreCase))
            ?? amd64Images.FirstOrDefault(static image => string.Equals(image.EditionId, "Professional", StringComparison.OrdinalIgnoreCase));

        if (selectedImage is null)
        {
            TargetIsoStatusText.Text = "В ISO не найдена поддерживаемая редакция Windows 11 Pro или Enterprise amd64. Редакция, версия и сборка ниже не изменены.";
            return;
        }

        SetSelectedTag(DeploymentEditionComboBox, selectedImage.EditionId);

        var editionCount = amd64Images.Count(image => string.Equals(image.EditionId, selectedImage.EditionId, StringComparison.OrdinalIgnoreCase));
        var summary = editionCount > 1
            ? $"В ISO несколько редакций amd64, выбрана {selectedImage.EditionId}."
            : $"Из ISO: {selectedImage.EditionId} amd64.";

        if (allowedEditions.Count > 0 && !IsAllowedEdition(selectedImage.EditionId, allowedEditions))
        {
            var allowedNames = string.Join(" или ", allowedEditions.Select(static edition =>
                edition == WindowsEdition.Professional ? "Pro" : "Enterprise"));
            summary += $" Профиль разрешает только {allowedNames}, поэтому проверка совместимости остановит сборку пакета —"
                + " отметьте нужную редакцию в разделе «Windows и первый запуск» или возьмите другой ISO.";
        }

        var buildNumber = ParseBuildNumber(selectedImage.Version);
        if (buildNumber is null)
        {
            TargetIsoStatusText.Text = summary + " Номер сборки не распознан из ISO, укажите вручную.";
            return;
        }

        DeploymentBuildTextBox.Text = buildNumber.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var matchingVersion = new Windows11CompatibilityCatalog().Entries
            .FirstOrDefault(entry => entry.BuildRange.Contains(buildNumber.Value))
            ?.DisplayVersion;
        if (matchingVersion is not null)
        {
            SetSelectedTag(DeploymentVersionComboBox, matchingVersion);
            TargetIsoStatusText.Text = $"{summary} Сборка {buildNumber.Value} ({matchingVersion}).";
        }
        else
        {
            TargetIsoStatusText.Text = $"{summary} Сборка {buildNumber.Value} — версия вне каталога 24H2/25H2, проверьте вручную.";
        }
    }

    private static bool IsAllowedEdition(string editionId, IReadOnlyList<WindowsEdition> allowedEditions) =>
        allowedEditions.Any(edition => string.Equals(
            edition == WindowsEdition.Professional ? "Professional" : "Enterprise",
            editionId,
            StringComparison.OrdinalIgnoreCase));

    private static int? ParseBuildNumber(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        var parts = version.Split('.');
        return parts.Length >= 3 && int.TryParse(parts[2], out var build) && build > 0 ? build : null;
    }

    private void PreviewDeployment_Click(object? sender, RoutedEventArgs e)
    {
        if (!TryGetDeploymentContext(out var profile, out var target))
        {
            return;
        }

        var result = _deploymentController.CreatePreview(profile, target);
        if (!result.IsValid)
        {
            DeploymentPreviewTextBox.Text = "Проверка заблокирована: " + GetMessage(result.Errors);
            SetStatus(DeploymentPreviewTextBox.Text);
            return;
        }

        DeploymentPreviewTextBox.Text = DescribePreview(result.Preview!, result.Warnings);
        SetStatus("Совместимость проверена. Windows и файлы не изменены.");
    }

    private void CreateDryRun_Click(object? sender, RoutedEventArgs e)
    {
        if (!TryGetDeploymentContext(out var profile, out var target))
        {
            return;
        }

        var result = _deploymentController.CreateDryRun(profile, target);
        if (!result.IsValid)
        {
            ClearDeploymentDryRun();
            DeploymentPreviewTextBox.Text = "Dry run заблокирован: " + GetMessage(result.Errors);
            SetStatus(DeploymentPreviewTextBox.Text);
            return;
        }

        _deploymentDryRun = result.DryRun;
        ExportDeploymentButton.IsEnabled = true;
        DeploymentPreviewTextBox.Text = DescribeDryRun(result.DryRun!, result.Warnings);
        SetStatus("Dry run готов в памяти. Файлы и Windows не изменены.");
    }

    private async void ExportDeploymentPackage_Click(object? sender, RoutedEventArgs e)
    {
        if (_deploymentDryRun is null || _selectedProfile is null)
        {
            SetStatus("Сначала создайте dry run для выбранного профиля.");
            return;
        }

        if (!StorageProvider.CanOpen)
        {
            SetStatus("Выбор папки недоступен на этой платформе.");
            return;
        }

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Выберите папку для нового пакета Easyaller",
            AllowMultiple = false,
        });
        var folder = folders.FirstOrDefault();
        if (folder is null)
        {
            SetStatus("Экспорт пакета отменён.");
            return;
        }

        var destination = Path.Combine(folder.Path.LocalPath, ToPackageDirectoryName(_selectedProfile.Name));
        var result = await _deploymentController.ExportAsync(_deploymentDryRun, destination);
        if (!result.IsSuccess)
        {
            SetStatus("Не удалось экспортировать пакет: " + GetMessage(result.Errors));
            return;
        }

        DeploymentPreviewTextBox.Text += $"\n\nПакет экспортирован: {result.DestinationDirectory}\nПроверено файлов: {result.Manifest!.Files.Count}.";
        SetStatus("Файловый пакет экспортирован. Перед использованием проверьте manifest и Windows SIM.");
    }

    private void ValidateRuntimeInputs_Click(object? sender, RoutedEventArgs e)
    {
        if (_plan is null)
        {
            SetStatus("Сначала выберите корректный профиль.");
            return;
        }

        if (!TryCreateRuntimeInputs(out var inputs))
        {
            return;
        }

        using (inputs)
        {
            var validation = _inputValidator.Validate(_plan, inputs);
            SetStatus(validation.IsValid
                ? "Введённые значения корректны. Ничего не изменено. Можно нажать «Применить к этому ПК»."
                : GetRuntimeMessage(validation.Errors[0].Code, validation.Errors[0].Message));
        }
    }

    private async void ApplyRuntimeInputs_Click(object? sender, RoutedEventArgs e)
    {
        if (_plan is null)
        {
            SetStatus("Сначала выберите корректный профиль.");
            return;
        }

        const string confirmation = ProvisioningExecutionService.ConfirmationPhrase;

        // Refuse a profile that targets a different Windows before touching the machine.
        var eligibility = await Task.Run(() => _eligibilityService.Evaluate(_selectedProfile!.Profile));
        if (!eligibility.CanApply)
        {
            ApplyResultTextBox.Text = $"Применение заблокировано.\n{eligibility.Summary}\n{eligibility.Reason}";
            SetStatus("Профиль не подходит этой Windows: " + eligibility.Reason);
            WriteJournalEntry("Применение профиля", "Заблокировано", [eligibility.Summary, eligibility.Reason]);
            return;
        }

        if (eligibility.State == ProfileEligibilityState.Warning && !await ConfirmActionWindow.AskAsync(
            this,
            "Совместимость не подтверждена",
            $"{eligibility.Summary}. {eligibility.Reason}",
            "Профиль всё равно будет применён к этому компьютеру. Продолжайте только если уверены, что настройки подходят.",
            "Всё равно применить"))
        {
            SetStatus("Применение отменено. Компьютер не изменён.");
            return;
        }

        // Renaming, domain join and network changes all need elevation. Warn before the first
        // change instead of failing partway and leaving the machine half configured.
        if (IsMissingAdministratorRights && !await ConfirmActionWindow.AskAsync(
            this,
            "Нет прав администратора",
            "Easyaller запущен обычным пользователем.",
            "Переименование компьютера, присоединение к домену и настройка сети остановятся с ошибкой. Часть шагов может успеть примениться, оставив компьютер настроенным наполовину. Лучше перезапустить с правами администратора.",
            "Всё равно продолжить"))
        {
            SetStatus("Применение отменено. Перезапустите Easyaller с правами администратора.");
            return;
        }

        if (!TryCreateRuntimeInputs(out var inputs))
        {
            return;
        }

        var plan = _plan;
        ApplyRuntimeInputsButton.IsEnabled = false;
        try
        {
            using (inputs)
            {
                SetStatus("Применение выполняется. Не закрывайте программу.");
                var report = new StringBuilder();
                var result = await Task.Run(() => _executionService.Execute(plan, inputs, confirmation));
                report.AppendLine(DescribeOperations(result.Operations));
                if (result.IsSuccess && eligibility.Runtime is { } runtime)
                {
                    report.AppendLine(await ApplyPrivacyPoliciesAsync(_selectedProfile!.Profile, runtime));
                }

                if (result.IsSuccess && _selectedProfile!.Profile.Applications.Any(static application =>
                        application.SourceKind == ApplicationSourceKind.PackageRelative))
                {
                    report.AppendLine(await InstallProfileApplicationsAsync(_selectedProfile.Profile));
                }

                report.AppendLine();
                report.Append(DescribeExecution(result));
                ApplyResultTextBox.Text = report.ToString();
                SetStatus(DescribeExecution(result));
                WriteJournalEntry(
                    "Применение профиля",
                    result.Status.ToString(),
                    result.Operations
                        .Select(static operation => DescribeOperationKind(operation.Kind)
                            + (operation.WasApplied ? ": применено" : ": проверено"))
                        .Concat(result.Errors.Select(static error => "ошибка: " + error.Code))
                        .ToArray());
            }
        }
        finally
        {
            ApplyRuntimeInputsButton.IsEnabled = true;
            RuntimeDomainPasswordTextBox.Text = string.Empty;
        }
    }

    private async Task<string> InstallProfileApplicationsAsync(ProvisioningProfile profile)
    {
        var installerRoot = ResolveInstallerRoot(profile.ApplicationSourcePath);
        InstallerRootTextBox.Text = installerRoot;
        var plan = _applicationInstaller.CreatePlan(profile, installerRoot);
        if (!plan.CanRun)
        {
            var reason = plan.Errors.FirstOrDefault()?.Message ?? "В профиле нет доступных установщиков.";
            return "Приложения не установлены: " + reason;
        }

        var destination = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            "Easyaller-Installers");
        var progress = new Progress<string>(SetStatus);
        var installReport = await _applicationInstaller.RunPipelinedAsync(
            plan,
            destination,
            new WindowsApplicationInstallerRunner(),
            progress);
        var details = DescribeInstallReport(installReport);
        ApplicationInstallResultTextBox.IsVisible = true;
        ApplicationInstallResultTextBox.Text = details + $"\n\nФайлы скопированы в: {destination}";
        WriteJournalEntry(
            "Установка приложений вместе с профилем",
            installReport.StoppedOnFailure ? "Остановлено на ошибке" : "Установлено",
            installReport.Results.Select(static item => $"{item.Step.DisplayName}: {DescribeInstallOutcome(item)}").ToArray());

        return installReport.StoppedOnFailure
            ? $"Приложения: установка остановлена на ошибке; установлено {installReport.InstalledCount} из {installReport.Results.Count}.\n{details}"
            : $"Приложения установлены: {installReport.InstalledCount}."
                + (installReport.RequiresRestart ? " Требуется перезагрузка." : string.Empty);
    }

    /// <summary>
    /// Applies the documented privacy policies after the main sequence. It is deliberately separate:
    /// hiding an OOBE page is not the same as setting a policy, and `notConfigured` must stay a no-op.
    /// </summary>
    private async Task<string> ApplyPrivacyPoliciesAsync(ProvisioningProfile profile, RuntimeWindowsInfo runtime)
    {
        if (!OperatingSystem.IsWindows())
        {
            return "Параметры конфиденциальности: доступны только в Windows.";
        }

        var target = new WindowsDeploymentTarget(
            runtime.Edition == RuntimeWindowsEdition.Enterprise ? WindowsEdition.Enterprise : WindowsEdition.Professional,
            WindowsArchitecture.Amd64,
            runtime.DisplayVersion,
            runtime.Build);

        var plan = _privacyService.CreatePlan(profile.Windows.Privacy, target);
        if (plan.Assignments.Count == 0)
        {
            return plan.Errors.Count > 0
                ? "Параметры конфиденциальности пропущены: " + plan.Errors[0].Message
                : "Параметры конфиденциальности: профиль ничего не меняет.";
        }

        // Created outside the lambda so the platform guard above stays visible to the analyzer.
        IPrivacyPolicyStore policyStore = new WindowsRegistryPrivacyPolicyStore();
        PrivacyConfigurationApplyResult result;
        try
        {
            result = await Task.Run(() => _privacyService.Apply(plan, policyStore));
        }
        catch (UnauthorizedAccessException)
        {
            return "Параметры конфиденциальности не применены: запустите Easyaller от имени администратора.";
        }
        catch (System.Security.SecurityException)
        {
            return "Параметры конфиденциальности не применены: Windows запретила изменение системных политик.";
        }
        catch (InvalidOperationException exception)
        {
            return "Параметры конфиденциальности не применены: " + exception.Message;
        }
        var verified = result.Verification.Count(static verification => verification.IsVerified);
        return result.IsApplied
            ? $"Параметры конфиденциальности применены и перечитаны: {verified} из {plan.Assignments.Count}."
            : "Параметры конфиденциальности не применены: " + (result.Errors.FirstOrDefault()?.Message ?? "неизвестная ошибка");
    }

    private async void ApplyPrivacyToCurrentPc_Click(object? sender, RoutedEventArgs e)
    {
        if (_selectedProfile is null)
        {
            SetStatus("Сначала выберите профиль.");
            return;
        }

        var selectedPreference = GetPrivacyPreference();
        var privacy = selectedPreference is { } preference
            ? new PrivacySettings(preference, preference, preference, preference, preference, preference, preference)
            : _selectedProfile.Profile.Windows.Privacy;
        var draft = _selectedProfile.Profile with
        {
            Windows = _selectedProfile.Profile.Windows with { Privacy = privacy },
        };
        var eligibility = _eligibilityService.Evaluate(draft);
        if (eligibility.Runtime is null)
        {
            SetStatus("Не удалось определить версию Windows для применения параметров конфиденциальности.");
            return;
        }

        ApplyPrivacyToCurrentPcButton.IsEnabled = false;
        try
        {
            var message = await ApplyPrivacyPoliciesAsync(draft, eligibility.Runtime);
            SetStatus(message);
            WriteJournalEntry("Быстрое действие: конфиденциальность", message.Contains("применены", StringComparison.OrdinalIgnoreCase) ? "Применено" : "Пропущено", [message]);
        }
        finally
        {
            ApplyPrivacyToCurrentPcButton.IsEnabled = true;
        }
    }

    private void UpdateApplicationInstallPanel()
    {
        _applicationInstallPlan = null;
        InstallApplicationsButton.IsEnabled = false;
        ApplicationInstallResultTextBox.IsVisible = false;

        var applications = _selectedProfile?.Profile.Applications ?? [];
        var fromPackage = applications.Count(static application => application.SourceKind == ApplicationSourceKind.PackageRelative);
        var manual = applications.Count - fromPackage;
        ApplicationInstallPanel.IsVisible = applications.Count > 0;
        if (applications.Count == 0)
        {
            return;
        }

        InstallerRootTextBox.Text = ResolveInstallerRoot(_selectedProfile?.Profile.ApplicationSourcePath);
        ApplicationInstallSummaryText.Text = fromPackage == 0
            ? $"В профиле только приложения с ручной установкой ({manual}). Автоматически запускать нечего."
            : $"Устанавливается по очереди: {fromPackage}. Пока один ставится, следующий уже копируется."
                + (manual > 0 ? $" Ещё {manual} помечено как ручная установка — их нужно поставить самостоятельно." : string.Empty);
    }

    private static string ResolveInstallerRoot(string? profilePath)
    {
        if (!string.IsNullOrWhiteSpace(profilePath) && Directory.Exists(profilePath))
        {
            return profilePath;
        }

        // A portable ISO keeps its payload next to Easyaller.App.exe. This takes precedence on a
        // new PC where the authoring-machine path stored in the profile cannot exist.
        var portablePath = Path.Combine(AppContext.BaseDirectory, "Installers");
        if (Directory.Exists(portablePath))
        {
            return portablePath;
        }

        // The application and the installer payload may be distributed as two separate ISO
        // images. When both are mounted, find the volume that exposes an Installers directory.
        if (OperatingSystem.IsWindows())
        {
            foreach (var drive in DriveInfo.GetDrives().Where(static drive => drive.IsReady))
            {
                var mountedInstallerPath = Path.Combine(drive.RootDirectory.FullName, "Installers");
                if (Directory.Exists(mountedInstallerPath))
                {
                    return mountedInstallerPath;
                }
            }
        }

        return profilePath ?? string.Empty;
    }

    private async void ChooseInstallerRoot_Click(object? sender, RoutedEventArgs e)
    {
        if (!StorageProvider.CanOpen)
        {
            SetStatus("Выбор папки недоступен на этой платформе.");
            return;
        }

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Выберите папку с установщиками",
            AllowMultiple = false,
        });
        if (folders.FirstOrDefault() is { } folder)
        {
            InstallerRootTextBox.Text = folder.Path.LocalPath;
            CheckInstallers_Click(sender, e);
        }
    }

    private void CheckInstallers_Click(object? sender, RoutedEventArgs e)
    {
        if (_selectedProfile is null)
        {
            SetStatus("Сначала выберите профиль.");
            return;
        }

        var plan = _applicationInstaller.CreatePlan(_selectedProfile.Profile, InstallerRootTextBox.Text ?? string.Empty);
        _applicationInstallPlan = plan.CanRun ? plan : null;
        InstallApplicationsButton.IsEnabled = plan.CanRun;
        ApplicationInstallResultTextBox.IsVisible = true;

        if (plan.Errors.Count > 0)
        {
            ApplicationInstallResultTextBox.Text = "Проверка не пройдена:\n"
                + string.Join("\n", plan.Errors.Select(static error => "• " + error.Message));
            SetStatus("Установщики не готовы: " + plan.Errors[0].Message);
            return;
        }

        var summary = new StringBuilder("Готово к установке по порядку:\n");
        summary.Append(string.Join("\n", plan.Steps.Select(static (step, index) =>
            $"{index + 1}. {step.DisplayName} — {Path.GetFileName(step.ExecutablePath)}"
            + (step.Arguments.Count == 0 ? "  (без тихих ключей — покажет окно)" : string.Empty))));

        if (plan.SkippedByArchitecture.Count > 0)
        {
            var architecture = ApplicationInstallationService.GetCurrentSystemArchitecture() == ApplicationArchitecture.X64
                ? "64-разрядная"
                : "32-разрядная";
            summary.Append($"\n\nПропущено по разрядности (эта Windows {architecture}):\n");
            summary.Append(string.Join("\n", plan.SkippedByArchitecture.Select(static application =>
                $"• {application.DisplayName} — только для {(application.Architecture == ApplicationArchitecture.X64 ? "64" : "32")}-разрядной")));
        }

        ApplicationInstallResultTextBox.Text = summary.ToString();
        SetStatus($"Установщики найдены: {plan.Steps.Count}."
            + (plan.SkippedByArchitecture.Count > 0 ? $" Пропущено по разрядности: {plan.SkippedByArchitecture.Count}." : string.Empty)
            + " Ничего не запускалось.");
    }

    private async void InstallApplications_Click(object? sender, RoutedEventArgs e)
    {
        if (_applicationInstallPlan is not { } plan)
        {
            SetStatus("Сначала проверьте установщики.");
            return;
        }

        if (!await ConfirmActionWindow.AskAsync(
            this,
            "Установить приложения на этот компьютер?",
            $"Будет запущено установщиков: {plan.Steps.Count}, по одному в порядке профиля.",
            "Установщики меняют этот компьютер. При первой ошибке установка остановится, оставшиеся запущены не будут."
                + (IsMissingAdministratorRights
                    ? " Easyaller запущен без прав администратора: каждый установщик будет запрашивать подтверждение отдельно, а часть из них не сможет установиться."
                    : string.Empty),
            "Установить"))
        {
            SetStatus("Установка приложений отменена. Ничего не запускалось.");
            return;
        }

        InstallApplicationsButton.IsEnabled = false;
        CheckInstallersButton.IsEnabled = false;
        try
        {
            SetStatus("Идёт копирование и установка. Не закрывайте программу.");
            var destination = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                "Easyaller-установщики");
            var progress = new Progress<string>(SetStatus);
            var report = await _applicationInstaller.RunPipelinedAsync(
                plan,
                destination,
                new WindowsApplicationInstallerRunner(),
                progress);
            ApplicationInstallResultTextBox.Text = DescribeInstallReport(report)
                + $"\n\nФайлы скопированы в: {destination}";
            SetStatus(report.StoppedOnFailure
                ? $"Установка остановлена на ошибке. Успешно установлено: {report.InstalledCount} из {report.Results.Count}."
                : $"Приложения установлены: {report.InstalledCount}."
                    + (report.RequiresRestart ? " Требуется перезагрузка." : string.Empty));
            WriteJournalEntry(
                "Установка приложений",
                report.StoppedOnFailure ? "Остановлено на ошибке" : "Установлено",
                report.Results.Select(static result => $"{result.Step.DisplayName}: {DescribeInstallOutcome(result)}").ToArray());
        }
        finally
        {
            CheckInstallersButton.IsEnabled = true;
            InstallApplicationsButton.IsEnabled = true;
        }
    }

    private static string DescribeInstallReport(ApplicationInstallReport report) =>
        string.Join("\n", report.Results.Select(static (result, index) =>
            $"{index + 1}. {result.Step.DisplayName} — {DescribeInstallOutcome(result)}"));

    private static string DescribeInstallOutcome(ApplicationInstallStepResult result) => result.Outcome switch
    {
        ApplicationInstallOutcome.Installed => "установлено",
        ApplicationInstallOutcome.InstalledRestartRequired => "установлено, нужна перезагрузка",
        ApplicationInstallOutcome.NotRun => "не запускалось из-за предыдущей ошибки",
        ApplicationInstallOutcome.Skipped => "пропущено",
        _ => "ошибка: " + (result.ErrorMessage ?? $"код возврата {result.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "неизвестен"}"),
    };

    private void UpdateApplyPlanSummary()
    {
        if (_plan is null)
        {
            ApplyPlanStepsText.Text = "Выберите профиль, чтобы увидеть список операций.";
            ApplyNotAutomatedText.Text = string.Empty;
            return;
        }

        var operations = new List<string>();
        if (!string.IsNullOrWhiteSpace(_plan.TimeZone))
        {
            operations.Add("часовой пояс Windows");
        }

        if (_plan.RuntimePrompts.Any(static prompt => prompt.Kind == RuntimePromptKind.NetworkConfiguration))
        {
            operations.Add("проверка выбранного сетевого адаптера");
        }

        if (_plan.StaticIpv4 is not null)
        {
            operations.Add("статический IPv4 из профиля");
        }

        if (_plan.RuntimePrompts.Any(static prompt => prompt.Kind == RuntimePromptKind.ProxyConfiguration))
        {
            operations.Add("WinHTTP-прокси");
        }

        operations.Add("переименование компьютера, если текущее имя отличается");
        operations.Add("присоединение к домену, если заполнено имя домена");

        if (_selectedProfile is not null && HasPrivacyIntent(_selectedProfile.Profile.Windows.Privacy))
        {
            operations.Add("параметры конфиденциальности из профиля");
        }

        ApplyPlanStepsText.Text = string.Join("\n", operations.Select(static operation => "• " + operation));
        ApplyNotAutomatedText.Text = _plan.Steps.Any(static step => step.Kind == ProvisioningStepKind.InstallApplication)
            ? "Приложения из профиля пока не устанавливаются автоматически — их нужно установить вручную. Инструкции показаны ниже."
            : string.Empty;
    }

    /// <summary>A profile only touches privacy policies when at least one value is an explicit choice.</summary>
    private static bool HasPrivacyIntent(PrivacySettings privacy) => new[]
    {
        privacy.LocationServices,
        privacy.AdvertisingId,
        privacy.OnlineSpeechRecognition,
    }.Any(static preference => preference is PrivacyPreference.Enabled or PrivacyPreference.Disabled);

    private static string DescribeOperations(IReadOnlyList<ProvisioningExecutionOperation> operations) =>
        operations.Count == 0
            ? "Ни одна операция не была выполнена."
            : string.Join("\n", operations.Select(static operation =>
                (operation.WasApplied ? "Применено: " : "Проверено: ")
                + DescribeOperationKind(operation.Kind)
                + (operation.RequiresRestart ? " (требуется перезагрузка)" : string.Empty)));

    private static string DescribeOperationKind(ProvisioningExecutionOperationKind kind) => kind switch
    {
        ProvisioningExecutionOperationKind.SetTimeZone => "часовой пояс",
        ProvisioningExecutionOperationKind.VerifyNetworkAdapter => "сетевой адаптер",
        ProvisioningExecutionOperationKind.ConfigureStaticIpv4 => "статический IPv4",
        ProvisioningExecutionOperationKind.SetWinHttpProxy => "WinHTTP-прокси",
        ProvisioningExecutionOperationKind.RenameComputer => "имя компьютера",
        ProvisioningExecutionOperationKind.JoinDomain => "присоединение к домену",
        _ => "операция",
    };

    private bool TryCreateRuntimeInputs(out RuntimeProvisioningInputs inputs)
    {
        RuntimeDomainCredential? credential = null;
        if (!string.IsNullOrWhiteSpace(RuntimeDomainUserNameTextBox.Text) || !string.IsNullOrWhiteSpace(RuntimeDomainPasswordTextBox.Text))
        {
            if (string.IsNullOrWhiteSpace(RuntimeDomainUserNameTextBox.Text) || string.IsNullOrWhiteSpace(RuntimeDomainPasswordTextBox.Text))
            {
                SetStatus("Введите имя и пароль доменного пользователя либо оставьте оба поля пустыми.");
                inputs = null!;
                return false;
            }

            credential = new RuntimeDomainCredential(RuntimeDomainUserNameTextBox.Text.Trim(), RuntimeDomainPasswordTextBox.Text.AsSpan());
        }

        inputs = new RuntimeProvisioningInputs
        {
            ComputerName = RuntimeComputerNameTextBox.Text?.Trim(),
            NetworkAdapterId = RuntimeNetworkAdapterTextBox.Text?.Trim(),
            ProxyAddress = RuntimeProxyAddressTextBox.Text?.Trim(),
            DomainName = RuntimeDomainNameTextBox.Text?.Trim(),
            DomainCredential = credential,
            ApplyTimeZone = true,
        };
        return true;
    }

    private bool TryGetDeploymentContext(out ProvisioningProfile profile, out WindowsDeploymentTarget target)
    {
        if (_selectedProfile is null)
        {
            profile = null!;
            target = null!;
            SetStatus("Сначала выберите профиль.");
            return false;
        }

        profile = _selectedProfile.Profile;
        var version = (DeploymentVersionComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        if (!Enum.TryParse<WindowsEdition>((DeploymentEditionComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var edition) ||
            string.IsNullOrWhiteSpace(version) ||
            !int.TryParse(DeploymentBuildTextBox.Text, out var build) || build < 1)
        {
            target = null!;
            SetStatus("Укажите поддерживаемые редакцию, версию и положительный номер сборки Windows 11.");
            return false;
        }

        target = new WindowsDeploymentTarget(edition, WindowsArchitecture.Amd64, version, build);
        return true;
    }

    private void ClearDeploymentDryRun()
    {
        _deploymentDryRun = null;
        if (ExportDeploymentButton is not null)
        {
            ExportDeploymentButton.IsEnabled = false;
        }
    }

    private static string DescribePreview(DeploymentPreview preview, IReadOnlyList<DeploymentValidationError> warnings) =>
        $"Цель: Windows 11 {preview.Target.Edition}, {preview.Target.DisplayVersion}, сборка {preview.Target.Build}, {preview.Target.Architecture}.\n" +
        $"Совместимость: {DescribeCompatibility(preview.CompatibilityState)}.\n" +
        $"Шагов: {preview.ProvisioningSteps.Count}. Запросов при настройке: {preview.RuntimePrompts.Count}.\n" +
        DescribeWarnings(warnings);

    private static string DescribeDryRun(DeploymentDryRun dryRun, IReadOnlyList<DeploymentValidationError> warnings) =>
        DescribePreview(dryRun.Preview, warnings) +
        $"OOBE: {DescribeOobe(dryRun.Oobe)}.\n" +
        $"Конфиденциальность: {DescribePrivacy(dryRun.Privacy)}.\n" +
        $"Файл ответов в памяти: {dryRun.AnswerFile.Length} байт.\n" +
        string.Join("\n", dryRun.SensitiveMaterialWarnings.Select(static warning => "Внимание: " + warning.Message));

    private static string DescribeCompatibility(DeploymentCompatibilityState state) => state switch
    {
        DeploymentCompatibilityState.Documented => "документирована",
        DeploymentCompatibilityState.SchemaValidated => "проверена Windows SIM",
        DeploymentCompatibilityState.VmValidated => "проверена в виртуальной машине",
        DeploymentCompatibilityState.Warning => "есть предупреждения",
        _ => "не поддерживается",
    };

    private static string DescribeOobe(OobeSettings oobe)
    {
        var settings = new List<string>();
        if (oobe.HideEula is not null) settings.Add(oobe.HideEula.Value ? "скрыть лицензию" : "показать лицензию");
        if (oobe.HideWirelessSetup is not null) settings.Add(oobe.HideWirelessSetup.Value ? "скрыть настройку сети" : "показать настройку сети");
        if (oobe.HideOnlineAccountScreens is not null) settings.Add(oobe.HideOnlineAccountScreens.Value ? "скрыть вход в онлайн-учётную запись" : "показать вход в онлайн-учётную запись");
        if (oobe.ProtectYourPc is not null) settings.Add($"ProtectYourPC={oobe.ProtectYourPc}");
        return settings.Count == 0 ? "явные параметры не заданы" : string.Join(", ", settings);
    }

    private static string DescribePrivacy(PrivacySettings privacy)
    {
        var values = new[]
        {
            privacy.LocationServices,
            privacy.AdvertisingId,
            privacy.DiagnosticData,
            privacy.TailoredExperiences,
            privacy.OnlineSpeechRecognition,
            privacy.FindMyDevice,
            privacy.InkingAndTypingPersonalization,
        };
        return values.Distinct().Count() == 1
            ? DescribePrivacyPreference(values[0])
            : "используются разные настройки";
    }

    private static string DescribePrivacyPreference(PrivacyPreference preference) => preference switch
    {
        PrivacyPreference.NotConfigured => "не настраивается",
        PrivacyPreference.UserChoice => "выбор пользователя",
        PrivacyPreference.Enabled => "включено",
        PrivacyPreference.Disabled => "отключено",
        _ => "некорректное значение",
    };

    private static string DescribeWarnings(IReadOnlyList<DeploymentValidationError> warnings) => warnings.Count == 0
        ? "Предупреждений нет."
        : "Предупреждения: " + string.Join("; ", warnings.Select(static warning => warning.Message));

    private static string GetMessage(IReadOnlyList<DeploymentValidationError> errors) =>
        errors.FirstOrDefault()?.Message ?? "Повторите попытку.";

    /// <summary>
    /// Core validation messages are English by contract, but the desktop interface is Russian,
    /// so runtime codes are translated before an operator ever sees them.
    /// </summary>
    private static string GetRuntimeMessage(string code, string fallback) => code switch
    {
        "runtime.computerName.required" => "Укажите имя компьютера.",
        "runtime.computerName.invalid" => "Имя компьютера: от 1 до 15 латинских букв, цифр или дефисов.",
        "runtime.computerName.prefix.mismatch" => "Имя компьютера должно начинаться с шаблона из профиля.",
        "runtime.computerName.suffix.invalid" => "После шаблона из профиля должно идти 2 или 3 цифры.",
        "runtime.network.adapter.required" => "Укажите сетевой адаптер.",
        "runtime.proxy.required" => "Укажите адрес прокси — этого требует профиль.",
        "runtime.domain.required" => "Укажите имя домена — этого требует профиль.",
        "runtime.domain.credential.required" => "Укажите учётную запись и пароль домена.",
        "execution.confirmation.required" => "Введите APPLY заглавными буквами, чтобы разрешить изменения.",
        "execution.administrator.required" => "Требуются права администратора. Запустите Easyaller от имени администратора.",
        "execution.windows.required" => "Эта операция доступна только в Windows.",
        "execution.network.adapter.invalid" => "Выбранный сетевой адаптер не найден или отключён.",
        "execution.network.staticIpv4.failed" => "Не удалось применить статический IPv4 к выбранному адаптеру.",
        "execution.proxy.failed" => "Не удалось применить WinHTTP-прокси.",
        "execution.computerName.failed" => "Не удалось переименовать компьютер.",
        "execution.domain.failed" => "Не удалось присоединить компьютер к домену.",
        "execution.timeZone.failed" => "Не удалось изменить часовой пояс.",
        "execution.timeZone.notConfigured" => "В профиле не задан часовой пояс Windows.",
        _ => fallback,
    };

    private static string DescribeExecution(ProvisioningExecutionResult result)
    {
        if (result.Errors.FirstOrDefault() is { } failure)
        {
            var restartWarning = result.Warnings.FirstOrDefault()?.Message;
            // Detail is raw PowerShell diagnostic text — untranslated on purpose, it is the actual
            // reason (e.g. "adapter not found") behind an otherwise generic translated sentence.
            return "Применение остановлено: " + GetRuntimeMessage(failure.Code, failure.Message)
                + (string.IsNullOrWhiteSpace(failure.Detail) ? string.Empty : "\nПодробности: " + failure.Detail)
                + (string.IsNullOrWhiteSpace(restartWarning) ? string.Empty : " " + restartWarning);
        }

        var warning = result.Warnings.FirstOrDefault()?.Message;
        return result.Status switch
        {
            ProvisioningExecutionStatus.Completed => "Подтверждённые параметры применены. Перезагрузка не требуется.",
            ProvisioningExecutionStatus.RestartRequired => string.IsNullOrWhiteSpace(warning)
                ? "Подтверждённые параметры применены. Перезагрузите Windows для завершения и не удаляйте созданное состояние resume."
                : "Подтверждённые параметры применены. " + warning,
            ProvisioningExecutionStatus.Resumed => "Возобновление после перезагрузки проверено.",
            _ => "Применение не завершено.",
        };
    }

    private static string ToPackageDirectoryName(string name)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var normalizedName = new string(name
            .Select(character => invalidCharacters.Contains(character) ? '-' : character)
            .ToArray())
            .Trim();
        return (string.IsNullOrEmpty(normalizedName) ? "easyaller" : normalizedName) + "-deployment-package";
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
            // Its name is already shown at the top of the editor, so restating it here would be noise.
            SetStatus($"Повреждённых локальных файлов профиля: {list.Issues.Count}. Они перемещены в Corrupted.");
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
                SelectedProfileRevision = "Профиль не выбран";
            }
            else
            {
                SelectedProfileName = _selectedProfile.Name;
                SelectedProfileRevision = $"Версия {_selectedProfile.Profile.Revision}";
            }

            ProfileNameTextBox.Text = _selectedProfile?.Profile.Metadata.Name ?? string.Empty;
            ProfileDescriptionTextBox.Text = _selectedProfile?.Profile.Metadata.Description ?? string.Empty;
            ProfileDescriptionDisplayText.Text = DescribeProfileDescription(ProfileDescriptionTextBox.Text);
            ProfileNameTextBox.IsVisible = false;
            ProfileNameDisplayText.IsVisible = true;
            ProfileDescriptionTextBox.IsVisible = false;
            ProfileDescriptionDisplayText.IsVisible = true;
            PopulateSettingsControls(_selectedProfile?.Profile);
        }
        finally
        {
            _isPopulatingEditor = false;
        }

        SetHasUnsavedChanges(false);
        RefreshEditorFeedback();
        RefreshApplyTab();
        ProfileEditorScrollViewer.Offset = default;
        Dispatcher.UIThread.Post(
            () => ProfileEditorScrollViewer.Offset = default,
            DispatcherPriority.Background);

        OnPropertyChanged(nameof(HasSelectedProfile));
        OnPropertyChanged(nameof(CanSaveProfile));
        OnPropertyChanged(nameof(CanUseSavedProfileActions));
    }

    private string GetNextProfileName() => _profiles.Count == 0
        ? "Новый профиль компьютера"
        : $"Новый профиль компьютера {_profiles.Count + 1}";

    private static string GetLocalProfileDirectory() => FileProfileRepository.GetDefaultRootDirectory();

    private static void MigrateLegacyMachineWideProfiles(string destinationDirectory)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var legacyDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Easyaller",
            "Profiles");
        if (!Directory.Exists(legacyDirectory) || string.Equals(legacyDirectory, destinationDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(destinationDirectory);
            foreach (var sourcePath in Directory.EnumerateFiles(legacyDirectory, $"*{FileProfileRepository.ProfileExtension}", SearchOption.TopDirectoryOnly))
            {
                var fileName = Path.GetFileName(sourcePath);
                var profileIdText = fileName[..^FileProfileRepository.ProfileExtension.Length];
                if (!Guid.TryParse(profileIdText, out _) || File.Exists(Path.Combine(destinationDirectory, fileName)))
                {
                    continue;
                }

                File.Copy(sourcePath, Path.Combine(destinationDirectory, fileName));
            }
        }
        catch (IOException)
        {
            // Legacy files remain untouched; the new per-user repository still opens.
        }
        catch (UnauthorizedAccessException)
        {
            // Some managed installations prohibit reading ProgramData.
        }
    }

    private static string GetMessage(IReadOnlyList<ProfileValidationError> errors) =>
        errors.FirstOrDefault() is { } error ? GetValidationMessage(error) : "Повторите попытку.";

    /// <summary>
    /// Whether the status strip has something worth showing. It stays collapsed while idle so the
    /// editor gets that space back, and appears only for an actual result, error, or confirmation.
    /// </summary>
    public bool HasStatusMessage => !string.IsNullOrEmpty(StatusText?.Text);

    private void SetStatus(string message)
    {
        StatusText.Text = message;
        OnPropertyChanged(nameof(HasStatusMessage));
    }

    private void AttachEditorChangeHandlers()
    {
        // The machine number is a runtime value, not profile data, so it only refreshes the preview.
        ComputerNameNumberTextBox.TextChanged += (_, _) => UpdateComputerNamePreview();
        ProfileNameTextBox.TextChanged += (_, _) => ProfileNameDisplayText.Text = ProfileNameTextBox.Text;
        ProfileDescriptionTextBox.TextChanged += (_, _) => ProfileDescriptionDisplayText.Text = DescribeProfileDescription(ProfileDescriptionTextBox.Text);

        foreach (var textBox in new[]
        {
            ProfileNameTextBox,
            ProfileDescriptionTextBox,
            ApplicationSourcePathTextBox,
            ComputerNameCustomTypeTextBox,
            StaticIpv4AddressTextBox,
            StaticIpv4SubnetMaskTextBox,
            StaticIpv4DefaultGatewayTextBox,
            StaticIpv4AdapterIdTextBox,
            ProxyAddressTextBox,
            ProxyBypassListTextBox,
            DomainNameTextBox,
            DomainUserNameTextBox,
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
            ComputerNameTypeComboBox,
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
        _dnsServers.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasNoDnsServers));
            EditorValueChanged();
        };
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
        DnsServerInputTextBox.IsEnabled = staticNetworkEnabled && _dnsServers.Count < 3;
        AddDnsServerButton.IsEnabled = staticNetworkEnabled && _dnsServers.Count < 3;
        RemoveDnsServerButton.IsEnabled = staticNetworkEnabled && DnsServersList.SelectedItem is not null;
        DnsServersList.IsEnabled = staticNetworkEnabled;
        var proxyEnabled = GetSelectedEnum(ProxyModeComboBox, original.Machine.Proxy.Mode)
            == ProxyConfigurationMode.PromptAtRuntime;
        ProxyBypassListTextBox.IsEnabled = proxyEnabled;
        ProxyAddressTextBox.IsEnabled = proxyEnabled;
        ApplyProxyToCurrentPcButton.IsEnabled = proxyEnabled;

        UpdateComputerNamePreview();

        var domainEnabled = GetSelectedEnum(DomainModeComboBox, original.Domain.Mode) != DomainMode.NotConfigured;
        DomainNameTextBox.IsEnabled = domainEnabled;
        DomainUserNameTextBox.IsEnabled = domainEnabled;
        DomainPasswordTextBox.IsEnabled = domainEnabled;
        ApplyDomainToCurrentPcButton.IsEnabled = domainEnabled;
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
                && string.IsNullOrWhiteSpace(GetComputerNamePrefix()));
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
            ? _dnsServers.Count == 0
                ? "Статический IPv4, DNS без изменений"
                : $"Статический IPv4, DNS: {_dnsServers.Count}"
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
        textBlock.Foreground = Brush.Parse(hasErrors ? "#FCA5A5" : isOptional ? "#98A6B8" : "#7EE2A8");
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
        "machine.network.staticIpv4.dnsServers.count.invalid" => "Можно указать не более трёх DNS-серверов.",
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
        GetComputerNamePrefix(),
        GetSelectedEnum(ProxyModeComboBox, original.Machine.Proxy.Mode),
        GetSelectedEnum(DomainModeComboBox, original.Domain.Mode),
        GetSelectedEnum(LaunchModeComboBox, original.Deployment.LaunchMode),
        GetSelectedEnum(CleanupModeComboBox, original.Cleanup.ProvisioningAccount),
        GetSelectedEnum(NetworkModeComboBox, original.Machine.Network.Mode),
        StaticIpv4AddressTextBox.Text,
        StaticIpv4SubnetMaskTextBox.Text,
        StaticIpv4DefaultGatewayTextBox.Text,
        string.Join(", ", _dnsServers),
        StaticIpv4AdapterIdTextBox.Text,
        ProxyBypassListTextBox.Text,
        ProxyAddressTextBox.Text,
        DomainNameTextBox.Text,
        DomainUserNameTextBox.Text,
        ApplicationSourcePathTextBox.Text);

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
        PopulateComputerNameTemplate(profile?.Machine.ComputerName.Prefix);
        SetSelectedEnum(NetworkModeComboBox, profile?.Machine.Network.Mode);
        StaticIpv4AddressTextBox.Text = profile?.Machine.Network.StaticIpv4?.Address ?? string.Empty;
        StaticIpv4SubnetMaskTextBox.Text = profile?.Machine.Network.StaticIpv4?.SubnetMask ?? string.Empty;
        StaticIpv4DefaultGatewayTextBox.Text = profile?.Machine.Network.StaticIpv4?.DefaultGateway ?? string.Empty;
        _dnsServers.Clear();
        if (profile?.Machine.Network.StaticIpv4 is { } staticIpv4)
        {
            foreach (var dnsServer in staticIpv4.DnsServers)
            {
                _dnsServers.Add(dnsServer);
            }
        }

        StaticIpv4AdapterIdTextBox.Text = profile is null
            ? string.Empty
            : DefaultIfBlank(profile.Machine.Network.StaticIpv4?.AdapterId, DefaultNetworkAdapterName);
        ApplicationSourcePathTextBox.Text = profile?.ApplicationSourcePath ?? string.Empty;
        ProxyAddressTextBox.Text = profile?.Machine.Proxy.Address ?? string.Empty;
        DomainNameTextBox.Text = profile?.Domain.DomainName ?? string.Empty;
        DomainUserNameTextBox.Text = profile?.Domain.UserName ?? string.Empty;
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

    private string GetComputerNamePrefix()
    {
        var type = GetSelectedTag(ComputerNameTypeComboBox, "NOMAD");
        var suffix = type == "Custom" ? ComputerNameCustomTypeTextBox.Text?.Trim() : type;
        return ComputerNameSitePrefix + (suffix ?? string.Empty);
    }

    private void PopulateComputerNameTemplate(string? prefix)
    {
        var suffix = prefix?.StartsWith(ComputerNameSitePrefix, StringComparison.OrdinalIgnoreCase) == true
            ? prefix[ComputerNameSitePrefix.Length..]
            : prefix ?? string.Empty;
        if (string.Equals(suffix, "NOMAD", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(suffix, "THINK", StringComparison.OrdinalIgnoreCase))
        {
            SetSelectedTag(ComputerNameTypeComboBox, suffix.ToUpperInvariant());
            ComputerNameCustomTypeTextBox.Text = string.Empty;
            return;
        }

        SetSelectedTag(ComputerNameTypeComboBox, "Custom");
        ComputerNameCustomTypeTextBox.Text = suffix;
    }

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

    private static readonly FilePickerFileType WindowsIsoFileType = new("Образ Windows (ISO)")
    {
        Patterns = ["*.iso"],
        MimeTypes = ["application/x-iso9660-image"],
    };

    private static readonly FilePickerFileType InstallerFileType = new("Установщики Windows")
    {
        Patterns = ["*.exe", "*.msi"],
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

public sealed record ComplianceCheckListItem(ComplianceCheck Check)
{
    public string Title => Check.Title;

    public string Detail => Check.Status == ComplianceStatus.Match
        ? Check.Actual
        : $"ожидается: {Check.Expected}   ·   фактически: {Check.Actual}";

    public string StatusLabel => Check.Status switch
    {
        ComplianceStatus.Match => "совпадает",
        ComplianceStatus.Mismatch => "расхождение",
        ComplianceStatus.Unknown => "не прочитано",
        _ => "не задано",
    };

    public IBrush StatusBrush => Check.Status switch
    {
        ComplianceStatus.Match => Brushes.LightGreen,
        ComplianceStatus.Mismatch => Brushes.Salmon,
        ComplianceStatus.Unknown => Brushes.Khaki,
        _ => Brushes.Gray,
    };
}

public sealed class ApplicationListItem(ApplicationProfile application) : INotifyPropertyChanged
{
    private int _queueNumber;

    public ApplicationProfile Application { get; } = application;

    public int QueueNumber
    {
        get => _queueNumber;
        set
        {
            if (_queueNumber == value)
            {
                return;
            }

            _queueNumber = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(QueueNumber)));
        }
    }

    public string DisplayName => Application.DisplayName;

    public string Detail => Application.SourceKind == ApplicationSourceKind.PackageRelative
        ? "Из пакета"
        : "Внешняя ручная установка";
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed record InstructionListItem(InstructionProfile Instruction)
{
    public string Id => Instruction.Id;

    public string Title => Instruction.Title;

    /// <summary>Instruction text is data shown to an operator; it is never executed.</summary>
    public string Content => Instruction.Content;
}
