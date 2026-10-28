using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace CreateLauncher;

internal static class OptionalModsTests
{
    static void Check(bool value, string message) { if (!value) throw new Exception("OPTIONAL MOD CHECK FAILED: " + message); }
    public static void Run(string parent)
    {
        var root = Path.Combine(parent, "optional-mod-tests");
        LauncherService.Write(Path.Combine(root, "launcher.json"), new LauncherConfig());
        var service = new LauncherService(root);
        var folder = Path.Combine(service.Game, "optional-mods");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(Path.Combine(service.Game, "mods"));
        var required = Path.Combine(service.Game, "mods", "required.jar");
        File.WriteAllText(required, "required mod");
        void Jar(string file, string version)
        {
            using var archive = ZipFile.Open(Path.Combine(folder, file), ZipArchiveMode.Create);
            using var writer = new StreamWriter(archive.CreateEntry("META-INF/neoforge.mods.toml").Open());
            writer.Write($"modLoader = \"javafml\"\n[[mods]]\nmodId = \"examplemap\"\ndisplayName = \"Example Map\"\nversion = \"{version}\"\n[[dependencies.examplemap]]\nmodId = \"neoforge\"\n");
        }
        Jar("map-1.jar", "1.0");
        var mod = OptionalMods.Scan(service).Single();
        Check(mod.Id == "examplemap" && mod.Name == "Example Map" && mod.Version == "1.0", "Metadata imported from JAR");
        OptionalMods.Apply(service);
        var active = Path.Combine(service.Game, "mods", "map-1.jar");
        Check(!File.Exists(active), "Optional mod starts disabled");
        OptionalMods.SetEnabled(service, mod.Id, true);
        OptionalMods.Apply(service);
        Check(File.Exists(active) && File.Exists(Path.Combine(folder, "map-1.jar")) && OptionalMods.Load(service).Enabled.Contains(mod.Id), "Enabled mod copied and preference persisted");
        File.Delete(Path.Combine(folder, "map-1.jar"));
        Jar("map-2.jar", "2.0");
        OptionalMods.Apply(service);
        active = Path.Combine(service.Game, "mods", "map-2.jar");
        Check(File.Exists(active) && !File.Exists(Path.Combine(service.Game, "mods", "map-1.jar")), "Version update keeps choice and removes old managed copy");
        OptionalMods.SetEnabled(service, mod.Id, false);
        OptionalMods.Apply(service);
        Check(!File.Exists(active) && File.ReadAllText(required) == "required mod", "Disabling removes only the optional managed copy");
        File.WriteAllText(active, "personal file");
        OptionalMods.SetEnabled(service, mod.Id, true);
        bool conflict = false;
        try { OptionalMods.Apply(service); } catch (IOException) { conflict = true; }
        Check(conflict && File.ReadAllText(active) == "personal file", "User mods are not overwritten");
        File.Delete(active);
        File.WriteAllText(Path.Combine(folder, "broken.jar"), "not a jar");
        Check(OptionalMods.Scan(service).Single(m => m.FileName == "broken.jar").Error != null, "Broken files have an explicit error");
        File.Delete(Path.Combine(folder, "broken.jar"));
        Jar("duplicate.jar", "2.0");
        Check(OptionalMods.Scan(service).All(m => m.Error != null), "Duplicate IDs cannot be enabled");
        var archivePath = Path.Combine(root, "optional-refresh.zip");
        var content = "downloaded optional file";
        var replacement = "required mod must not change";
        var hashes = new Dictionary<string, string> {
            ["optional-mods/new.jar"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content))),
            ["mods/required.jar"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(replacement))) };
        using (var zip = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("optional-mods/new.jar").Open())) writer.Write(content);
            using (var writer = new StreamWriter(zip.CreateEntry("mods/required.jar").Open())) writer.Write(replacement);
            using var writerManifest = new StreamWriter(zip.CreateEntry("manifest.json").Open());
            writerManifest.Write(System.Text.Json.JsonSerializer.Serialize(new PackManifest("1.21.1", "21.1.264", hashes), LauncherService.Json));
        }
        LauncherService.ApplyPack(archivePath, service.Game, Path.Combine(service.Data, "pack-state.json"), service.Config, optionalOnly: true);
        Check(File.ReadAllText(required) == "required mod" && File.ReadAllText(Path.Combine(folder, "new.jar")) == content, "Refreshing optional files preserves required mods");
        File.Delete(Path.Combine(folder, "duplicate.jar"));
        File.Delete(Path.Combine(folder, "new.jar"));
        OptionalMods.Apply(service);
        var promoted = Path.Combine(service.Game, "mods", "map-2.jar");
        var promotedDigest = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(promoted)));
        LauncherService.Write(Path.Combine(service.Data, "pack-state.json"), new PackManifest("1.21.1", "21.1.264",
            new() { ["mods/map-2.jar"] = promotedDigest }));
        Check(OptionalMods.Scan(service).Count == 0, "A mod promoted to required must no longer have an optional toggle");
        OptionalMods.Apply(service);
        Check(File.Exists(promoted) && !OptionalMods.Load(service).Managed.ContainsKey("map-2.jar"),
            "Promoted required mod remains installed and is no longer owned by optional toggles");
        Console.WriteLine("PASS: optional mod discovery, toggles, persistence, version updates, conflict protection and optional-only refresh");
    }
}
