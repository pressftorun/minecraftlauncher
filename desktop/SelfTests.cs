using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using fNbt;

namespace CreateLauncher;

static class SelfTests
{
    static void Check(bool value, string message) { if (!value) throw new Exception("CHECK FAILED: " + message); }
    static void Reject(Action action)
    {
        try { action(); }
        catch (Exception e) when (e is InvalidDataException or ArgumentException) { return; }
        throw new Exception("Expected rejection");
    }
    static string Pack(string root, Dictionary<string, string> files, bool corrupt = false)
    {
        var path = Path.Combine(root, "client.zip");
        var hashes = files.ToDictionary(x => x.Key, x => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(x.Value))));
        using var z = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, text) in files) { using var writer = new StreamWriter(z.CreateEntry(name).Open()); writer.Write(corrupt ? "tampered" : text); }
        using var manifest = new StreamWriter(z.CreateEntry("manifest.json").Open());
        manifest.Write(System.Text.Json.JsonSerializer.Serialize(new PackManifest("1.21.1", "21.1.264", hashes), LauncherService.Json));
        return path;
    }
    public static void Run()
    {
        LocalizationTests.Run();
        DownloadTests.Run().GetAwaiter().GetResult();
        var root = Path.Combine(AppContext.BaseDirectory, "test-data-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            LauncherUpdaterTests.Run(root);
            OptionalModsTests.Run(root);
            Check(LauncherService.Offline("Notch").UUID == "b50ad385829d3141a2167e7d7539ba7f", "Offline UUID matches Java");
            foreach (var name in new[] { "ab", "a b", new string('x', 17), "../bad" }) Reject(() => LauncherService.Offline(name));
            foreach (var value in new[] { 0, 127, 128, 767, 2097152 }) { using var buffer = new MemoryStream(); LauncherService.WriteVarInt(buffer, value); buffer.Position = 0; Check(LauncherService.ReadVarInt(buffer) == value, "VarInt"); }
            Reject(() => LauncherService.ReadVarInt(new MemoryStream([128, 128, 128, 128, 128])));
            var game = Path.Combine(root, "game");
            var skinConfig = Path.Combine(game, "CustomSkinLoader", "CustomSkinLoader.json");
            Directory.CreateDirectory(Path.GetDirectoryName(skinConfig)!);
            File.WriteAllText(skinConfig, "personal skin settings");
            LauncherService.PrepareGameSkins(game);
            var skinExtra = Path.Combine(game, "CustomSkinLoader", "ExtraList", "CreateLauncher-TLauncher.json");
            using (var extra = JsonDocument.Parse(File.ReadAllText(skinExtra)))
            {
                Check(extra.RootElement.GetProperty("type").GetString() == "Legacy", "TLauncher uses direct texture loading");
                Check(extra.RootElement.GetProperty("skin").GetString() == "https://auth.tlauncher.org/skin/fileservice/skins/skin_{USERNAME}.png", "TLauncher textures use HTTPS and player name");
                Check(extra.RootElement.GetProperty("model").GetString() == "auto" && extra.RootElement.GetProperty("checkPNG").GetBoolean(), "TLauncher model detection and PNG validation");
            }
            File.Delete(skinExtra); // CustomSkinLoader consumes ExtraList files after importing them.
            LauncherService.PrepareGameSkins(game);
            Check(File.Exists(skinExtra) && File.ReadAllText(skinConfig) == "personal skin settings", "Skin setup survives consumed ExtraList and preserves user config");
            var state = Path.Combine(root, "state.json");
            var config = new LauncherConfig();
            var zip = Pack(root, new() { ["mods/old.jar"] = "old", ["optional-mods/map.jar"] = "optional map", ["optional-mods/retired.jar"] = "retired", ["config/test.json"] = "default", ["options.txt"] = "saved game defaults" });
            LauncherService.ApplyPack(zip, game, state, config);
            Check(File.ReadAllText(Path.Combine(game, "optional-mods", "map.jar")) == "optional map"
                && !File.Exists(Path.Combine(game, "mods", "map.jar")), "Optional mods install into a separate inactive folder");
            Check(File.ReadAllText(Path.Combine(game, "options.txt")) == "saved game defaults", "New client receives default Minecraft options");
            File.Delete(zip);
            File.WriteAllText(Path.Combine(game, "mods", "personal.jar"), "personal mod");
            File.WriteAllText(Path.Combine(game, "optional-mods", "personal.jar"), "personal optional mod");
            File.WriteAllText(Path.Combine(game, "optional-mods", "map.jar"), "damaged optional mod");
            File.WriteAllText(Path.Combine(game, "config", "test.json"), "personal");
            File.WriteAllText(Path.Combine(game, "options.txt"), "personal game options");
            zip = Pack(root, new() { ["mods/new.jar"] = "new", ["optional-mods/map.jar"] = "updated optional map", ["config/test.json"] = "new default", ["options.txt"] = "new game defaults" });
            LauncherService.ApplyPack(zip, game, state, config);
            Check(File.ReadAllText(Path.Combine(game, "optional-mods", "map.jar")) == "updated optional map", "Optional pack mods are repaired and updated");
            Check(!File.Exists(Path.Combine(game, "optional-mods", "retired.jar")), "Retired optional pack mods are removed");
            Check(File.ReadAllText(Path.Combine(game, "optional-mods", "personal.jar")) == "personal optional mod", "Personal optional mods survive updates");
            Check(!File.Exists(Path.Combine(game, "mods", "old.jar")), "Obsolete pack mod removed");
            Check(File.ReadAllText(Path.Combine(game, "mods", "personal.jar")) == "personal mod", "User mods preserved");
            Check(File.ReadAllText(Path.Combine(game, "config", "test.json")) == "personal", "User config preserved");
            Check(File.ReadAllText(Path.Combine(game, "options.txt")) == "personal game options", "Player options survive pack updates");
            File.Delete(zip);
            foreach (var name in new[] { "../escape", "mods/../../escape", "C:/escape", "mods\\escape", "/mods/escape", "options.txt/escape", "accounts.json" }) Reject(() => LauncherService.PackPath(game, name));
            zip = Pack(root, new() { ["mods/bad.jar"] = "expected" }, true);
            Reject(() => LauncherService.ApplyPack(zip, game, state, config));
            Check(!File.Exists(Path.Combine(game, "mods", "bad.jar")), "Checksum rejection before writing");
            LauncherService.AddServer(game, "example.org:25565", "Personal");
            LauncherService.AddServer(game, config.Server, config.Name);
            LauncherService.AddServer(game, config.Server, config.Name);
            var servers = new NbtFile(Path.Combine(game, "servers.dat")).RootTag.Get<NbtList>("servers");
            Check(servers?.Count == 2 && servers.Get<NbtCompound>(0)?.Get<NbtString>("ip")?.Value == "example.org:25565", "Server list preserved and deduplicated");
            Check(File.Exists(Path.Combine(game, "servers.dat.launcher-backup")), "Server list backed up");
            LauncherService.Write(Path.Combine(root, "launcher.json"), config);
            var service = new LauncherService(root);
            var releaseJson = "{\"draft\":false,\"prerelease\":false,\"assets\":[{\"name\":\"client.zip\",\"browser_download_url\":\"https://github.com/pressftorun/minecraftversion/releases/download/test/client.zip\",\"digest\":\"sha256:" + new string('a', 64) + "\"}]}";
            using var release = JsonDocument.Parse(releaseJson);
            var download = LauncherService.ParsePackRelease(release.RootElement);
            Check(download.Sha256 == new string('a', 64) && download.Url.EndsWith("/test/client.zip"), "GitHub release resolves an archive and its SHA-256 together");
            using var optionalRelease = JsonDocument.Parse(releaseJson.Replace("}]}", "},{\"name\":\"optional-mods.zip\",\"browser_download_url\":\"https://github.com/pressftorun/minecraftversion/releases/download/test/optional-mods.zip\",\"digest\":\"sha256:" + new string('b', 64) + "\"}]}"));
            var optionalDownload = LauncherService.ParsePackRelease(optionalRelease.RootElement, optionalOnly: true);
            Check(optionalDownload.Sha256 == new string('b', 64) && optionalDownload.Url.EndsWith("/optional-mods.zip"), "Optional refresh selects the small verified archive");
            Check(LauncherService.ParsePackRelease(optionalRelease.RootElement).FileName == "client.zip", "Full installation still selects the full archive");
            Check(LauncherService.ParsePackRelease(release.RootElement, optionalOnly: true).Url == download.Url, "Older releases without optional archive still work");
            using var badOptional = JsonDocument.Parse(optionalRelease.RootElement.GetRawText().Replace("sha256:" + new string('b', 64), "sha1:" + new string('b', 64)));
            Reject(() => LauncherService.ParsePackRelease(badOptional.RootElement, optionalOnly: true));
            using var missingDigest = JsonDocument.Parse(releaseJson.Replace("sha256:", "sha1:"));
            Reject(() => LauncherService.ParsePackRelease(missingDigest.RootElement));
            using var insecureRelease = JsonDocument.Parse(releaseJson.Replace("https://", "http://"));
            Reject(() => LauncherService.ParsePackRelease(insecureRelease.RootElement));
            var target = Path.Combine(root, "download.bin");
            File.WriteAllText(target, "previous verified archive");
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                try { LauncherService.Download("https://example.invalid/client.zip", target, cancellation: cancelled.Token).GetAwaiter().GetResult(); throw new Exception("Cancelled download succeeded"); }
                catch (OperationCanceledException) { }
            }
            Check(File.ReadAllText(target) == "previous verified archive" && !File.Exists(target + ".part"), "Cancelled download preserves the previous file and cleans temporary data");
            using (var running = Process.GetCurrentProcess())
            {
                typeof(LauncherService).GetProperty(nameof(LauncherService.GameProcess))!.SetValue(service, running);
                try { service.Install(_ => { }, _ => { }).GetAwaiter().GetResult(); throw new Exception("Install while running succeeded"); }
                catch (InvalidOperationException e) { Check(e.Message.Contains("Закройте Minecraft"), "Running game blocks installation before network or file changes"); }
                finally { typeof(LauncherService).GetProperty(nameof(LauncherService.GameProcess))!.SetValue(service, null); }
            }
            service.SavePreferences("Player", 8192, Path.Combine(root, "data", "java", "bin", "java"));
            var preferences = service.LoadPreferences();
            Check(preferences.Memory == 8 && !Path.IsPathRooted(preferences.JavaPath), "Portable Java settings");
            service.SavePreferences("Player", 7425);
            Check(service.LoadPreferences().MemoryMb == 7425, "Manual memory value is preserved without presets");
            service.SavePreferences("Player", 16384);
            Check(service.LoadPreferences().MemoryMb == 16384, "Maximum memory is 16384 MB");
            Reject(() => service.SavePreferences("Player", 16385));
            LauncherService.Write(Path.Combine(service.Data, "settings.json"), new Preferences(MemoryMb: 32768));
            Check(service.LoadPreferences().MemoryMb == 16384, "Old larger memory settings are clamped to 16384 MB");
            var first = new PlayerProfile("Player", "b50ad385829d3141a2167e7d7539ba7f", 0);
            LauncherService.Write(Path.Combine(service.Data, "profile.json"), first);
            Check(service.LoadProfiles().Count == 1, "Previous single profile migrates to saved accounts");
            service.SaveProfile(first);
            service.SaveProfile(first with { Name = "RenamedPlayer" });
            var second = first with { Name = "ElyPlayer", Provider = 1 };
            service.SaveProfile(second);
            Check(service.LoadProfiles().Count == 2 && service.LoadProfiles()[0].Name == "RenamedPlayer", "Profiles deduplicate by provider and UUID");
            Check(service.LoadProfile() == second, "Last selected account is remembered");
            service.RemoveProfile(second);
            Check(service.LoadProfiles().Count == 1 && service.LoadProfile() == null, "Selected account can be removed");
            Check(Greetings.All.Length == 20, "Twenty greeting variants");
            for (int i = 0; i < 100; i++) { int old = i % 20; Check(Greetings.Next(old) != old, "Greeting changes on next startup"); }
            Check(Greetings.Format(0, "Player").Title.Contains("Player"), "Personal greeting");
            Reject(() => LauncherService.VerifyProfile(new CmlLib.Core.Auth.MSession { Username = "Player", UUID = "bad", AccessToken = "" }));
            using var validTLauncher = System.Text.Json.JsonDocument.Parse("{\"selectedProfile\":{\"name\":\"Player\",\"id\":\"b50ad385-829d-3141-a216-7e7d7539ba7f\"},\"accessToken\":\"test-token\"}");
            var tl = LauncherService.ParseTLauncher(validTLauncher.RootElement, true);
            Check(tl.Username == "Player" && tl.UUID == "b50ad385829d3141a2167e7d7539ba7f" && tl.AccessToken == "test-token", "TLauncher profile parsing");
            using var incomplete = System.Text.Json.JsonDocument.Parse("{\"accessToken\":\"test\"}");
            Reject(() => LauncherService.ParseTLauncher(incomplete.RootElement, true));
            using var textures = System.Text.Json.JsonDocument.Parse("{\"textures\":{\"SKIN\":{\"url\":\"https://auth.tlauncher.org/skin/test.png\"}}}");
            Check(LauncherService.SkinUrl(textures.RootElement) == "https://auth.tlauncher.org/skin/test.png", "Skin metadata parsing");
            Check(LauncherService.ResolveSkinUri(2, "skin/fileservice/skins/skin_player.png").AbsoluteUri == "https://auth.tlauncher.org/skin/fileservice/skins/skin_player.png", "TLauncher relative skin URL");
            Check(LauncherService.ResolveSkinUri(1, "http://ely.by/storage/skin.png").Scheme == "https", "Skin download uses HTTPS");
            Reject(() => LauncherService.ResolveSkinUri(2, "https://example.net/skin.png"));
            Console.WriteLine("PASS: offline identity, VarInt, safe pack paths, checksums, mod updates, config preservation, NBT server list, GitHub release, download cancellation, running game guard");
        }
        finally { Directory.Delete(root, true); }
    }
}
