using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Easyaller.Core.Profiles;
using Easyaller.Core.Provisioning;

namespace Easyaller.App;

public sealed partial class SetupWindow : Window
{
    private readonly IProfileRepository _repository;
    private readonly ProvisioningPlanBuilder _planBuilder = new();
    private readonly RuntimeProvisioningInputValidator _inputValidator = new();
    private readonly ObservableCollection<ProfileListItem> _profiles = [];
    private readonly ObservableCollection<RuntimePromptListItem> _prompts = [];
    private ProvisioningPlan? _plan;

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
        LoadProfiles();
    }

    private void SetupProfilesList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (SetupProfilesList.SelectedItem is not ProfileListItem selected)
        {
            return;
        }

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
