using System.IO.Compression;
using System.Text.Json;

namespace CreateLauncher;

internal static class LauncherUpdaterTests
{
    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("UPDATE CHECK FAILED: " + message);
    }

    static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new Exception("Unsafe update accepted");
    }

    public static void Run(string root)
    {
        var json = """
            {"draft":false,"prerelease":false,"tag_name":"v1.2.3","assets":[
              {"name":"CreateLauncher-1.2.3-win-x64.zip",
               "browser_download_url":"https://github.com/pressftorun/minecraftlauncher/releases/download/v1.2.3/CreateLauncher-1.2.3-win-x64.zip",
               "digest":"sha256:HASH"}]}
            """.Replace("HASH", new string('a', 64));
        using var release = JsonDocument.Parse(json);
        Check(LauncherUpdater.ParseRelease(release.RootElement, new Version(1, 2, 2, 0), "win-x64")?.Version == new Version(1, 2, 3), "New release selected");
        Check(LauncherUpdater.ParseRelease(release.RootElement, new Version(1, 2, 3, 0), "win-x64") == null, "Same release does not update again");
        Check(LauncherUpdater.ParseRelease(release.RootElement, new Version(2, 0, 0, 0), "win-x64") == null, "Downgrades rejected");
        using var preview = JsonDocument.Parse(json.Replace("\"prerelease\":false", "\"prerelease\":true"));
        Check(LauncherUpdater.ParseRelease(preview.RootElement, new Version(1, 0, 0), "win-x64") == null, "Prereleases ignored");
        foreach (var unsafeJson in new[] { json.Replace("sha256:", "sha1:"), json.Replace("https://github.com/", "https://example.org/") })
        {
            using var invalid = JsonDocument.Parse(unsafeJson);
            Reject(() => LauncherUpdater.ParseRelease(invalid.RootElement, new Version(1, 0, 0), "win-x64"));
        }
        var workspace = Path.Combine(root, "updater");
        Directory.CreateDirectory(workspace);
        Check(LauncherUpdater.SafePath(workspace + Path.DirectorySeparatorChar, "data/update.zip")
            == Path.Combine(workspace, "data", "update.zip"), "Installation paths with a trailing separator are accepted");
        string MakeZip(string name, params (string Path, string Text)[] files)
        {
            var path = Path.Combine(workspace, name);
            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
            foreach (var file in files) { using var writer = new StreamWriter(zip.CreateEntry(file.Path).Open()); writer.Write(file.Text); }
            return path;
        }
        var archive = MakeZip("valid.zip", (LauncherUpdater.Executable, "new launcher"), ("libTest.dll", "new library"),
            ("NOTICE.md", "dependency license notices"), ("launcher.json", "release defaults"), ("client.zip", "bundled client"));
        var stage = Path.Combine(workspace, "stage");
        var files = LauncherUpdater.Extract(archive, stage);
        Check(files.Length == 3 && !File.Exists(Path.Combine(stage, "launcher.json")) && !File.Exists(Path.Combine(stage, "client.zip")), "Release defaults and game archive stay untouched");
        foreach (var path in new[] { "../escape", "data/settings.json", "Assets/../../escape", "C:/escape", "Assets\\escape" })
        {
            var unsafeZip = MakeZip(Guid.NewGuid() + ".zip", (LauncherUpdater.Executable, "new launcher"), (path, "unwanted"));
            var unsafeStage = Path.Combine(workspace, Guid.NewGuid().ToString("N"));
            Reject(() => LauncherUpdater.Extract(unsafeZip, unsafeStage));
            Check(!Directory.Exists(unsafeStage), "Entire archive validated before extraction");
        }
        var duplicate = MakeZip("duplicate.zip", (LauncherUpdater.Executable, "one"), (LauncherUpdater.Executable, "two"));
        Reject(() => LauncherUpdater.Extract(duplicate, Path.Combine(workspace, "duplicate")));
        var target = Path.Combine(workspace, "installed");
        Directory.CreateDirectory(Path.Combine(target, "data"));
        File.WriteAllText(Path.Combine(target, LauncherUpdater.Executable), "old launcher");
        File.WriteAllText(Path.Combine(target, "launcher.json"), "personal server");
        File.WriteAllText(Path.Combine(target, "data", "settings.json"), "personal settings");
        LauncherUpdater.ReplaceFiles(stage, target, Path.Combine(workspace, "backup"), files);
        Check(File.ReadAllText(Path.Combine(target, LauncherUpdater.Executable)) == "new launcher"
            && File.ReadAllText(Path.Combine(target, "NOTICE.md")) == "dependency license notices"
            && File.ReadAllText(Path.Combine(target, "launcher.json")) == "personal server"
            && File.ReadAllText(Path.Combine(target, "data", "settings.json")) == "personal settings", "Replacement preserves settings and data");
        File.WriteAllText(Path.Combine(target, LauncherUpdater.Executable), "old launcher");
        File.WriteAllText(Path.Combine(target, "NOTICE.md"), "old license notices");
        Directory.CreateDirectory(Path.Combine(target, "blocked.dll"));
        File.WriteAllText(Path.Combine(stage, "blocked.dll"), "library");
        try
        {
            LauncherUpdater.ReplaceFiles(stage, target, Path.Combine(workspace, "rollback"), ["NOTICE.md", LauncherUpdater.Executable, "blocked.dll"]);
            throw new Exception("Expected copy failure");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        Check(File.ReadAllText(Path.Combine(target, LauncherUpdater.Executable)) == "old launcher", "A failed replacement rolls back earlier files");
        Check(File.ReadAllText(Path.Combine(target, "NOTICE.md")) == "old license notices", "A failed replacement rolls back license notices");
        Reject(() => LauncherUpdater.ReplaceFiles(stage, target, Path.Combine(workspace, "unsafe-backup"), ["data/settings.json"]));
        Console.WriteLine("PASS: launcher release selection, SHA-256 requirement, update paths, data preservation and rollback");
    }
}
