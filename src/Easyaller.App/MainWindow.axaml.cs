using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
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
    private readonly ObservableCollection<RuntimePromptListItem> _prompts = [];
    private ProfileListItem? _selectedProfile;
    private byte[]? _pendingImportSource;
    private ProvisioningProfile? _pendingExportProfile;
    private ProvisioningPlan? _plan;
    private DeploymentDryRun? _deploymentDryRun;
    private const string ComputerNameSitePrefix = "URA01";
    private readonly CurrentMachineInspector _machineInspector = new();
    private readonly ObservableCollection<ComplianceCheckListItem> _complianceChecks = [];
    private readonly ProfileComplianceChecker _complianceChecker = new();
    private readonly ProvisioningJournal _journal = new();
    private readonly RuntimeProfileEligibilityService _eligibilityService = new();
    private readonly PrivacyConfigurationService _privacyService = new();
    private ComplianceReport? _complianceReport;
    private EditorTab _activeTab = EditorTab.Profile;
    private bool _isPrepareInstallMode;
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
        MigrateLegacyMachineWideProfiles(_repository.RootDirectory);
        _profileImportExportService = new ProfileImportExportService(_repository);
        _profileEditorController = new ProfileEditorController(_repository);
        ProfilesList.ItemsSource = _profiles;
        ApplicationsList.ItemsSource = _applications;
        InstructionsList.ItemsSource = _instructions;
        DnsServersList.ItemsSource = _dnsServers;
        RuntimePromptsList.ItemsSource = _prompts;
        ComplianceList.ItemsSource = _complianceChecks;
        DeploymentEditionComboBox.SelectedIndex = 0;
        DeploymentVersionComboBox.SelectedIndex = 0;
        StoragePathText.Text = _repository.RootDirectory;
        AttachEditorChangeHandlers();
        SetMode(prepareInstallMode: false);
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

    private void SetMode(bool prepareInstallMode)
    {
        _isPrepareInstallMode = prepareInstallMode;
        SetActiveNavClass(ThisPcModeButton, !prepareInstallMode);
        SetActiveNavClass(NewInstallModeButton, prepareInstallMode);
        ApplyTabButton.Content = prepareInstallMode ? "Подготовка установки" : "Применить на этом ПК";
        ModeSubtitleText.Text = prepareInstallMode
            ? "Профиль превращается в пакет файлов для Windows, которую только предстоит установить. Этот компьютер не изменяется."
            : "Профиль применяется к уже установленной Windows на этом компьютере. Пароли и учётные данные не сохраняются.";

        // Answer-file settings only reach a Windows Setup run, so they stay hidden while configuring a live PC.
        InstallOnlyWindowsSettings.IsVisible = prepareInstallMode;
        InstallOnlyLaunchSettings.IsVisible = prepareInstallMode;
        InstallOnlyCleanupSettings.IsVisible = prepareInstallMode;
        InstallOnlyApplicationsSection.IsVisible = prepareInstallMode;
        WindowsSectionTitleText.Text = prepareInstallMode ? "Windows и первый запуск" : "Часовой пояс Windows";
        WindowsSectionNoteText.Text = prepareInstallMode
            ? "Это стандарт для устанавливаемой Windows. Он не меняет уже работающий компьютер."
            : "К уже установленной Windows из этого раздела применяется только часовой пояс. Языки, редакции и экраны первичной настройки задаются файлом ответов и видны в режиме подготовки установки.";
        SetActiveTab(_activeTab);
    }

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
        EditorBottomBar.IsVisible = showEditor;
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
            ApplyProxyToCurrentPcButton.IsEnabled = true;
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

            var report = _complianceChecker.Check(
                _selectedProfile.Profile,
                snapshot.ToMachineState(),
                DateTimeOffset.Now);
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

    /// <summary>
    /// Copies the values typed for this PC back into the shared profile, so the same settings
    /// can be reused when preparing a new Windows installation.
    /// </summary>
    private void PushRuntimeToProfile_Click(object? sender, RoutedEventArgs e)
    {
        if (_selectedProfile is null)
        {
            SetStatus("Сначала выберите профиль.");
            return;
        }

        var moved = new List<string>();

        if (RuntimeComputerNameTextBox.Text?.Trim() is { Length: > 0 } computerName)
        {
            SetComputerNameTemplateFromFullName(computerName);
            moved.Add("имя компьютера");
        }

        if (RuntimeNetworkAdapterTextBox.Text?.Trim() is { Length: > 0 } adapterId)
        {
            StaticIpv4AdapterIdTextBox.Text = adapterId;
            moved.Add("сетевой адаптер");
        }

        if (RuntimeProxyAddressTextBox.Text?.Trim() is { Length: > 0 } proxyAddress)
        {
            SetSelectedEnum<ProxyConfigurationMode>(ProxyModeComboBox, ProxyConfigurationMode.PromptAtRuntime);
            ProxyAddressTextBox.Text = proxyAddress;
            moved.Add("прокси");
        }

        if (RuntimeDomainNameTextBox.Text?.Trim() is { Length: > 0 } domainName)
        {
            DomainNameTextBox.Text = domainName;
            if (GetSelectedEnum(DomainModeComboBox, DomainMode.NotConfigured) == DomainMode.NotConfigured)
            {
                SetSelectedEnum<DomainMode>(DomainModeComboBox, DomainMode.Optional);
            }

            moved.Add("домен");
        }

        if (RuntimeDomainUserNameTextBox.Text?.Trim() is { Length: > 0 } domainUserName)
        {
            DomainUserNameTextBox.Text = domainUserName;
        }

        SetStatus(moved.Count == 0
            ? "Заполните поля выше, тогда их можно будет перенести в профиль."
            : $"Перенесено в профиль: {string.Join(", ", moved)}. Откройте вкладку «Профиль» и сохраните. Пароль не переносится.");
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
        RuntimeNetworkAdapterTextBox.Text = profile.Machine.Network.StaticIpv4?.AdapterId ?? string.Empty;
        RuntimeProxyAddressTextBox.Text = profile.Machine.Proxy.Address ?? string.Empty;
        RuntimeDomainNameTextBox.Text = profile.Domain.DomainName ?? string.Empty;
        RuntimeDomainUserNameTextBox.Text = profile.Domain.UserName ?? string.Empty;
        SetStatus("Поля заполнены значениями профиля. Имя компьютера и пароль домена всегда вводятся вручную.");
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
        finally { ApplyDomainToCurrentPcButton.IsEnabled = true; }
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

    private void RefreshApplyTab()
    {
        ClearDeploymentDryRun();
        if (_selectedProfile is null)
        {
            _plan = null;
            PlanSummaryText.Text = "Выберите профиль в списке слева.";
            TimeZoneActionText.Text = "Выберите профиль, чтобы увидеть его часовой пояс.";
            _prompts.Clear();
            ManualInstructionsPanel.IsVisible = false;
            UpdateApplyPlanSummary();
            return;
        }

        var result = _planBuilder.Create(_selectedProfile.Profile);
        if (!result.IsValid)
        {
            _plan = null;
            PlanSummaryText.Text = "Выбранный профиль содержит ошибки.";
            _prompts.Clear();
            UpdateApplyPlanSummary();
            return;
        }

        _plan = result.Plan;
        ApplyTimeZoneCheckBox.IsChecked = false;
        TimeZoneActionText.Text = $"Часовой пояс профиля: {result.Plan!.TimeZone}. Без флажка он не меняется.";
        PlanSummaryText.Text = $"Запланировано шагов: {_plan!.Steps.Count}. Запросов при настройке: {_plan.RuntimePrompts.Count}.";
        _prompts.Clear();
        foreach (var prompt in _plan.RuntimePrompts)
        {
            _prompts.Add(new RuntimePromptListItem(prompt));
        }

        var instructions = _selectedProfile.Profile.Instructions
            .Select(static instruction => new InstructionListItem(instruction))
            .ToArray();
        ManualInstructionsList.ItemsSource = instructions;
        ManualInstructionsPanel.IsVisible = instructions.Length > 0;

        UpdateApplyPlanSummary();
    }

    private void DeploymentEdition_SelectionChanged(object? sender, SelectionChangedEventArgs e) => ClearDeploymentDryRun();

    private void DeploymentVersion_SelectionChanged(object? sender, SelectionChangedEventArgs e) => ClearDeploymentDryRun();

    private void DeploymentBuild_TextChanged(object? sender, TextChangedEventArgs e) => ClearDeploymentDryRun();

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

        if (ApplyTimeZoneCheckBox.IsChecked == true && string.IsNullOrWhiteSpace(_plan.TimeZone))
        {
            SetStatus("В профиле не задан часовой пояс Windows. Снимите флажок или задайте пояс в профиле.");
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
                ? $"Введённые значения корректны. Ничего не изменено. Для применения введите {ProvisioningExecutionService.ConfirmationPhrase}."
                : validation.Errors.First().Message);
        }
    }

    private async void ApplyRuntimeInputs_Click(object? sender, RoutedEventArgs e)
    {
        if (_plan is null)
        {
            SetStatus("Сначала выберите корректный профиль.");
            return;
        }

        var confirmation = ApplyConfirmationTextBox.Text;
        if (!string.Equals(confirmation, ProvisioningExecutionService.ConfirmationPhrase, StringComparison.Ordinal))
        {
            SetStatus($"Введите {ProvisioningExecutionService.ConfirmationPhrase} заглавными буквами, чтобы разрешить изменения Windows.");
            return;
        }

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
            ApplyConfirmationTextBox.Text = string.Empty;
            RuntimeDomainPasswordTextBox.Text = string.Empty;
        }
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
        var result = await Task.Run(() => _privacyService.Apply(plan, policyStore));
        var verified = result.Verification.Count(static verification => verification.IsVerified);
        return result.IsApplied
            ? $"Параметры конфиденциальности применены и перечитаны: {verified} из {plan.Assignments.Count}."
            : "Параметры конфиденциальности не применены: " + (result.Errors.FirstOrDefault()?.Message ?? "неизвестная ошибка");
    }

    private void ApplyTimeZoneCheckBox_IsCheckedChanged(object? sender, RoutedEventArgs e) => UpdateApplyPlanSummary();

    private void UpdateApplyPlanSummary()
    {
        if (_plan is null)
        {
            ApplyPlanStepsText.Text = "Выберите профиль, чтобы увидеть список операций.";
            ApplyNotAutomatedText.Text = string.Empty;
            return;
        }

        var operations = new List<string>();
        if (ApplyTimeZoneCheckBox.IsChecked == true)
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
            ApplyTimeZone = ApplyTimeZoneCheckBox.IsChecked == true,
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

    private static string DescribeExecution(ProvisioningExecutionResult result)
    {
        var error = result.Errors.FirstOrDefault()?.Message;
        if (!string.IsNullOrWhiteSpace(error))
        {
            var restartWarning = result.Warnings.FirstOrDefault()?.Message;
            return "Применение остановлено: " + error +
                (string.IsNullOrWhiteSpace(restartWarning) ? string.Empty : " " + restartWarning);
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
        RefreshApplyTab();
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

    private void SetStatus(string message) => StatusText.Text = message;

    private void AttachEditorChangeHandlers()
    {
        foreach (var textBox in new[]
        {
            ProfileNameTextBox,
            ProfileDescriptionTextBox,
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
        DnsServerInputTextBox.IsEnabled = staticNetworkEnabled && _dnsServers.Count < 3;
        AddDnsServerButton.IsEnabled = staticNetworkEnabled && _dnsServers.Count < 3;
        RemoveDnsServerButton.IsEnabled = staticNetworkEnabled && DnsServersList.SelectedItem is not null;
        DnsServersList.IsEnabled = staticNetworkEnabled;
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
            ? "WinHTTP-прокси при применении"
            : "Без настройки WinHTTP-прокси";
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
        DomainUserNameTextBox.Text);

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

        StaticIpv4AdapterIdTextBox.Text = profile?.Machine.Network.StaticIpv4?.AdapterId ?? string.Empty;
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

    /// <summary>Instruction text is data shown to an operator; it is never executed.</summary>
    public string Content => Instruction.Content;
}

public sealed record RuntimePromptListItem(RuntimePrompt Prompt)
{
    public string Title => Prompt.Kind switch
    {
        RuntimePromptKind.ComputerName => Prompt.IsRequired ? "Имя компьютера обязательно" : "Имя компьютера необязательно",
        RuntimePromptKind.NetworkConfiguration => Prompt.IsRequired ? "Настройка сети обязательна" : "Настройка сети необязательна",
        RuntimePromptKind.ProxyConfiguration => Prompt.IsRequired ? "Настройка прокси обязательна" : "Настройка прокси необязательна",
        RuntimePromptKind.DomainJoin => Prompt.IsRequired ? "Присоединение к домену обязательно" : "Присоединение к домену необязательно",
        _ => "Параметр настройки",
    };

    public string Description => Prompt.Kind switch
    {
        RuntimePromptKind.ComputerName => "Выберите окончательное имя компьютера при настройке.",
        RuntimePromptKind.NetworkConfiguration => "Выберите сетевой адаптер и параметры сети при настройке.",
        RuntimePromptKind.ProxyConfiguration => "Введите параметры прокси при настройке.",
        RuntimePromptKind.DomainJoin => "Введите параметры присоединения к домену и краткоживущие учётные данные при настройке.",
        _ => Prompt.Description,
    };
}
