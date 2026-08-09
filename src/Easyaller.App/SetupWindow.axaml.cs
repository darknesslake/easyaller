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
            PlanSummaryText.Text = "The selected profile is invalid.";
            SetStatus(result.Errors.FirstOrDefault()?.Message ?? "Could not create provisioning plan.");
            return;
        }

        _plan = result.Plan;
        SelectedProfileText.Text = selected.Name;
        PlanSummaryText.Text = $"{_plan!.Steps.Count} planned step(s), {_plan.RuntimePrompts.Count} runtime prompt(s).";
        _prompts.Clear();
        foreach (var prompt in _plan.RuntimePrompts)
        {
            _prompts.Add(new RuntimePromptListItem(prompt));
        }

        SetStatus("Plan preview is ready. No Windows changes have been made.");
    }

    private void ValidateRuntimeInputs_Click(object? sender, RoutedEventArgs e)
    {
        if (_plan is null)
        {
            SetStatus("Select a valid profile first.");
            return;
        }

        RuntimeDomainCredential? credential = null;
        if (!string.IsNullOrWhiteSpace(DomainUserNameTextBox.Text) || !string.IsNullOrWhiteSpace(DomainPasswordTextBox.Text))
        {
            if (string.IsNullOrWhiteSpace(DomainUserNameTextBox.Text) || string.IsNullOrWhiteSpace(DomainPasswordTextBox.Text))
            {
                SetStatus("Enter both domain user name and password, or leave both empty.");
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
            ? "Runtime inputs are valid. Execution is not implemented yet."
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
            SetStatus($"{list.Issues.Count} invalid local profile file(s) were isolated.");
        }
    }

    private void SetStatus(string text) => SetupStatusText.Text = text;
}

public sealed record RuntimePromptListItem(RuntimePrompt Prompt)
{
    public string Title => Prompt.IsRequired ? $"{Prompt.Kind} required" : $"{Prompt.Kind} optional";

    public string Description => Prompt.Description;
}
