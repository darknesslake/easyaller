using System.Runtime.InteropServices;

#pragma warning disable CA1416 // All COM entry points are guarded by IsAvailable/EnsureAvailable at runtime.

namespace Easyaller.App;

public sealed record OutlookMailFolder(string Name, string FolderPath, string EntryId, string StoreId)
{
    public string DisplayName => $"{FolderPath} — {Name}";
}

public sealed record OutlookArchivePreview(int MatchingMessages, DateTime OlderThan, string SourceFolder);

public sealed record OutlookArchiveFailure(string ItemDescription, string Error);

public sealed record OutlookArchiveResult(
    string FolderName,
    int ProcessedMessages,
    int MovedMessages,
    string ArchivePath,
    IReadOnlyList<OutlookArchiveFailure> Failures,
    bool WasCancelled)
{
    public IReadOnlyList<string> Errors => Failures
        .Select(static failure => $"{failure.ItemDescription}: {failure.Error}")
        .ToArray();
}

public sealed record OutlookArchiveVerification(
    string ArchivePath,
    long FileSizeBytes,
    IReadOnlyList<string> FolderNames);

public sealed record OutlookArchiveProgress(
    string FolderName,
    int ProcessedMessages,
    int TotalMessages,
    int MovedMessages,
    int FailedMessages);

public enum OutlookArchiveAge
{
    AllTime,
    TwoWeeks,
    OneMonth,
    ThreeMonths,
}

public sealed record StandardOutlookFolders(OutlookMailFolder Inbox, OutlookMailFolder SentItems)
{
    public IReadOnlyList<OutlookMailFolder> All => [Inbox, SentItems];
}

public sealed class OutlookArchiveService
{
    private const int OutlookMailItemClass = 43;
    private const int UnicodeStore = 3;
    private const int InboxFolderType = 6;
    private const int SentMailFolderType = 5;

    public bool IsAvailable => OperatingSystem.IsWindows() && Type.GetTypeFromProgID("Outlook.Application") is not null;

    public IReadOnlyList<OutlookMailFolder> GetMailFolders()
    {
        EnsureAvailable();
        object? application = null;
        object? session = null;
        var result = new List<OutlookMailFolder>();
        try
        {
            application = Activator.CreateInstance(Type.GetTypeFromProgID("Outlook.Application")!);
            dynamic outlook = application!;
            session = outlook.Session;
            dynamic nameSpace = session;
            var stores = nameSpace.Stores;
            try
            {
                for (var index = 1; index <= stores.Count; index++)
                {
                    object? store = null;
                    object? root = null;
                    try
                    {
                        store = stores.Item(index);
                        dynamic dynamicStore = store;
                        root = dynamicStore.GetRootFolder();
                        CollectFolders(root, dynamicStore.StoreID, result);
                    }
                    finally
                    {
                        Release(root);
                        Release(store);
                    }
                }
            }
            finally
            {
                Release(stores);
            }

            return result.OrderBy(static folder => folder.FolderPath, StringComparer.CurrentCultureIgnoreCase).ToArray();
        }
        finally
        {
            Release(session);
            Release(application);
        }
    }

    public StandardOutlookFolders GetStandardMailFolders()
    {
        EnsureAvailable();
        object? application = null;
        object? session = null;
        object? inbox = null;
        object? sent = null;
        try
        {
            application = Activator.CreateInstance(Type.GetTypeFromProgID("Outlook.Application")!);
            dynamic outlook = application!;
            session = outlook.Session;
            dynamic nameSpace = session;
            inbox = nameSpace.GetDefaultFolder(InboxFolderType);
            sent = nameSpace.GetDefaultFolder(SentMailFolderType);
            return new StandardOutlookFolders(ToMailFolder(inbox), ToMailFolder(sent));
        }
        finally
        {
            Release(sent);
            Release(inbox);
            Release(session);
            Release(application);
        }
    }

    public OutlookArchivePreview Preview(OutlookMailFolder folder, DateTime olderThan)
    {
        ArgumentNullException.ThrowIfNull(folder);
        EnsureCutoff(olderThan);
        return WithFolder(folder, source =>
            new OutlookArchivePreview(FindMatchingEntryIds(source, olderThan).Count, olderThan, folder.FolderPath));
    }

    public OutlookArchiveResult Archive(
        OutlookMailFolder folder,
        DateTime olderThan,
        string archivePath,
        string targetFolderName,
        IProgress<OutlookArchiveProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folder);
        EnsureCutoff(olderThan);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetFolderName);
        if (!string.Equals(Path.GetExtension(archivePath), ".pst", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Файл архива должен иметь расширение .pst.", nameof(archivePath));
        }

        var fullPath = Path.GetFullPath(archivePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        return WithSession((session, source) =>
        {
            dynamic nameSpace = session;
            if (FindStoreRootByPath(nameSpace, fullPath) is not object existingRoot)
            {
                nameSpace.AddStoreEx(fullPath, UnicodeStore);
            }
            else
            {
                Release(existingRoot);
            }
            object? archiveRoot = null;
            object? target = null;
            var failures = new List<OutlookArchiveFailure>();
            var moved = 0;
            var processed = 0;
            var wasCancelled = false;
            try
            {
                archiveRoot = FindStoreRootByPath(nameSpace, fullPath)
                    ?? throw new InvalidOperationException("Outlook подключил архив, но его хранилище не найдено.");
                SetArchiveDisplayName(archiveRoot, Path.GetFileNameWithoutExtension(fullPath));
                target = GetOrCreateChildFolder(archiveRoot, SanitizeFolderName(targetFolderName));
                var entryIds = FindMatchingEntryIds(source, olderThan);
                progress?.Report(new OutlookArchiveProgress(targetFolderName, 0, entryIds.Count, 0, 0));
                foreach (var entryId in entryIds)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        wasCancelled = true;
                        break;
                    }

                    object? item = null;
                    object? movedItem = null;
                    var itemDescription = $"Письмо {processed + 1}";
                    try
                    {
                        item = nameSpace.GetItemFromID(entryId, folder.StoreId);
                        dynamic mail = item;
                        itemDescription = GetItemDescription(mail, itemDescription);
                        movedItem = mail.Move(target);
                        moved++;
                    }
                    catch (Exception exception) when (exception is COMException or InvalidCastException)
                    {
                        failures.Add(new OutlookArchiveFailure(itemDescription, exception.Message));
                    }
                    finally
                    {
                        Release(movedItem);
                        Release(item);
                    }

                    processed++;
                    progress?.Report(new OutlookArchiveProgress(
                        targetFolderName,
                        processed,
                        entryIds.Count,
                        moved,
                        failures.Count));
                }

                return new OutlookArchiveResult(
                    targetFolderName,
                    processed,
                    moved,
                    fullPath,
                    failures,
                    wasCancelled);
            }
            finally
            {
                Release(target);
                Release(archiveRoot);
            }
        }, folder);
    }

    public OutlookArchiveVerification VerifyArchive(string archivePath, params string[] requiredFolderNames)
    {
        if (!string.Equals(Path.GetExtension(archivePath), ".pst", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Файл архива должен иметь расширение .pst.", nameof(archivePath));
        }

        var fullPath = Path.GetFullPath(archivePath);
        if (!File.Exists(fullPath))
        {
            throw new InvalidOperationException("Файл PST не найден после архивации.");
        }

        var fileSize = new FileInfo(fullPath).Length;
        if (fileSize <= 0)
        {
            throw new InvalidOperationException("Созданный файл PST пуст.");
        }

        EnsureAvailable();
        object? application = null;
        object? session = null;
        object? root = null;
        try
        {
            application = Activator.CreateInstance(Type.GetTypeFromProgID("Outlook.Application")!);
            dynamic outlook = application!;
            session = outlook.Session;
            dynamic nameSpace = session;
            root = FindStoreRootByPath(nameSpace, fullPath)
                ?? throw new InvalidOperationException("PST создан, но Outlook не видит подключённое хранилище.");
            var folderNames = GetChildFolderNames(root);
            var missing = requiredFolderNames
                .Where(required => !folderNames.Contains(required, StringComparer.CurrentCultureIgnoreCase))
                .ToArray();
            if (missing.Length > 0)
            {
                throw new InvalidOperationException("В PST отсутствуют папки: " + string.Join(", ", missing));
            }

            return new OutlookArchiveVerification(fullPath, fileSize, folderNames);
        }
        finally
        {
            Release(root);
            Release(session);
            Release(application);
        }
    }

    private static string GetItemDescription(dynamic mail, string fallback)
    {
        try
        {
            var subject = ((string?)mail.Subject)?.Trim();
            return string.IsNullOrWhiteSpace(subject) ? fallback : $"«{subject}»";
        }
        catch (COMException)
        {
            return fallback;
        }
    }

    private static IReadOnlyList<string> GetChildFolderNames(object rootObject)
    {
        dynamic root = rootObject;
        object? foldersObject = null;
        var names = new List<string>();
        try
        {
            foldersObject = root.Folders;
            dynamic folders = foldersObject;
            for (var index = 1; index <= folders.Count; index++)
            {
                object? child = null;
                try
                {
                    child = folders.Item(index);
                    dynamic folder = child;
                    names.Add((string)folder.Name);
                }
                finally
                {
                    Release(child);
                }
            }

            return names;
        }
        finally
        {
            Release(foldersObject);
        }
    }

    private T WithFolder<T>(OutlookMailFolder folder, Func<dynamic, T> action) =>
        WithSession((_, source) => action(source), folder);

    private T WithSession<T>(Func<dynamic, dynamic, T> action, OutlookMailFolder folder)
    {
        EnsureAvailable();
        object? application = null;
        object? session = null;
        object? source = null;
        try
        {
            application = Activator.CreateInstance(Type.GetTypeFromProgID("Outlook.Application")!);
            dynamic outlook = application!;
            session = outlook.Session;
            dynamic nameSpace = session;
            source = nameSpace.GetFolderFromID(folder.EntryId, folder.StoreId);
            return action(nameSpace, source);
        }
        finally
        {
            Release(source);
            Release(session);
            Release(application);
        }
    }

    private static List<string> FindMatchingEntryIds(dynamic source, DateTime olderThan)
    {
        var result = new List<string>();
        object? items = null;
        try
        {
            items = source.Items;
            dynamic collection = items;
            for (var index = 1; index <= collection.Count; index++)
            {
                object? item = null;
                try
                {
                    item = collection.Item(index);
                    dynamic candidate = item;
                    if ((int)candidate.Class == OutlookMailItemClass && (DateTime)candidate.ReceivedTime < olderThan)
                    {
                        result.Add((string)candidate.EntryID);
                    }
                }
                catch (COMException)
                {
                    // An inaccessible or non-mail item is not part of the archive operation.
                }
                finally
                {
                    Release(item);
                }
            }
        }
        finally
        {
            Release(items);
        }

        return result;
    }

    private static void CollectFolders(object folderObject, string storeId, List<OutlookMailFolder> destination)
    {
        dynamic folder = folderObject;
        try
        {
            destination.Add(new OutlookMailFolder((string)folder.Name, (string)folder.FolderPath, (string)folder.EntryID, storeId));
        }
        catch (COMException)
        {
            // Some special Search Folders do not expose all properties.
        }

        object? children = null;
        try
        {
            children = folder.Folders;
            dynamic folders = children;
            for (var index = 1; index <= folders.Count; index++)
            {
                object? child = null;
                try
                {
                    child = folders.Item(index);
                    CollectFolders(child, storeId, destination);
                }
                finally
                {
                    Release(child);
                }
            }
        }
        catch (COMException)
        {
            // Ignore a branch Outlook does not allow us to enumerate.
        }
        finally
        {
            Release(children);
        }
    }

    private static OutlookMailFolder ToMailFolder(object folderObject)
    {
        dynamic folder = folderObject;
        dynamic store = folder.Store;
        try
        {
            return new OutlookMailFolder(
                (string)folder.Name,
                (string)folder.FolderPath,
                (string)folder.EntryID,
                (string)store.StoreID);
        }
        finally
        {
            Release(store);
        }
    }

    private static object? FindStoreRootByPath(dynamic nameSpace, string archivePath)
    {
        object? stores = null;
        try
        {
            stores = nameSpace.Stores;
            dynamic collection = stores;
            for (var index = 1; index <= collection.Count; index++)
            {
                object? store = null;
                try
                {
                    store = collection.Item(index);
                    dynamic candidate = store;
                    var filePath = (string?)candidate.FilePath;
                    if (!string.IsNullOrEmpty(filePath)
                        && string.Equals(Path.GetFullPath(filePath), archivePath, StringComparison.OrdinalIgnoreCase))
                    {
                        return candidate.GetRootFolder();
                    }
                }
                finally
                {
                    Release(store);
                }
            }

            return null;
        }
        finally
        {
            Release(stores);
        }
    }

    private static object GetOrCreateChildFolder(object parentObject, string name)
    {
        dynamic parent = parentObject;
        object? foldersObject = null;
        try
        {
            foldersObject = parent.Folders;
            dynamic folders = foldersObject;
            for (var index = 1; index <= folders.Count; index++)
            {
                object? child = null;
                try
                {
                    child = folders.Item(index);
                    dynamic candidate = child;
                    if (string.Equals((string)candidate.Name, name, StringComparison.CurrentCultureIgnoreCase))
                    {
                        return child;
                    }
                }
                finally
                {
                    if (child is not null)
                    {
                        try
                        {
                            dynamic candidate = child;
                            if (!string.Equals((string)candidate.Name, name, StringComparison.CurrentCultureIgnoreCase))
                            {
                                Release(child);
                            }
                        }
                        catch (COMException)
                        {
                            Release(child);
                        }
                    }
                }
            }

            return folders.Add(name);
        }
        finally
        {
            Release(foldersObject);
        }
    }

    public static DateTime CalculateCutoff(DateTime now, int olderThanMonths)
    {
        if (olderThanMonths is < 1 or > 120)
        {
            throw new ArgumentOutOfRangeException(nameof(olderThanMonths));
        }

        return now.AddMonths(-olderThanMonths);
    }

    public static DateTime CalculateCutoff(DateTime now, OutlookArchiveAge age) => age switch
    {
        OutlookArchiveAge.AllTime => now,
        OutlookArchiveAge.TwoWeeks => now.AddDays(-14),
        OutlookArchiveAge.OneMonth => now.AddMonths(-1),
        OutlookArchiveAge.ThreeMonths => now.AddMonths(-3),
        _ => throw new ArgumentOutOfRangeException(nameof(age)),
    };

    public static string GetDefaultArchivePath(DateTime today, string documentsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentsDirectory);
        var localizedDirectory = Path.Combine(documentsDirectory, "Файлы Outlook");
        var englishDirectory = Path.Combine(documentsDirectory, "Outlook Files");
        var archiveDirectory = Directory.Exists(localizedDirectory)
            ? localizedDirectory
            : Directory.Exists(englishDirectory)
                ? englishDirectory
                : localizedDirectory;
        return Path.Combine(archiveDirectory, $"{today:dd.MM.yyyy}.pst");
    }

    private static void SetArchiveDisplayName(object archiveRoot, string displayName)
    {
        dynamic root = archiveRoot;
        if (!string.Equals((string)root.Name, displayName, StringComparison.CurrentCulture))
        {
            root.Name = displayName;
        }
    }

    private static string SanitizeFolderName(string name)
    {
        var invalid = new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' };
        return string.Concat(name.Select(character => invalid.Contains(character) ? '_' : character)).Trim();
    }

    private static void EnsureCutoff(DateTime cutoff)
    {
        if (cutoff >= DateTime.Now)
        {
            throw new ArgumentOutOfRangeException(nameof(cutoff), "Дата архивации должна быть в прошлом.");
        }
    }

    private void EnsureAvailable()
    {
        if (!IsAvailable)
        {
            throw new PlatformNotSupportedException("Классический Microsoft Outlook не установлен или недоступен.");
        }
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }
}
