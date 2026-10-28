using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CreateLauncher;

internal record LauncherUpdate(Version Version, string Url, string Sha256);

internal static class LauncherUpdater
{
    const string Repository = "pressftorun/minecraftlauncher";
    public static Version CurrentVersion => typeof(LauncherUpdater).Assembly.GetName().Version!;
    public static string VersionLabel => $"v{CurrentVersion.ToString(3)}";
    public static string Executable => OperatingSystem.IsWindows() ? "CreateLauncher.exe" : "CreateLauncher";
    public static string Runtime => OperatingSystem.IsWindows() ? "win-x64" : "linux-x64";
    public static bool Supported => (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        && RuntimeInformation.ProcessArchitecture == Architecture.X64
        && Path.GetFileName(Environment.ProcessPath) == Executable;

    internal static LauncherUpdate? ParseRelease(JsonElement release, Version current, string runtime)
    {
        if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean()) return null;
        var tag = release.GetProperty("tag_name").GetString() ?? "";
        if (!Regex.IsMatch(tag, @"^v\d+\.\d+\.\d+$") || !Version.TryParse(tag[1..], out var version))
            throw new InvalidDataException("Неизвестный формат версии лаунчера");
        // Assembly versions contain a fourth component; compare release components only.
        if (version <= new Version(current.Major, current.Minor, current.Build)) return null;
        var name = $"CreateLauncher-{version}-{runtime}.zip";
        var asset = release.GetProperty("assets").EnumerateArray().SingleOrDefault(a => a.GetProperty("name").GetString() == name);
        if (asset.ValueKind != JsonValueKind.Object) throw new InvalidDataException("В релизе нет сборки для этой системы");
        var url = asset.GetProperty("browser_download_url").GetString() ?? "";
        var digest = asset.TryGetProperty("digest", out var hash) ? hash.GetString() ?? "" : "";
        if (!url.StartsWith($"https://github.com/{Repository}/releases/download/{tag}/", StringComparison.Ordinal)
            || !Regex.IsMatch(digest, "^sha256:[a-fA-F0-9]{64}$"))
            throw new InvalidDataException("Обновление не содержит официальный адрес и SHA-256");
        return new(version, url, digest[7..]);
    }

    public static async Task<LauncherUpdate?> Check()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases/latest");
        request.Headers.UserAgent.ParseAdd("CreateLauncher/" + CurrentVersion.ToString(3));
        using var response = await LauncherService.Http.SendAsync(request, timeout.Token);
        response.EnsureSuccessStatusCode();
        using var release = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        return ParseRelease(release.RootElement, CurrentVersion, Runtime);
    }

    internal static string SafePath(string root, string name)
    {
        var parts = name.Split('/');
        if (name.Contains('\\') || name.Contains(':') || parts.Any(p => p is "" or "." or ".."))
            throw new InvalidDataException("Недопустимый путь обновления: " + name);
        root = Path.GetFullPath(root);
        var target = Path.GetFullPath(Path.Combine(root, name));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!target.StartsWith(root + Path.DirectorySeparatorChar, comparison)) throw new InvalidDataException("Файл вне папки лаунчера");
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(target)!); dir != null && dir.FullName.Length >= root.Length; dir = dir.Parent)
            if (dir.Exists && (dir.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Ссылки в папке обновления не поддерживаются");
        if (File.Exists(target) && (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Файл обновления является ссылкой");
        return target;
    }

    static bool IsApplicationFile(string name) => name == Executable || name == "CreateLauncher.dll"
        || name is "CreateLauncher.deps.json" or "CreateLauncher.runtimeconfig.json"
        || name is "README.md" or "LICENSE" or "NOTICE.md" or "CHANGELOG.md"
        || name == "Assets/logo.ico"
        || (!name.Contains('/') && (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".so", StringComparison.Ordinal)));

    internal static string[] Extract(string archive, string stage)
    {
        using var zip = ZipFile.OpenRead(archive);
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        if (zip.Entries.Select(e => e.FullName).Distinct(comparer).Count() != zip.Entries.Count
            || zip.Entries.Sum(e => e.Length) > 1024L * 1024 * 1024) throw new InvalidDataException("Некорректный архив обновления");
        var files = new List<string>();
        foreach (var entry in zip.Entries)
        {
            SafePath(stage, entry.FullName);
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000) throw new InvalidDataException("Ссылка в архиве обновления");
            // Settings, the game and a bundled client archive belong to the player.
            if (entry.FullName is "launcher.json" or "client.zip") continue;
            if (!IsApplicationFile(entry.FullName)) throw new InvalidDataException("Неизвестный файл обновления: " + entry.FullName);
            files.Add(entry.FullName);
        }
        if (!files.Contains(Executable)) throw new InvalidDataException("В обновлении нет исполняемого файла лаунчера");
        foreach (var name in files)
        {
            var target = SafePath(stage, name);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            zip.GetEntry(name)!.ExtractToFile(target);
        }
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(SafePath(stage, Executable), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return files.ToArray();
    }

    public static async Task Start(LauncherUpdate update, string root, Action<string> report)
    {
        if (!Supported) throw new InvalidOperationException("Обновление доступно в установленной сборке лаунчера.");
        var workspace = SafePath(root, "data/launcher-updates/" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        var archive = Path.Combine(workspace, "update.zip");
        report("Скачиваем лаунчер " + update.Version + "…");
        await LauncherService.Download(update.Url, archive, update.Sha256);
        var stage = Path.Combine(workspace, "app");
        var files = Extract(archive, stage);
        // Never start a helper that cannot replace the installed files safely.
        foreach (var name in files) SafePath(root, name);
        LauncherService.Write(Path.Combine(workspace, "files.json"), files);
        var helper = new ProcessStartInfo(Path.Combine(stage, Executable)) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = stage };
        foreach (var arg in new[] { "--apply-launcher-update", Environment.ProcessId.ToString(), Path.GetFullPath(root) }) helper.ArgumentList.Add(arg);
        using var process = Process.Start(helper) ?? throw new IOException("Не удалось запустить установку обновления");
        var ready = Path.Combine(workspace, "ready");
        var deadline = Stopwatch.StartNew();
        while (!File.Exists(ready))
        {
            if (process.HasExited || deadline.Elapsed > TimeSpan.FromSeconds(10)) throw new IOException("Помощник обновления не запустился. Лаунчер продолжает работу.");
            await Task.Delay(100);
        }
    }

    internal static void ReplaceFiles(string stage, string root, string backup, string[] files)
    {
        if (files.Length == 0 || files.Any(name => !IsApplicationFile(name))
            || files.Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Length) throw new InvalidDataException("Некорректный список файлов обновления");
        foreach (var name in files) { SafePath(stage, name); SafePath(root, name); SafePath(backup, name); }
        var replaced = new List<(string Name, bool Existed)>();
        try
        {
            foreach (var name in files)
            {
                var target = SafePath(root, name);
                var saved = SafePath(backup, name);
                var existed = File.Exists(target);
                if (existed) { Directory.CreateDirectory(Path.GetDirectoryName(saved)!); File.Copy(target, saved); }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                CopyIntoPlace(SafePath(stage, name), target);
                replaced.Add((name, existed));
            }
        }
        catch
        {
            foreach (var item in replaced.AsEnumerable().Reverse())
            {
                var target = SafePath(root, item.Name);
                if (item.Existed) CopyIntoPlace(SafePath(backup, item.Name), target);
                else if (File.Exists(target)) File.Delete(target);
            }
            throw;
        }
    }

    static void CopyIntoPlace(string source, string target)
    {
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".update";
        try
        {
            File.Copy(source, temporary);
            File.Move(temporary, target, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static void Apply(int parentId, string root)
    {
        var stage = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
        var workspace = Path.GetDirectoryName(stage)!;
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var expected = SafePath(root, "data/launcher-updates");
        if (Path.GetFileName(stage) != "app" || Path.GetDirectoryName(workspace) != expected)
            throw new InvalidDataException("Помощник запущен вне папки обновления");
        try
        {
            using var parent = Process.GetProcessById(parentId);
            if (!string.Equals(parent.MainModule?.FileName, Path.Combine(root, Executable), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidDataException("Процесс лаунчера не совпадает с папкой установки");
            File.WriteAllText(Path.Combine(workspace, "ready"), "ready");
            if (!parent.WaitForExit(60000)) throw new IOException("Лаунчер не закрылся; обновление отменено");
        }
        catch (ArgumentException) { throw new IOException("Лаунчер уже закрыт; запустите проверку обновления заново"); }
        try
        {
            ReplaceFiles(stage, root, Path.Combine(workspace, "backup"), LauncherService.Read<string[]>(Path.Combine(workspace, "files.json")));
        }
        catch (Exception e)
        {
            File.WriteAllText(Path.Combine(root, "launcher-update-error.log"), e.ToString());
            Process.Start(new ProcessStartInfo(Path.Combine(root, Executable)) { UseShellExecute = false, WorkingDirectory = root });
            throw;
        }
        Process.Start(new ProcessStartInfo(Path.Combine(root, Executable)) { UseShellExecute = false, WorkingDirectory = root });
    }
}
