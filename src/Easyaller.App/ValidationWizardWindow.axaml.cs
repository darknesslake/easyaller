using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace Easyaller.App;

public sealed partial class ValidationWizardWindow : Window
{
    private readonly ValidationChecklistStore _store = ValidationChecklistStore.CreateDefault();
    private ValidationChecklistState _state;
    private int _currentIndex;
    private bool _isPopulating;

    public ValidationWizardWindow()
    {
        InitializeComponent();
        _state = _store.LoadOrCreate();
        _currentIndex = FindFirstPendingIndex(_state);
        ShowCurrentStep();
    }

    private void ShowCurrentStep()
    {
        _isPopulating = true;
        var entry = _state.Entries[_currentIndex];
        ValidationStepNumberText.Text = $"Шаг {_currentIndex + 1} из {_state.Entries.Count} · {DescribeStatus(entry.Status)}";
        ValidationStepTitleText.Text = entry.Title;
        ValidationStepInstructionsText.Text = entry.Instructions;
        ValidationStepExpectedText.Text = entry.ExpectedResult;
        ValidationNotesTextBox.Text = entry.Notes;
        PreviousValidationStepButton.IsEnabled = _currentIndex > 0;
        NextValidationStepButton.IsEnabled = _currentIndex < _state.Entries.Count - 1;
        var completed = _state.Entries.Count(static entry => entry.Status != ValidationCheckStatus.Pending);
        ValidationProgressBar.Value = completed * 100d / _state.Entries.Count;
        ValidationSummaryText.Text = $"Выполнено {completed} из {_state.Entries.Count}; "
            + $"пройдено {_state.Entries.Count(static entry => entry.Status == ValidationCheckStatus.Passed)}, "
            + $"ошибок {_state.Entries.Count(static entry => entry.Status == ValidationCheckStatus.Failed)}. Прогресс сохраняется автоматически.";
        _isPopulating = false;
    }

    private void SetCurrentStatus(ValidationCheckStatus status)
    {
        var entries = _state.Entries.ToArray();
        entries[_currentIndex] = entries[_currentIndex] with
        {
            Status = status,
            Notes = ValidationNotesTextBox.Text?.Trim() ?? string.Empty,
        };
        _state = new ValidationChecklistState(DateTimeOffset.Now, entries);
        _store.Save(_state);
        if (_currentIndex < entries.Length - 1)
        {
            _currentIndex++;
        }

        ShowCurrentStep();
    }

    private void Passed_Click(object? sender, RoutedEventArgs e) => SetCurrentStatus(ValidationCheckStatus.Passed);
    private void Failed_Click(object? sender, RoutedEventArgs e) => SetCurrentStatus(ValidationCheckStatus.Failed);
    private void Skipped_Click(object? sender, RoutedEventArgs e) => SetCurrentStatus(ValidationCheckStatus.Skipped);

    private void Previous_Click(object? sender, RoutedEventArgs e)
    {
        SaveNotes();
        _currentIndex = Math.Max(0, _currentIndex - 1);
        ShowCurrentStep();
    }

    private void Next_Click(object? sender, RoutedEventArgs e)
    {
        SaveNotes();
        _currentIndex = Math.Min(_state.Entries.Count - 1, _currentIndex + 1);
        ShowCurrentStep();
    }

    private void ValidationNotesTextBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_isPopulating)
        {
            SaveNotes();
        }
    }

    private void SaveNotes()
    {
        if (_isPopulating)
        {
            return;
        }

        var entries = _state.Entries.ToArray();
        entries[_currentIndex] = entries[_currentIndex] with { Notes = ValidationNotesTextBox.Text?.Trim() ?? string.Empty };
        _state = new ValidationChecklistState(DateTimeOffset.Now, entries);
        _store.Save(_state);
    }

    private async void SaveReport_Click(object? sender, RoutedEventArgs e)
    {
        SaveNotes();
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Сохранить отчёт самопроверки Easyaller",
            SuggestedFileName = $"Easyaller-validation-{Environment.MachineName}-{DateTime.Today:yyyy-MM-dd}.txt",
            DefaultExtension = "txt",
            FileTypeChoices = [new FilePickerFileType("Текстовый отчёт") { Patterns = ["*.txt"] }],
        });
        if (file is not null)
        {
            await File.WriteAllTextAsync(file.Path.LocalPath, ValidationChecklistStore.BuildReport(_state, Environment.MachineName));
        }
    }

    private async void Reset_Click(object? sender, RoutedEventArgs e)
    {
        if (!await ConfirmActionWindow.AskAsync(
            this,
            "Начать самопроверку заново?",
            "Все отметки и заметки текущего чек-листа будут удалены.",
            "Сохраните отчёт перед сбросом, если он нужен.",
            "Сбросить чек-лист"))
        {
            return;
        }

        _store.Clear();
        _state = _store.LoadOrCreate();
        _currentIndex = 0;
        ShowCurrentStep();
    }

    private static int FindFirstPendingIndex(ValidationChecklistState state)
    {
        for (var index = 0; index < state.Entries.Count; index++)
        {
            if (state.Entries[index].Status == ValidationCheckStatus.Pending)
            {
                return index;
            }
        }

        return 0;
    }

    private static string DescribeStatus(ValidationCheckStatus status) => status switch
    {
        ValidationCheckStatus.Passed => "пройдено",
        ValidationCheckStatus.Failed => "ошибка",
        ValidationCheckStatus.Skipped => "пропущено",
        _ => "не проверено",
    };
}
