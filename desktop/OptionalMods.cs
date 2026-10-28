using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CreateLauncher;

internal record OptionalMod(string Id, string Name, string Version, string FileName, string? Error = null);
internal record OptionalModsState(HashSet<string> Enabled, Dictionary<string, string> Managed);

internal static class OptionalMods
{
    static string Hash(string file) { using var stream = File.OpenRead(file); return Convert.ToHexStringLower(SHA256.HashData(stream)); }
    static string StatePath(LauncherService service) => Path.Combine(service.Data, "optional-mods.json");
    public static OptionalModsState Load(LauncherService service) => File.Exists(StatePath(service))
        ? LauncherService.Read<OptionalModsState>(StatePath(service)) : new([], new());

    public static void SetEnabled(LauncherService service, string id, bool enabled)
    {
        if (service.IsGameRunning) throw new InvalidOperationException("Закрой Minecraft перед изменением модов.");
        if (!Scan(service).Any(m => m.Id == id && m.Error == null)) throw new InvalidDataException("Мод отсутствует или повреждён");
        var state = Load(service);
        if (enabled) state.Enabled.Add(id); else state.Enabled.Remove(id);
        LauncherService.Write(StatePath(service), state);
    }

    public static List<OptionalMod> Scan(LauncherService service)
    {
        var directory = LauncherService.PackPath(service.Game, "optional-mods/.scan");
        directory = Path.GetDirectoryName(directory)!;
        Directory.CreateDirectory(directory);
        var result = new List<OptionalMod>();
        foreach (var file in Directory.EnumerateFiles(directory).Where(p => p.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)))
        {
            var name = Path.GetFileName(file);
            try
            {
                LauncherService.PackPath(service.Game, "optional-mods/" + name);
                using var zip = ZipFile.OpenRead(file);
                var entry = zip.GetEntry("META-INF/neoforge.mods.toml") ?? zip.GetEntry("META-INF/mods.toml") ?? zip.GetEntry("fabric.mod.json");
                if (entry == null || entry.Length > 262144) throw new InvalidDataException("Нет поддерживаемых метаданных мода");
                using var reader = new StreamReader(entry.Open());
                var text = reader.ReadToEnd();
                string id, display, version;
                if (entry.FullName.EndsWith(".json"))
                {
                    using var json = JsonDocument.Parse(text);
                    id = json.RootElement.GetProperty("id").GetString()!;
                    display = json.RootElement.TryGetProperty("name", out var title) ? title.GetString() ?? id : id;
                    version = json.RootElement.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "";
                }
                else
                {
                    var body = Regex.Match(text, @"(?ms)^\s*\[\[mods\]\][^\r\n]*\r?\n(.*?)(?=^\s*\[|\z)").Groups[1].Value;
                    string Field(string key)
                    {
                        var value = Regex.Match(body, "(?m)^\\s*" + key + "\\s*=\\s*(\"(?:\\\\.|[^\"\\\\])*\"|'[^']*')").Groups[1].Value;
                        return value.StartsWith('"') ? JsonSerializer.Deserialize<string>(value) ?? "" : value.Trim('\'');
                    }
                    id = Field("modId"); display = Field("displayName"); version = Field("version");
                }
                if (!Regex.IsMatch(id, "^[a-z][a-z0-9_-]{1,63}$")) throw new InvalidDataException("Не удалось прочитать ID мода");
                if (string.IsNullOrWhiteSpace(display) || display.Contains("${")) display = id;
                if (version.Contains("${")) version = "";
                result.Add(new(id, display, version, name));
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException)
            { result.Add(new(name, name, "", name, error.Message)); }
        }
        foreach (var group in result.Where(m => m.Error == null).GroupBy(m => m.Id).Where(g => g.Count() > 1))
            foreach (var mod in group.ToArray()) result[result.IndexOf(mod)] = mod with { Error = "Несколько версий одного мода. Оставь одну." };
        return result.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static void Apply(LauncherService service)
    {
        if (service.IsGameRunning) throw new InvalidOperationException("Закрой Minecraft перед изменением модов.");
        var state = Load(service);
        var mods = Scan(service);
        if (mods.Any(m => m.Error != null && state.Enabled.Contains(m.Id))) throw new InvalidDataException("Включённый опциональный мод повреждён");
        var desired = mods.Where(m => m.Error == null && state.Enabled.Contains(m.Id)).ToDictionary(m => m.FileName,
            m => Hash(LauncherService.PackPath(service.Game, "optional-mods/" + m.FileName)));
        string Target(string name)
        {
            if (name.Contains('/') || !name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Некорректное имя опционального мода");
            return LauncherService.PackPath(service.Game, "mods/" + name);
        }
        foreach (var (name, digest) in state.Managed)
            if (File.Exists(Target(name)) && Hash(Target(name)) != digest
                && (!desired.TryGetValue(name, out var desiredDigest) || Hash(Target(name)) != desiredDigest)) throw new IOException("Изменена копия опционального мода в mods/: " + name);
        foreach (var name in desired.Keys)
            if (File.Exists(Target(name)) && !state.Managed.ContainsKey(name)) throw new IOException("Файл уже существует в mods/: " + name + ". Убери дубликат перед включением.");
        // Persist ownership before copying so an interrupted operation can be retried safely.
        var pending = desired.Concat(state.Managed).GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.Last().Value);
        LauncherService.Write(StatePath(service), state with { Managed = pending });
        foreach (var (name, digest) in desired)
        {
            var target = Target(name);
            if (File.Exists(target) && Hash(target) == digest) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var temporary = LauncherService.PackPath(service.Game, "mods/" + name + ".optional-tmp");
            try
            {
                File.Copy(LauncherService.PackPath(service.Game, "optional-mods/" + name), temporary, true);
                if (Hash(temporary) != digest) throw new IOException("Мод изменился во время копирования: " + name);
                File.Move(temporary, target, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        foreach (var name in state.Managed.Keys.Where(n => !desired.ContainsKey(n)))
            if (File.Exists(Target(name))) File.Delete(Target(name));
        LauncherService.Write(StatePath(service), state with { Managed = desired });
    }
}
