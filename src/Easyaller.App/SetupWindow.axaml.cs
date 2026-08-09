using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Easyaller.Core.Profiles;
using Easyaller.Core.Provisioning;
using Easyaller.Deployment;

namespace Easyaller.App;

public sealed partial class SetupWindow : Window
{
    private readonly IProfileRepository _repository;
    private readonly ProvisioningPlanBuilder _planBuilder = new();
    private readonly RuntimeProvisioningInputValidator _inputValidator = new();
    private readonly DeploymentPreparationController _deploymentController = new();
    private readonly ObservableCollection<ProfileListItem> _profiles = [];
    private readonly ObservableCollection<RuntimePromptListItem> _prompts = [];
    private ProvisioningPlan? _plan;
    private ProfileListItem? _selectedProfile;
    private DeploymentDryRun? _deploymentDryRun;

    public SetupWindow()
        : this(new FileProfileRepository())
    {
    }

    public SetupWindow(IProfileRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        InitializeComponent();
        SetupProfilesList.ItemsSource = _profiles;
        RuntimePromptsList.ItemsSource = _prompts;
        DeploymentEditionComboBox.SelectedIndex = 0;
        DeploymentVersionComboBox.SelectedIndex = 0;
        LoadProfiles();
    }

    private void SetupProfilesList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (SetupProfilesList.SelectedItem is not ProfileListItem selected)
        {
            return;
        }

        _selectedProfile = selected;
        ClearDeploymentDryRun();
        var result = _planBuilder.Create(selected.Profile);
        if (!result.IsValid)
        {
            _plan = null;
            SelectedProfileText.Text = selected.Name;
            PlanSummaryText.Text = "Выбранный профиль содержит ошибки.";
            SetStatus(result.Errors.FirstOrDefault()?.Message ?? "Не удалось создать план настройки.");
            return;
        }

        _plan = result.Plan;
        SelectedProfileText.Text = selected.Name;
        PlanSummaryText.Text = $"Запланировано шагов: {_plan!.Steps.Count}. Запросов при настройке: {_plan.RuntimePrompts.Count}.";
        _prompts.Clear();
        foreach (var prompt in _plan.RuntimePrompts)
        {
            _prompts.Add(new RuntimePromptListItem(prompt));
        }

        SetStatus("Предпросмотр плана готов. Изменения Windows не вносились.");
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

        RuntimeDomainCredential? credential = null;
        if (!string.IsNullOrWhiteSpace(DomainUserNameTextBox.Text) || !string.IsNullOrWhiteSpace(DomainPasswordTextBox.Text))
        {
            if (string.IsNullOrWhiteSpace(DomainUserNameTextBox.Text) || string.IsNullOrWhiteSpace(DomainPasswordTextBox.Text))
            {
                SetStatus("Введите имя и пароль доменного пользователя либо оставьте оба поля пустыми.");
                return;
            }

            credential = new RuntimeDomainCredential(DomainUserNameTextBox.Text.Trim(), DomainPasswordTextBox.Text.AsSpan());
        }

        using var inputs = new RuntimeProvisioningInputs
        {
            ComputerName = ComputerNameTextBox.Text?.Trim(),
            NetworkAdapterId = NetworkAdapterTextBox.Text?.Trim(),
            ProxyAddress = ProxyAddressTextBox.Text?.Trim(),
            DomainName = DomainNameTextBox.Text?.Trim(),
            DomainCredential = credential,
        };
        DomainPasswordTextBox.Text = string.Empty;
        var validation = _inputValidator.Validate(_plan, inputs);
        SetStatus(validation.IsValid
            ? "Введённые значения корректны. Выполнение пока не реализовано."
            : validation.Errors.First().Message);
    }

    private void LoadProfiles()
    {
        var list = _repository.List();
        foreach (var profile in list.Profiles)
        {
            _profiles.Add(new ProfileListItem(profile));
        }

        SetupProfilesList.SelectedItem = _profiles.FirstOrDefault();
        if (list.Issues.Count > 0)
        {
            SetStatus($"Повреждённых локальных файлов профиля изолировано: {list.Issues.Count}.");
        }
    }

    private void SetStatus(string text) => SetupStatusText.Text = text;

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

    private static string ToPackageDirectoryName(string name)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var normalizedName = new string(name
            .Select(character => invalidCharacters.Contains(character) ? '-' : character)
            .ToArray())
            .Trim();
        return (string.IsNullOrEmpty(normalizedName) ? "easyaller" : normalizedName) + "-deployment-package";
    }
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
