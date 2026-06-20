using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace TriffView.EveSettings;

internal sealed class EveSettingsController
{
    private const string BackupManifestName = "triffhud-eve-settings-backup.json";
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(8),
    };
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly Dispatcher _dispatcher;
    private readonly Action<object> _postToHud;
    private readonly EveSettingsLocalState _settings;
    private readonly Dictionary<long, string> _characterNames = new();
    private string _lastPostedStateJson = "";

    public EveSettingsController(Dispatcher dispatcher, Action<object> postToHud)
    {
        _dispatcher = dispatcher;
        _postToHud = postToHud;
        _settings = EveSettingsLocalState.Load();
        EnsureInitialFolder();
    }

    public bool HandleWebMessage(string type, JsonObject? message)
    {
        switch (type)
        {
            case "eve-settings:get-state":
            case "eve-settings:refresh":
                _ = PostStateAsync(force: true);
                return true;
            case "eve-settings:select-folder":
                SelectFolder();
                return true;
            case "eve-settings:set-server":
                SetServer(message?["serverPath"]?.GetValue<string>());
                return true;
            case "eve-settings:set-profile":
                SetProfile(message?["profilePath"]?.GetValue<string>());
                return true;
            case "eve-settings:create-profile":
                CreateProfile(message?["name"]?.GetValue<string>());
                return true;
            case "eve-settings:duplicate-profile":
                DuplicateProfile(message?["profilePath"]?.GetValue<string>(), message?["name"]?.GetValue<string>());
                return true;
            case "eve-settings:rename-profile":
                RenameProfile(message?["profilePath"]?.GetValue<string>(), message?["name"]?.GetValue<string>());
                return true;
            case "eve-settings:delete-profile":
                DeleteProfile(message?["profilePath"]?.GetValue<string>());
                return true;
            case "eve-settings:backup-profile":
                BackupProfile(message?["profilePath"]?.GetValue<string>());
                return true;
            case "eve-settings:backup-file":
                BackupFile(message?["filePath"]?.GetValue<string>());
                return true;
            case "eve-settings:copy-file":
                CopyFile(message?["sourcePath"]?.GetValue<string>(), message?["targetPath"]?.GetValue<string>());
                return true;
            case "eve-settings:copy-file-to-targets":
                CopyFileToTargets(message?["sourcePath"]?.GetValue<string>(), message?["targetPaths"] as JsonArray);
                return true;
            case "eve-settings:restore-backup":
                RestoreBackup(message?["backupPath"]?.GetValue<string>());
                return true;
            case "eve-settings:delete-backup":
                DeleteBackup(message?["backupPath"]?.GetValue<string>());
                return true;
            case "eve-settings:set-note":
                SetNote(message?["entityType"]?.GetValue<string>(), message?["entityId"]?.GetValue<string>(), message?["note"]?.GetValue<string>());
                return true;
            case "eve-settings:show-in-folder":
                ShowInFolder(message?["path"]?.GetValue<string>());
                return true;
            default:
                return false;
        }
    }

    private void EnsureInitialFolder()
    {
        if (!string.IsNullOrWhiteSpace(_settings.RootPath) && Directory.Exists(_settings.RootPath)) return;

        var defaultRoot = DefaultEveRoot();
        if (Directory.Exists(defaultRoot))
        {
            _settings.RootPath = defaultRoot;
            _settings.Save();
        }
    }

    private async Task PostStateAsync(bool force = false)
    {
        try
        {
            var state = await BuildStateAsync();
            var json = JsonSerializer.Serialize(state, JsonOptions);
            if (!force && string.Equals(json, _lastPostedStateJson, StringComparison.Ordinal)) return;
            _lastPostedStateJson = json;
            _postToHud(state);
        }
        catch (Exception ex)
        {
            PostError("state", ex.Message);
        }
    }

    private async Task<object> BuildStateAsync()
    {
        var defaultRoot = DefaultEveRoot();
        NormalizeSelectedPaths();
        var rootPath = ExistingDirectoryOrEmpty(_settings.RootPath);
        var servers = EnumerateServers(rootPath);
        var selectedServer = PickSelectedServer(servers);
        var profiles = EnumerateProfiles(selectedServer?.Path ?? "");
        var selectedProfile = PickSelectedProfile(profiles);
        var characterFiles = EnumerateSettingsFiles(selectedProfile?.Path ?? "", "core_char_*.dat", "character");
        var accountFiles = EnumerateSettingsFiles(selectedProfile?.Path ?? "", "core_user_*.dat", "account");
        var characterIds = characterFiles
            .Select(file => long.TryParse(file.Id, out var id) ? id : 0)
            .Where(id => id > 0)
            .ToArray();

        await ResolveCharacterNamesAsync(characterIds);

        return new
        {
            type = "eve-settings:state",
            defaultRoot,
            rootPath,
            selectedServerPath = selectedServer?.Path ?? "",
            selectedProfilePath = selectedProfile?.Path ?? "",
            eveRunning = IsEveRunning(),
            servers = servers.Select(server => new
            {
                path = server.Path,
                name = server.Name,
                key = server.Key,
                exists = server.Exists,
            }).ToArray(),
            profiles = profiles.Select(profile => new
            {
                path = profile.Path,
                name = profile.Name,
                fileCount = profile.FileCount,
                modifiedUtc = profile.ModifiedUtc,
                note = NoteFor("profile", profile.Path),
            }).ToArray(),
            characters = characterFiles.Select(file => new
            {
                path = file.Path,
                id = file.Id,
                name = long.TryParse(file.Id, out var id) && _characterNames.TryGetValue(id, out var name) ? name : $"Character {file.Id}",
                size = file.Size,
                modifiedUtc = file.ModifiedUtc,
                note = NoteFor("character", file.Id),
            }).ToArray(),
            accounts = accountFiles.Select(file => new
            {
                path = file.Path,
                id = file.Id,
                name = $"Account {file.Id}",
                size = file.Size,
                modifiedUtc = file.ModifiedUtc,
                note = NoteFor("account", file.Id),
            }).ToArray(),
            backups = EnumerateBackups().Select(backup => new
            {
                path = backup.Path,
                kind = backup.Kind,
                label = backup.Label,
                sourcePath = backup.SourcePath,
                createdUtc = backup.CreatedUtc,
                size = backup.Size,
            }).ToArray(),
            notes = _settings.Notes,
        };
    }

    private void SelectFolder()
    {
        try
        {
            using var dialog = new Forms.FolderBrowserDialog
            {
                Description = "Select the EVE settings folder or a server folder",
                UseDescriptionForTitle = true,
                SelectedPath = Directory.Exists(_settings.RootPath) ? _settings.RootPath : DefaultEveRoot(),
                ShowNewFolderButton = false,
            };

            if (dialog.ShowDialog() != Forms.DialogResult.OK) return;

            var selected = FullPath(dialog.SelectedPath);
            if (IsProfileDirectory(selected))
            {
                var serverPath = Directory.GetParent(selected)?.FullName ?? "";
                _settings.RootPath = Directory.GetParent(serverPath)?.FullName ?? serverPath;
                _settings.SelectedServerPath = serverPath;
                _settings.SelectedProfilePath = selected;
            }
            else if (DirectoryContainsProfiles(selected))
            {
                _settings.RootPath = Directory.GetParent(selected)?.FullName ?? selected;
                _settings.SelectedServerPath = selected;
                _settings.SelectedProfilePath = "";
            }
            else
            {
                _settings.RootPath = selected;
                _settings.SelectedServerPath = "";
                _settings.SelectedProfilePath = "";
            }
            _settings.Save();
            _ = PostStateAsync(force: true);
        }
        catch (Exception ex)
        {
            PostError("select-folder", ex.Message);
        }
    }

    private void SetServer(string? serverPath)
    {
        try
        {
            var path = ValidateDirectoryUnderRoot(serverPath, allowRoot: true);
            if (!DirectoryContainsProfiles(path)) throw new InvalidOperationException("That folder does not contain EVE settings profiles.");
            _settings.SelectedServerPath = path;
            _settings.SelectedProfilePath = "";
            _settings.Save();
            _ = PostStateAsync(force: true);
        }
        catch (Exception ex)
        {
            PostError("set-server", ex.Message);
        }
    }

    private void SetProfile(string? profilePath)
    {
        try
        {
            var path = ValidateDirectoryUnderRoot(profilePath);
            if (!IsProfileDirectory(path)) throw new InvalidOperationException("That folder is not an EVE settings profile.");
            _settings.SelectedProfilePath = path;
            _settings.Save();
            _ = PostStateAsync(force: true);
        }
        catch (Exception ex)
        {
            PostError("set-profile", ex.Message);
        }
    }

    private void CreateProfile(string? name)
    {
        try
        {
            var server = CurrentServerPath();
            var profilePath = UniqueProfilePath(server, name);
            Directory.CreateDirectory(profilePath);
            _settings.SelectedProfilePath = profilePath;
            _settings.Save();
            _ = PostStateAsync(force: true);
        }
        catch (Exception ex)
        {
            PostError("create-profile", ex.Message);
        }
    }

    private void DuplicateProfile(string? sourceProfilePath, string? name)
    {
        try
        {
            var source = ValidateDirectoryUnderRoot(sourceProfilePath);
            if (!IsProfileDirectory(source)) throw new InvalidOperationException("Choose a settings profile to duplicate.");
            var target = UniqueProfilePath(CurrentServerPath(), name);
            Directory.CreateDirectory(target);
            foreach (var file in Directory.EnumerateFiles(source, "core_*.dat", SearchOption.TopDirectoryOnly))
            {
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
            }
            _settings.SelectedProfilePath = target;
            _settings.Save();
            _ = PostStateAsync(force: true);
        }
        catch (Exception ex)
        {
            PostError("duplicate-profile", ex.Message);
        }
    }

    private void RenameProfile(string? profilePath, string? name)
    {
        try
        {
            var source = ValidateDirectoryUnderRoot(profilePath);
            if (!IsProfileDirectory(source)) throw new InvalidOperationException("Choose a settings profile to rename.");
            var server = CurrentServerPath();
            var cleanName = CleanProfileName(name);
            var target = Path.Combine(server, $"settings_{cleanName}");
            if (PathsEqual(source, target)) return;
            var index = 2;
            while (Directory.Exists(target))
            {
                target = Path.Combine(server, $"settings_{cleanName}-{index}");
                index += 1;
            }
            Directory.Move(source, target);
            if (PathsEqual(_settings.SelectedProfilePath, source)) _settings.SelectedProfilePath = target;
            _settings.Save();
            _ = PostStateAsync(force: true);
        }
        catch (Exception ex)
        {
            PostError("rename-profile", ex.Message);
        }
    }

    private void DeleteProfile(string? profilePath)
    {
        try
        {
            var source = ValidateDirectoryUnderRoot(profilePath);
            if (!IsProfileDirectory(source)) throw new InvalidOperationException("Choose a settings profile to delete.");
            BackupProfileInternal(source);
            Directory.Delete(source, recursive: true);
            if (PathsEqual(_settings.SelectedProfilePath, source)) _settings.SelectedProfilePath = "";
            _settings.Save();
            _ = PostStateAsync(force: true);
        }
        catch (Exception ex)
        {
            PostError("delete-profile", ex.Message);
        }
    }

    private void BackupProfile(string? profilePath)
    {
        try
        {
            var source = ValidateDirectoryUnderRoot(profilePath);
            if (!IsProfileDirectory(source)) throw new InvalidOperationException("Choose a settings profile to back up.");
            BackupProfileInternal(source);
            _ = PostStateAsync(force: true);
        }
        catch (Exception ex)
        {
            PostError("backup-profile", ex.Message);
        }
    }

    private void BackupFile(string? filePath)
    {
        try
        {
            var source = ValidateFileUnderRoot(filePath);
            BackupFileInternal(source);
            _ = PostStateAsync(force: true);
        }
        catch (Exception ex)
        {
            PostError("backup-file", ex.Message);
        }
    }

    private void CopyFile(string? sourcePath, string? targetPath)
    {
        try
        {
            CopyFileToTargetsInternal(sourcePath, new[] { targetPath });
            _ = PostStateAsync(force: true);
        }
        catch (Exception ex)
        {
            PostError("copy-file", ex.Message);
        }
    }

    private void CopyFileToTargets(string? sourcePath, JsonArray? targetPaths)
    {
        try
        {
            var targets = targetPaths?
                .Select(node => node?.GetValue<string>())
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .ToArray() ?? Array.Empty<string?>();

            CopyFileToTargetsInternal(sourcePath, targets);
            _ = PostStateAsync(force: true);
        }
        catch (Exception ex)
        {
            PostError("copy-file-to-targets", ex.Message);
        }
    }

    private int CopyFileToTargetsInternal(string? sourcePath, IEnumerable<string?> targetPaths)
    {
        var source = ValidateFileUnderRoot(sourcePath);
        if (!File.Exists(source)) throw new FileNotFoundException("The source settings file does not exist.", source);

        var sourceKind = CoreSettingsFileKind(source);
        if (string.IsNullOrWhiteSpace(sourceKind)) throw new InvalidOperationException("Only EVE core settings files can be copied.");

        var targets = targetPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => ValidateFileUnderRoot(path, mustExist: false))
            .Where(target => !PathsEqual(source, target))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (targets.Length == 0) throw new InvalidOperationException("Choose at least one target settings file.");

        foreach (var target in targets)
        {
            var targetKind = CoreSettingsFileKind(target);
            if (!string.Equals(sourceKind, targetKind, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Character settings can only be copied to character settings, and account settings can only be copied to account settings.");
            }
        }

        foreach (var target in targets)
        {
            if (File.Exists(target)) BackupFileInternal(target);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: true);
        }

        return targets.Length;
    }

    private void RestoreBackup(string? backupPath)
    {
        try
        {
            var backup = ValidateBackupPath(backupPath);
            using var archive = ZipFile.OpenRead(backup);
            var manifest = ReadManifest(archive) ?? throw new InvalidDataException("This backup has no TriffHUD manifest.");
            if (string.Equals(manifest.Kind, "profile", StringComparison.OrdinalIgnoreCase))
            {
                var target = ValidateDirectoryUnderRoot(manifest.SourcePath, allowMissing: true);
                if (Directory.Exists(target)) BackupProfileInternal(target);
                Directory.CreateDirectory(target);
                foreach (var existing in Directory.EnumerateFiles(target, "core_*.dat", SearchOption.TopDirectoryOnly))
                {
                    File.Delete(existing);
                }
                foreach (var entry in archive.Entries.Where(entry => !string.Equals(entry.FullName, BackupManifestName, StringComparison.OrdinalIgnoreCase)))
                {
                    entry.ExtractToFile(Path.Combine(target, Path.GetFileName(entry.FullName)), overwrite: true);
                }
            }
            else
            {
                var target = ValidateFileUnderRoot(manifest.SourcePath, mustExist: false);
                if (File.Exists(target)) BackupFileInternal(target);
                var fileEntry = archive.Entries.FirstOrDefault(entry => !string.Equals(entry.FullName, BackupManifestName, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidDataException("This file backup is empty.");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                fileEntry.ExtractToFile(target, overwrite: true);
            }

            _ = PostStateAsync(force: true);
        }
        catch (Exception ex)
        {
            PostError("restore-backup", ex.Message);
        }
    }

    private void DeleteBackup(string? backupPath)
    {
        try
        {
            var backup = ValidateBackupPath(backupPath);
            File.Delete(backup);
            _ = PostStateAsync(force: true);
        }
        catch (Exception ex)
        {
            PostError("delete-backup", ex.Message);
        }
    }

    private void SetNote(string? entityType, string? entityId, string? note)
    {
        try
        {
            var type = CleanKey(entityType);
            var id = (entityId ?? "").Trim();
            if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(id)) return;
            var key = NoteKey(type, id);
            var value = note?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(value)) _settings.Notes.Remove(key);
            else _settings.Notes[key] = value;
            _settings.Save();
            _ = PostStateAsync(force: true);
        }
        catch (Exception ex)
        {
            PostError("set-note", ex.Message);
        }
    }

    private void ShowInFolder(string? path)
    {
        try
        {
            var full = FullPath(path);
            if (!CanRevealPath(full)) throw new InvalidOperationException("That path is outside the selected EVE settings folder.");
            if (File.Exists(full))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{full}\"") { UseShellExecute = true });
                return;
            }
            if (Directory.Exists(full))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{full}\"") { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            PostError("show-in-folder", ex.Message);
        }
    }

    private bool CanRevealPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var root = FullPath(_settings.RootPath);
        return (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root) && IsUnder(path, root))
            || IsUnder(path, BackupRoot());
    }

    private string BackupProfileInternal(string profilePath)
    {
        var source = FullPath(profilePath);
        var backupPath = NewBackupPath("profile", Path.GetFileName(source));
        using var archive = ZipFile.Open(backupPath, ZipArchiveMode.Create);
        var manifest = new EveSettingsBackupManifest
        {
            Kind = "profile",
            SourcePath = source,
            Label = ProfileDisplayName(source),
            CreatedUtc = DateTime.UtcNow,
        };
        WriteManifest(archive, manifest);

        foreach (var file in Directory.EnumerateFiles(source, "core_*.dat", SearchOption.TopDirectoryOnly))
        {
            archive.CreateEntryFromFile(file, Path.GetFileName(file), CompressionLevel.Optimal);
        }

        return backupPath;
    }

    private void NormalizeSelectedPaths()
    {
        var root = ExistingDirectoryOrEmpty(_settings.RootPath);
        var selectedServer = ExistingDirectoryOrEmpty(_settings.SelectedServerPath);
        var selectedProfile = ExistingDirectoryOrEmpty(_settings.SelectedProfilePath);
        var changed = false;

        if (IsProfileDirectory(root))
        {
            var serverPath = Directory.GetParent(root)?.FullName ?? "";
            var rootPath = Directory.GetParent(serverPath)?.FullName ?? serverPath;
            _settings.RootPath = rootPath;
            _settings.SelectedServerPath = serverPath;
            _settings.SelectedProfilePath = root;
            changed = true;
        }
        else if (DirectoryContainsProfiles(root))
        {
            _settings.RootPath = Directory.GetParent(root)?.FullName ?? root;
            _settings.SelectedServerPath = root;
            changed = true;
        }
        else if (!string.IsNullOrWhiteSpace(selectedProfile) && IsProfileDirectory(selectedProfile))
        {
            var serverPath = Directory.GetParent(selectedProfile)?.FullName ?? "";
            if (!string.IsNullOrWhiteSpace(serverPath) && DirectoryContainsProfiles(serverPath))
            {
                _settings.SelectedServerPath = serverPath;
                if (string.IsNullOrWhiteSpace(root) || !IsUnder(serverPath, root))
                {
                    _settings.RootPath = Directory.GetParent(serverPath)?.FullName ?? serverPath;
                }
                changed = true;
            }
        }
        else if (!string.IsNullOrWhiteSpace(selectedServer) && DirectoryContainsProfiles(selectedServer))
        {
            if (string.IsNullOrWhiteSpace(root) || !IsUnder(selectedServer, root))
            {
                _settings.RootPath = Directory.GetParent(selectedServer)?.FullName ?? selectedServer;
                changed = true;
            }
        }

        if (changed) _settings.Save();
    }

    private string BackupFileInternal(string filePath)
    {
        var source = FullPath(filePath);
        var backupPath = NewBackupPath("file", Path.GetFileName(source));
        using var archive = ZipFile.Open(backupPath, ZipArchiveMode.Create);
        var manifest = new EveSettingsBackupManifest
        {
            Kind = "file",
            SourcePath = source,
            Label = Path.GetFileName(source),
            CreatedUtc = DateTime.UtcNow,
        };
        WriteManifest(archive, manifest);
        archive.CreateEntryFromFile(source, Path.GetFileName(source), CompressionLevel.Optimal);
        return backupPath;
    }

    private static void WriteManifest(ZipArchive archive, EveSettingsBackupManifest manifest)
    {
        var entry = archive.CreateEntry(BackupManifestName, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(JsonSerializer.Serialize(manifest, JsonOptions));
    }

    private static EveSettingsBackupManifest? ReadManifest(ZipArchive archive)
    {
        var entry = archive.GetEntry(BackupManifestName);
        if (entry == null) return null;
        using var stream = entry.Open();
        return JsonSerializer.Deserialize<EveSettingsBackupManifest>(stream, JsonOptions);
    }

    private IReadOnlyList<EveSettingsBackupInfo> EnumerateBackups()
    {
        var backupRoot = BackupRoot();
        if (!Directory.Exists(backupRoot)) return Array.Empty<EveSettingsBackupInfo>();

        return Directory
            .EnumerateFiles(backupRoot, "*.zip", SearchOption.TopDirectoryOnly)
            .Select(ReadBackupInfo)
            .Where(info => info != null)
            .Select(info => info!)
            .OrderByDescending(info => info.CreatedUtc)
            .ToArray();
    }

    private static EveSettingsBackupInfo? ReadBackupInfo(string path)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            var manifest = ReadManifest(archive);
            if (manifest == null) return null;
            var file = new FileInfo(path);
            return new EveSettingsBackupInfo(
                FullPath(path),
                manifest.Kind,
                manifest.Label,
                manifest.SourcePath,
                manifest.CreatedUtc,
                file.Length
            );
        }
        catch
        {
            return null;
        }
    }

    private static string NewBackupPath(string kind, string sourceName)
    {
        var backupRoot = BackupRoot();
        Directory.CreateDirectory(backupRoot);
        var cleanName = CleanFileSegment(sourceName);
        return Path.Combine(backupRoot, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{kind}-{cleanName}.zip");
    }

    private IReadOnlyList<EveSettingsServerInfo> EnumerateServers(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath)) return Array.Empty<EveSettingsServerInfo>();
        var root = FullPath(rootPath);
        var servers = new List<EveSettingsServerInfo>();

        TryAddServer(root, servers);

        foreach (var directory in SafeEnumerateDirectories(root, "*"))
        {
            TryAddServer(directory, servers);
        }

        var selectedServer = ExistingDirectoryOrEmpty(_settings.SelectedServerPath);
        if (!string.IsNullOrWhiteSpace(selectedServer) && IsUnder(selectedServer, root))
        {
            TryAddServer(selectedServer, servers);
        }

        return servers
            .GroupBy(server => server.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderByDescending(server => string.Equals(server.Key, "tranquility", StringComparison.OrdinalIgnoreCase))
            .ThenBy(server => server.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void TryAddServer(string directory, ICollection<EveSettingsServerInfo> servers)
    {
        if (!Directory.Exists(directory)) return;
        if (!DirectoryContainsProfiles(directory) && !LooksLikeEveServerFolder(directory)) return;
        servers.Add(ServerInfo(directory));
    }

    private EveSettingsServerInfo? PickSelectedServer(IReadOnlyList<EveSettingsServerInfo> servers)
    {
        if (servers.Count == 0) return null;
        var selected = ExistingDirectoryOrEmpty(_settings.SelectedServerPath);
        var match = servers.FirstOrDefault(server => PathsEqual(server.Path, selected));
        if (match != null) return match;

        match = servers.FirstOrDefault(server => string.Equals(server.Key, "tranquility", StringComparison.OrdinalIgnoreCase));
        match ??= servers[0];
        _settings.SelectedServerPath = match.Path;
        _settings.Save();
        return match;
    }

    private IReadOnlyList<EveSettingsProfileInfo> EnumerateProfiles(string serverPath)
    {
        if (string.IsNullOrWhiteSpace(serverPath) || !Directory.Exists(serverPath)) return Array.Empty<EveSettingsProfileInfo>();
        return SafeEnumerateDirectories(serverPath, "settings_*")
            .Select(directory =>
            {
                var info = new DirectoryInfo(directory);
                return new EveSettingsProfileInfo(
                    FullPath(directory),
                    ProfileDisplayName(directory),
                    Directory.EnumerateFiles(directory, "core_*.dat", SearchOption.TopDirectoryOnly).Count(),
                    info.LastWriteTimeUtc
                );
            })
            .OrderByDescending(profile => string.Equals(profile.Name, "Default", StringComparison.OrdinalIgnoreCase))
            .ThenBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private EveSettingsProfileInfo? PickSelectedProfile(IReadOnlyList<EveSettingsProfileInfo> profiles)
    {
        if (profiles.Count == 0) return null;
        var selected = ExistingDirectoryOrEmpty(_settings.SelectedProfilePath);
        var match = profiles.FirstOrDefault(profile => PathsEqual(profile.Path, selected));
        if (match != null) return match;

        match = profiles.FirstOrDefault(profile => string.Equals(profile.Name, "Default", StringComparison.OrdinalIgnoreCase));
        match ??= profiles[0];
        _settings.SelectedProfilePath = match.Path;
        _settings.Save();
        return match;
    }

    private static IReadOnlyList<EveSettingsFileInfo> EnumerateSettingsFiles(string profilePath, string pattern, string kind)
    {
        if (string.IsNullOrWhiteSpace(profilePath) || !Directory.Exists(profilePath)) return Array.Empty<EveSettingsFileInfo>();
        return SafeEnumerateFiles(profilePath, pattern)
            .Select(path =>
            {
                var file = new FileInfo(path);
                return TryExtractCoreId(file.Name, kind, out var id)
                    ? new EveSettingsFileInfo(
                    FullPath(path),
                    id,
                    file.Length,
                    file.LastWriteTimeUtc
                    )
                    : null;
            })
            .Where(file => file != null)
            .Select(file => file!)
            .OrderBy(file => file.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task ResolveCharacterNamesAsync(IEnumerable<long> ids)
    {
        var missing = ids
            .Where(id => id > 0 && !_characterNames.ContainsKey(id))
            .Distinct()
            .Take(250)
            .ToArray();
        if (missing.Length == 0) return;

        try
        {
            using var content = new StringContent(JsonSerializer.Serialize(missing), Encoding.UTF8, "application/json");
            using var response = await Http.PostAsync("https://esi.evetech.net/latest/universe/names/?datasource=tranquility", content);
            if (!response.IsSuccessStatusCode) return;
            var json = await response.Content.ReadAsStringAsync();
            if (JsonNode.Parse(json) is not JsonArray array) return;

            foreach (var node in array.OfType<JsonObject>())
            {
                var id = node["id"]?.GetValue<long>() ?? 0;
                var name = node["name"]?.GetValue<string>()?.Trim() ?? "";
                if (id > 0 && !string.IsNullOrWhiteSpace(name)) _characterNames[id] = name;
            }
        }
        catch
        {
            // Character names are nice-to-have; raw IDs keep the tool usable offline.
        }
    }

    private string CurrentServerPath()
    {
        var servers = EnumerateServers(_settings.RootPath);
        return PickSelectedServer(servers)?.Path ?? throw new InvalidOperationException("Select an EVE settings folder first.");
    }

    private string ValidateDirectoryUnderRoot(string? path, bool allowRoot = false, bool allowMissing = false)
    {
        var root = FullPath(_settings.RootPath);
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) throw new InvalidOperationException("Select an EVE settings folder first.");
        var full = FullPath(path);
        if (!allowMissing && !Directory.Exists(full)) throw new DirectoryNotFoundException("Folder not found.");
        if (!IsUnder(full, root)) throw new InvalidOperationException("That folder is outside the selected EVE settings root.");
        if (!allowRoot && PathsEqual(full, root)) throw new InvalidOperationException("Choose a profile or server folder, not the root folder.");
        return full;
    }

    private string ValidateFileUnderRoot(string? path, bool mustExist = true)
    {
        var root = FullPath(_settings.RootPath);
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) throw new InvalidOperationException("Select an EVE settings folder first.");
        var full = FullPath(path);
        if (mustExist && !File.Exists(full)) throw new FileNotFoundException("Settings file not found.", full);
        var parent = Path.GetDirectoryName(full) ?? "";
        if (!IsUnder(parent, root)) throw new InvalidOperationException("That file is outside the selected EVE settings root.");
        if (!full.EndsWith(".dat", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Only .dat settings files are supported.");
        return full;
    }

    private static string ValidateBackupPath(string? path)
    {
        var full = FullPath(path);
        if (!File.Exists(full)) throw new FileNotFoundException("Backup not found.", full);
        if (!IsUnder(full, BackupRoot())) throw new InvalidOperationException("That backup is outside TriffHUD's EVE settings backup folder.");
        if (!full.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Only TriffHUD ZIP backups are supported.");
        return full;
    }

    private static bool IsUnder(string candidate, string root)
    {
        var fullCandidate = FullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullRoot = FullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return fullCandidate.Equals(fullRoot, StringComparison.OrdinalIgnoreCase)
            || fullCandidate.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || fullCandidate.StartsWith(fullRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        return string.Equals(FullPath(left), FullPath(right), StringComparison.OrdinalIgnoreCase);
    }

    private static string FullPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
    }

    private static string ExistingDirectoryOrEmpty(string? path)
    {
        var full = FullPath(path);
        return Directory.Exists(full) ? full : "";
    }

    private static bool DirectoryContainsProfiles(string path)
    {
        return Directory.Exists(path) && SafeEnumerateDirectories(path, "settings_*").Any();
    }

    private static bool IsProfileDirectory(string path)
    {
        return Directory.Exists(path) && Path.GetFileName(path).StartsWith("settings_", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeEveServerFolder(string path)
    {
        var name = Path.GetFileName(path).ToLowerInvariant();
        return name.Contains("tranquil")
            || name.Contains("serenity")
            || name.Contains("singulari")
            || name.Contains("duality")
            || name.Contains("thunderdome")
            || name.Contains("infinity")
            || name.Contains("buckshot")
            || name.Contains("tornado");
    }

    private static EveSettingsServerInfo ServerInfo(string path)
    {
        var name = Path.GetFileName(path);
        var key = ServerKey(name);
        return new EveSettingsServerInfo(FullPath(path), ServerDisplayName(name, key), key, Directory.Exists(path));
    }

    private static string ServerKey(string folderName)
    {
        var lower = folderName.ToLowerInvariant();
        if (lower.Contains("tranquil")) return "tranquility";
        if (lower.Contains("serenity")) return "serenity";
        if (lower.Contains("singulari")) return "singularity";
        if (lower.Contains("duality")) return "duality";
        if (lower.Contains("thunderdome")) return "thunderdome";
        if (lower.Contains("infinity")) return "infinity";
        if (lower.Contains("buckshot")) return "buckshot";
        if (lower.Contains("tornado")) return "tornado";
        return folderName;
    }

    private static string ServerDisplayName(string folderName, string key)
    {
        return key switch
        {
            "tranquility" => "Tranquility",
            "serenity" => "Serenity",
            "singularity" => "Singularity",
            "duality" => "Duality",
            "thunderdome" => "Thunderdome",
            "infinity" => "Infinity",
            "buckshot" => "Buckshot",
            "tornado" => "Tornado",
            _ => folderName,
        };
    }

    private static string ProfileDisplayName(string path)
    {
        var name = Path.GetFileName(path);
        return name.StartsWith("settings_", StringComparison.OrdinalIgnoreCase)
            ? name["settings_".Length..]
            : name;
    }

    private static bool TryExtractCoreId(string fileName, string kind, out string id)
    {
        var prefix = kind == "account" ? "core_user_" : "core_char_";
        id = "";
        if (!fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        if (!fileName.EndsWith(".dat", StringComparison.OrdinalIgnoreCase)) return false;
        var candidate = fileName[prefix.Length..^4];
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Any(ch => !char.IsDigit(ch))) return false;
        id = candidate;
        return true;
    }

    private static string CoreSettingsFileKind(string path)
    {
        var fileName = Path.GetFileName(path);
        if (TryExtractCoreId(fileName, "character", out _)) return "character";
        if (TryExtractCoreId(fileName, "account", out _)) return "account";
        return "";
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string path, string pattern)
    {
        try
        {
            return Directory.EnumerateDirectories(path, pattern, SearchOption.TopDirectoryOnly).ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static IEnumerable<string> SafeEnumerateFiles(string path, string pattern)
    {
        try
        {
            return Directory.EnumerateFiles(path, pattern, SearchOption.TopDirectoryOnly).ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static string UniqueProfilePath(string serverPath, string? rawName)
    {
        var baseName = CleanProfileName(rawName);
        var candidate = Path.Combine(serverPath, $"settings_{baseName}");
        var index = 2;
        while (Directory.Exists(candidate))
        {
            candidate = Path.Combine(serverPath, $"settings_{baseName}-{index}");
            index += 1;
        }
        return candidate;
    }

    private static string CleanProfileName(string? rawName)
    {
        var value = (rawName ?? "").Trim();
        if (string.IsNullOrWhiteSpace(value)) value = "TriffHUD";
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(value.Select(ch => invalid.Contains(ch) ? '-' : ch).ToArray()).Trim();
        clean = clean.Replace("settings_", "", StringComparison.OrdinalIgnoreCase).Trim(' ', '.', '-');
        return string.IsNullOrWhiteSpace(clean) ? "TriffHUD" : clean;
    }

    private static string CleanFileSegment(string? rawName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string((rawName ?? "settings").Select(ch => invalid.Contains(ch) ? '-' : ch).ToArray()).Trim(' ', '.', '-');
        return string.IsNullOrWhiteSpace(clean) ? "settings" : clean;
    }

    private static string CleanKey(string? value)
    {
        return new string((value ?? "").Where(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '_').ToArray()).ToLowerInvariant();
    }

    private string NoteFor(string type, string id)
    {
        return _settings.Notes.TryGetValue(NoteKey(type, id), out var note) ? note : "";
    }

    private static string NoteKey(string type, string id)
    {
        return $"{CleanKey(type)}:{id.Trim()}";
    }

    private static string StableKey(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(FullPath(value).ToLowerInvariant()));
        return Convert.ToHexString(bytes).ToLowerInvariant()[..16];
    }

    private static bool IsEveRunning()
    {
        try
        {
            return Process.GetProcesses().Any(process =>
            {
                try
                {
                    return string.Equals(process.ProcessName, "exefile", StringComparison.OrdinalIgnoreCase);
                }
                catch
                {
                    return false;
                }
            });
        }
        catch
        {
            return false;
        }
    }

    private static string DefaultEveRoot()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "CCP", "EVE");
    }

    private static string BackupRoot()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TriffHud", "EveSettings", "backups");
    }

    private void PostError(string action, string message)
    {
        _postToHud(new
        {
            type = "eve-settings:error",
            action,
            message,
        });
    }
}

internal sealed class EveSettingsLocalState
{
    public string RootPath { get; set; } = "";
    public string SelectedServerPath { get; set; } = "";
    public string SelectedProfilePath { get; set; } = "";
    public Dictionary<string, string> Notes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TriffHud",
        "eve-settings.json"
    );

    public static EveSettingsLocalState Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new EveSettingsLocalState();
            var state = JsonSerializer.Deserialize<EveSettingsLocalState>(File.ReadAllText(SettingsPath), EveSettingsControllerJson.Options);
            return state?.Normalize() ?? new EveSettingsLocalState();
        }
        catch
        {
            return new EveSettingsLocalState();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(Normalize(), EveSettingsControllerJson.Options), new UTF8Encoding(false));
    }

    private EveSettingsLocalState Normalize()
    {
        RootPath = RootPath?.Trim() ?? "";
        SelectedServerPath = SelectedServerPath?.Trim() ?? "";
        SelectedProfilePath = SelectedProfilePath?.Trim() ?? "";
        Notes = Notes == null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(Notes, StringComparer.OrdinalIgnoreCase);
        return this;
    }
}

internal static class EveSettingsControllerJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };
}

internal sealed record EveSettingsServerInfo(string Path, string Name, string Key, bool Exists);
internal sealed record EveSettingsProfileInfo(string Path, string Name, int FileCount, DateTime ModifiedUtc);
internal sealed record EveSettingsFileInfo(string Path, string Id, long Size, DateTime ModifiedUtc);
internal sealed record EveSettingsBackupInfo(string Path, string Kind, string Label, string SourcePath, DateTime CreatedUtc, long Size);

internal sealed class EveSettingsBackupManifest
{
    public string Kind { get; set; } = "";
    public string Label { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}
