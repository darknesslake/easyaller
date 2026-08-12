using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace Easyaller.App;

public sealed partial class UsbCreatorWindow : Window
{
    private static readonly FilePickerFileType WindowsIsoFileType = new("Образ Windows (ISO)")
    {
        Patterns = ["*.iso"],
        MimeTypes = ["application/x-iso9660-image"],
    };

    private readonly UsbCreationController _controller;
    private readonly ObservableCollection<UsbCreatorCandidate> _candidates = [];
    private readonly WindowsIsoMount _isoMount = new();
    private UsbCreatorCandidate? _selectedCandidate;
    private UsbCreatorPreparationResult? _preparation;

    public UsbCreatorWindow()
        : this(new UsbCreationController())
    {
    }

    public UsbCreatorWindow(UsbCreationController controller, string? suggestedIsoPath = null)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        InitializeComponent();
        CandidatesList.ItemsSource = _candidates;
        PrepareConfirmationButton.IsEnabled = false;
        WriteUsbButton.IsEnabled = false;
        RefreshCandidates();

        if (!string.IsNullOrWhiteSpace(suggestedIsoPath))
        {
            UseSetupIso(suggestedIsoPath);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        // Only an image this window mounted itself is released here.
        _isoMount.Dispose();
        base.OnClosed(e);
    }

    private async void ChooseSetupIso_Click(object? sender, RoutedEventArgs e)
    {
        if (!StorageProvider.CanOpen)
        {
            SetStatus("Выбор файла недоступен на этой платформе.");
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Выберите ISO Windows",
            AllowMultiple = false,
            FileTypeFilter = [WindowsIsoFileType],
        });
        if (files.FirstOrDefault() is { } file)
        {
            UseSetupIso(file.Path.LocalPath);
        }
    }

    private void UseSetupIso(string isoPath)
    {
        SetupIsoStatusText.IsVisible = true;
        SetupIsoStatusText.Text = $"Монтируется {Path.GetFileName(isoPath)} только для чтения…";
        ChooseSetupIsoButton.IsEnabled = false;
        try
        {
            var result = _isoMount.Mount(isoPath);
            if (!result.IsMounted)
            {
                SetupIsoStatusText.Text = "Не удалось смонтировать ISO: " + (result.ErrorMessage ?? "неизвестная ошибка");
                return;
            }

            SetupMediaDirectoryTextBox.Text = result.Root;
            SetupIsoStatusText.Text = $"{Path.GetFileName(isoPath)} смонтирован как {result.Root} только для чтения. Образ будет отключён при закрытии окна.";
            ClearPreparation();
            UpdatePrepareButton();
        }
        finally
        {
            ChooseSetupIsoButton.IsEnabled = true;
        }
    }

    private void RefreshCandidates_Click(object? sender, RoutedEventArgs e) => RefreshCandidates();

    private void CandidatesList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        _selectedCandidate = CandidatesList.SelectedItem as UsbCreatorCandidate;
        ClearPreparation();
        UpdatePrepareButton();
        if (_selectedCandidate is not null)
        {
            SetStatus($"Выбран диск {_selectedCandidate.Disk.DiskNumber}. Источники ещё не проверены.");
        }
    }

    private async void ChooseSetupMedia_Click(object? sender, RoutedEventArgs e)
    {
        var directory = await PickDirectoryAsync("Выберите каталог распакованного Windows Setup");
        if (directory is null)
        {
            return;
        }

        SetupMediaDirectoryTextBox.Text = directory;
        ClearPreparation();
        UpdatePrepareButton();
    }

    private async void ChooseDeploymentPackage_Click(object? sender, RoutedEventArgs e)
    {
        var directory = await PickDirectoryAsync("Выберите экспортированный package Easyaller");
        if (directory is null)
        {
            return;
        }

        DeploymentPackageDirectoryTextBox.Text = directory;
        ClearPreparation();
        UpdatePrepareButton();
    }

    private void PrepareConfirmation_Click(object? sender, RoutedEventArgs e)
    {
        if (_selectedCandidate is null)
        {
            SetStatus("Сначала явно выберите один съёмный диск.");
            return;
        }

        _preparation = _controller.Prepare(
            _selectedCandidate.Disk,
            SetupMediaDirectoryTextBox.Text ?? string.Empty,
            DeploymentPackageDirectoryTextBox.Text ?? string.Empty);
        if (!_preparation.IsReadyForPhrase)
        {
            ConfirmationDetailsText.Text = "Подтверждение заблокировано: " + GetMessage(_preparation.Errors);
            SetStatus(ConfirmationDetailsText.Text);
            return;
        }

        var prompt = _preparation.Confirmation!.Prompt;
        ConfirmationDetailsText.Text =
            $"Будет использован диск {prompt.DiskNumber}: {prompt.Vendor}; ID: {prompt.SerialOrDeviceId}; размер: {prompt.SizeBytes} байт. " +
            $"Подтверждение действует до {prompt.ExpiresAt.LocalDateTime:t}. Введите строго {prompt.RequiredPhrase}.";
        ErasePhraseTextBox.Text = string.Empty;
        WriteUsbButton.IsEnabled = true;
        SetStatus($"План содержит {_preparation.Plan!.Files.Count} файлов. Ожидается точное подтверждение ERASE.");
    }

    private async void WriteUsb_Click(object? sender, RoutedEventArgs e)
    {
        if (_preparation is null || !_preparation.IsReadyForPhrase)
        {
            SetStatus("Сначала подготовьте подтверждение для выбранного диска.");
            return;
        }

        WriteUsbButton.IsEnabled = false;
        var plan = _preparation.Plan!;
        var confirmation = _preparation.Confirmation!;
        var typedPhrase = ErasePhraseTextBox.Text;
        ErasePhraseTextBox.Text = string.Empty;
        var result = await Task.Run(() => _controller.Write(plan, confirmation, typedPhrase));
        if (!result.IsReady)
        {
            SetStatus("Создание USB не выполнено: " + GetMessage(result.Errors));
            if (confirmation.Status == Easyaller.Deployment.UsbDestructiveConfirmationStatus.AwaitingTypedPhrase)
            {
                WriteUsbButton.IsEnabled = true;
            }

            return;
        }

        ConfirmationDetailsText.Text = "Установочный USB создан и проверен по SHA-256. Перед использованием сохраните результаты теста отдельно от Git.";
        SetStatus($"USB готов: проверено файлов {plan.Files.Count}.");
        _preparation = null;
    }

    private void RefreshCandidates()
    {
        var result = _controller.RefreshCandidates();
        _candidates.Clear();
        foreach (var candidate in result.Candidates)
        {
            _candidates.Add(candidate);
        }

        _selectedCandidate = null;
        CandidatesList.SelectedItem = null;
        ClearPreparation();
        UpdatePrepareButton();
        SetStatus(result.Errors.Count > 0
            ? "Не удалось получить список съёмных дисков: " + GetMessage(result.Errors)
            : _candidates.Count == 0
                ? "Подходящих съёмных дисков не найдено. Внутренние и небезопасные диски не показываются."
                : $"Найдено подходящих съёмных дисков: {_candidates.Count}. Выберите один вручную.");
    }

    private async Task<string?> PickDirectoryAsync(string title)
    {
        if (!StorageProvider.CanOpen)
        {
            SetStatus("Выбор папки недоступен на этой платформе.");
            return null;
        }

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
        });
        return folders.FirstOrDefault()?.Path.LocalPath;
    }

    private void UpdatePrepareButton() =>
        PrepareConfirmationButton.IsEnabled = _selectedCandidate is not null &&
            !string.IsNullOrWhiteSpace(SetupMediaDirectoryTextBox.Text) &&
            !string.IsNullOrWhiteSpace(DeploymentPackageDirectoryTextBox.Text);

    private void ClearPreparation()
    {
        _preparation = null;
        ErasePhraseTextBox.Text = string.Empty;
        WriteUsbButton.IsEnabled = false;
        ConfirmationDetailsText.Text = "Сначала выберите диск и пройдите проверку источников.";
    }

    private void SetStatus(string text) => UsbStatusText.Text = text;

    private static string GetMessage(IReadOnlyList<Easyaller.Deployment.DeploymentValidationError> errors) =>
        errors.FirstOrDefault()?.Message ?? "Повторите попытку.";
}
