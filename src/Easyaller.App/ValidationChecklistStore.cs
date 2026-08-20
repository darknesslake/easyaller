using System.Text;
using System.Text.Json;

namespace Easyaller.App;

public enum ValidationCheckStatus
{
    Pending,
    Passed,
    Failed,
    Skipped,
}

public sealed record ValidationChecklistEntry(
    string Id,
    string Title,
    string Instructions,
    string ExpectedResult,
    ValidationCheckStatus Status = ValidationCheckStatus.Pending,
    string Notes = "");

public sealed record ValidationChecklistState(DateTimeOffset UpdatedAt, IReadOnlyList<ValidationChecklistEntry> Entries);

public sealed class ValidationChecklistStore
{
    private readonly string _filePath;

    public ValidationChecklistStore(string filePath) => _filePath = filePath;

    public ValidationChecklistState LoadOrCreate()
    {
        try
        {
            if (File.Exists(_filePath)
                && JsonSerializer.Deserialize<ValidationChecklistState>(File.ReadAllText(_filePath)) is { } saved)
            {
                var savedById = saved.Entries.ToDictionary(static entry => entry.Id, StringComparer.Ordinal);
                return new ValidationChecklistState(
                    saved.UpdatedAt,
                    CreateDefaultEntries().Select(entry => savedById.TryGetValue(entry.Id, out var existing)
                        ? entry with { Status = existing.Status, Notes = existing.Notes }
                        : entry).ToArray());
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
        }

        return new ValidationChecklistState(DateTimeOffset.Now, CreateDefaultEntries());
    }

    public void Save(ValidationChecklistState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        var temporaryPath = _filePath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporaryPath, _filePath, overwrite: true);
    }

    public void Clear()
    {
        try
        {
            File.Delete(_filePath);
            File.Delete(_filePath + ".tmp");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    public static string BuildReport(ValidationChecklistState state, string machineName)
    {
        var builder = new StringBuilder();
        builder.AppendLine("ОТЧЁТ САМОПРОВЕРКИ EASYALLER");
        builder.AppendLine($"Компьютер: {machineName}");
        builder.AppendLine($"Дата: {DateTimeOffset.Now.LocalDateTime:dd.MM.yyyy HH:mm:ss}");
        builder.AppendLine($"Пройдено: {state.Entries.Count(static entry => entry.Status == ValidationCheckStatus.Passed)}");
        builder.AppendLine($"Ошибок: {state.Entries.Count(static entry => entry.Status == ValidationCheckStatus.Failed)}");
        builder.AppendLine($"Пропущено: {state.Entries.Count(static entry => entry.Status == ValidationCheckStatus.Skipped)}");
        builder.AppendLine($"Не проверено: {state.Entries.Count(static entry => entry.Status == ValidationCheckStatus.Pending)}");
        builder.AppendLine();
        foreach (var entry in state.Entries)
        {
            builder.AppendLine($"[{DescribeStatus(entry.Status)}] {entry.Title}");
            builder.AppendLine($"Ожидается: {entry.ExpectedResult}");
            if (!string.IsNullOrWhiteSpace(entry.Notes))
            {
                builder.AppendLine($"Заметки: {entry.Notes.Trim()}");
            }

            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static string DescribeStatus(ValidationCheckStatus status) => status switch
    {
        ValidationCheckStatus.Passed => "ПРОЙДЕНО",
        ValidationCheckStatus.Failed => "ОШИБКА",
        ValidationCheckStatus.Skipped => "ПРОПУЩЕНО",
        _ => "НЕ ПРОВЕРЕНО",
    };

    public static IReadOnlyList<ValidationChecklistEntry> CreateDefaultEntries() =>
    [
        new("timezone", "Выборочное применение часового пояса", "Оставьте остальные runtime-поля пустыми, примените только часовой пояс и затем выполните проверку профиля.", "Часовой пояс совпадает; остальные настройки Windows не изменились."),
        new("blank-fields", "Пустые поля ничего не меняют", "Зафиксируйте текущее значение необязательной настройки, оставьте её поле пустым и примените другую операцию.", "Зафиксированное значение осталось прежним."),
        new("network", "IPv4, шлюз и DNS", "На тестовом адаптере примените значения профиля и затем выполните сверку.", "IPv4, маска, шлюз и все DNS совпадают с профилем."),
        new("computer-name", "Имя устройства", "Введите допустимое имя устройства, примените изменение, перезагрузите Windows и откройте Easyaller снова.", "После перезагрузки Windows использует новое имя устройства; очередь самопроверки сохранилась."),
        new("apps-success", "Успешная очередь программ", "Запустите очередь из двух безопасных тестовых установщиков.", "Обе программы установлены строго в заданном порядке."),
        new("apps-recovery", "Ошибка, повтор и пропуск программы", "Создайте тестовую ошибку в середине очереди; отдельно проверьте повтор и вариант «Пропустить и продолжить».", "Очередь останавливается, сохраняется и корректно продолжается выбранным способом."),
        new("apps-cancel", "Остановка активного установщика", "Запустите длительный тестовый установщик и нажмите «Остановить установку».", "Процесс завершён, текущая и оставшиеся программы доступны для продолжения."),
        new("shortcuts-current", "Ярлыки текущему пользователю", "Проверьте список и скопируйте тестовые ярлыки в режимах «не заменять» и «заменить».", "Итог по каждому файлу соответствует выбранному режиму."),
        new("shortcuts-other", "Ярлыки другому пользователю", "Выберите другой локальный профиль Windows, подтвердите доступ и скопируйте тестовый ярлык.", "Ярлык появился на рабочем столе выбранного пользователя."),
        new("outlook-preview", "Предпросмотр Outlook", "Подключите классический Outlook, выберите период и нажмите «Посчитать письма».", "Количество показано отдельно для «Входящих» и «Отправленных»; письма ещё не перемещены."),
        new("outlook-archive", "Архив Outlook и проверка PST", "Перенесите небольшую тестовую выборку в новый PST с сегодняшней датой.", "PST проверен, виден в Outlook и содержит папки «Входящие» и «Отправленные»."),
        new("outlook-cancel", "Остановка архивации Outlook", "На тестовой выборке запросите остановку во время переноса.", "Остановка произошла между письмами, частичный результат и история сохранены."),
    ];

    public static ValidationChecklistStore CreateDefault()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Easyaller");
        return new ValidationChecklistStore(Path.Combine(root, "validation-checklist.json"));
    }
}
