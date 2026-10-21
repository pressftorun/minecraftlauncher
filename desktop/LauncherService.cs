using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.Installer.Forge;
using CmlLib.Core.Installer.NeoForge;
using CmlLib.Core.Installer.Forge.Versions;
using CmlLib.Core.ProcessBuilder;
using fNbt;

namespace CreateLauncher;

public record LauncherConfig(string Name = "Create Server", string Server = "46.146.228.125:25565",
    string Minecraft = "1.21.1", string Loader = "21.1.264",
    [property: JsonPropertyName("pack_url")] string PackUrl = "",
    [property: JsonPropertyName("pack_sha256")] string PackSha256 = "",
    [property: JsonPropertyName("bluemap_url")] string BlueMapUrl = "http://46.146.228.125:8100/",
    [property: JsonPropertyName("pack_release_url")] string PackReleaseUrl = "https://api.github.com/repos/pressftorun/minecraftversion/releases/latest");
internal record PackDownload(string Url, string Sha256);
public record PackManifest(string Minecraft, string Loader, Dictionary<string, string> Files);
public record Preferences(string Nickname = "", int Memory = 6, string JavaPath = "", int MemoryMb = 0);
public record PlayerProfile(string Name, string Uuid, int Provider);
public record Installed(string Version, string Minecraft, string Loader);
public record ServerInfo(int Online, int Max);

public sealed class LauncherService
{
    public static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    public static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };
    public string Root { get; }
    public string Data => Path.Combine(Root, "data");
    public string Game => Path.Combine(Data, "minecraft");
    public LauncherConfig Config { get; }
    public bool IsInstalled => File.Exists(Path.Combine(Data, "installed.json"));
    public Process? GameProcess { get; private set; }
    public bool IsGameRunning => GameProcess is { HasExited: false };

    public LauncherService(string? root = null)
    {
        Root = Path.GetFullPath(root ?? AppContext.BaseDirectory);
        Config = Read<LauncherConfig>(Path.Combine(Root, "launcher.json"));
        foreach (var folder in new[] { Data, Game, Path.Combine(Data, "tmp"), Path.Combine(Data, "home"), Path.Combine(Data, "logs") }) Directory.CreateDirectory(folder);
        foreach (var key in new[] { "TEMP", "TMP", "TMPDIR" }) Environment.SetEnvironmentVariable(key, Path.Combine(Data, "tmp"));
    }

    public static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) ?? throw new InvalidDataException("Пустой JSON: " + Path.GetFileName(path));
    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(value, Json));
        File.Move(path + ".tmp", path, true);
    }
    public Preferences LoadPreferences()
    {
        try { var p = Read<Preferences>(Path.Combine(Data, "settings.json")); return p with { MemoryMb = Math.Clamp(p.MemoryMb > 0 ? p.MemoryMb : p.Memory * 1024, 2048, 16384) }; }
        catch (Exception e) when (e is IOException or JsonException) { return new(MemoryMb: 6144); }
    }
    public PlayerProfile? LoadProfile()
    {
        try { return Read<PlayerProfile>(Path.Combine(Data, "profile.json")); }
        catch (Exception e) when (e is IOException or JsonException) { return null; }
    }
    public static string ProfileKey(PlayerProfile profile) => profile.Provider + ":" + profile.Uuid.ToLowerInvariant();
    public List<PlayerProfile> LoadProfiles()
    {
        try { return Read<List<PlayerProfile>>(Path.Combine(Data, "profiles.json")); }
        catch (Exception e) when (e is IOException or JsonException)
        { var previous = LoadProfile(); return previous == null ? [] : [previous]; }
    }
    public void SaveProfile(PlayerProfile profile)
    {
        var profiles = LoadProfiles();
        int index = profiles.FindIndex(p => ProfileKey(p) == ProfileKey(profile));
        if (index < 0) profiles.Add(profile); else profiles[index] = profile;
        Write(Path.Combine(Data, "profiles.json"), profiles);
        Write(Path.Combine(Data, "profile.json"), profile);
    }
    public void RemoveProfile(PlayerProfile profile)
    {
        Write(Path.Combine(Data, "profiles.json"), LoadProfiles().Where(p => ProfileKey(p) != ProfileKey(profile)).ToList());
        var selected = LoadProfile();
        if (selected != null && ProfileKey(selected) == ProfileKey(profile))
        {
            File.Delete(Path.Combine(Data, "profile.json"));
            var prefs = LoadPreferences(); SavePreferences("", prefs.MemoryMb, prefs.JavaPath);
        }
    }

    public static void VerifyProfile(MSession session)
    {
        Offline(session.Username);
        if (!Guid.TryParse(session.UUID, out _) || string.IsNullOrWhiteSpace(session.AccessToken)) throw new InvalidDataException("Сервис авторизации вернул неполный профиль");
    }

    public async Task<byte[]?> GetSkin(PlayerProfile profile)
    {
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{profile.Provider}:{profile.Name}:{profile.Uuid}")));
        var cache = Path.Combine(Data, "skins", key + ".png");
        async Task<byte[]?> CachedOrFallback()
        {
            if (File.Exists(cache)) return await File.ReadAllBytesAsync(cache);
            return profile.Provider == 0 ? await GetSkin(profile with { Provider = 2 }) : null;
        }
        try
        {
            if (File.Exists(cache) && File.GetLastWriteTimeUtc(cache) > DateTime.UtcNow.AddMinutes(-10)) return await File.ReadAllBytesAsync(cache);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var metaUrl = profile.Provider == 2 ? "https://auth.tlauncher.org/skin/v1/profile/texture/login/" : "https://skinsystem.ely.by/textures/";
            using var metaResponse = await Http.GetAsync(metaUrl + Uri.EscapeDataString(profile.Name), timeout.Token);
            metaResponse.EnsureSuccessStatusCode();
            if (metaResponse.StatusCode == System.Net.HttpStatusCode.NoContent) return await CachedOrFallback();
            var metaBytes = await ReadLimited(metaResponse.Content, 65536, timeout.Token);
            using var meta = JsonDocument.Parse(metaBytes);
            var url = SkinUrl(meta.RootElement);
            if (url == null) return await CachedOrFallback();
            url = ResolveSkinUri(profile.Provider, url).AbsoluteUri;
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            var bytes = await ReadLimited(response.Content, 2097152, timeout.Token);
            if (bytes.Length < 24 || !bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) throw new InvalidDataException("Сервис не вернул PNG-скин");
            uint width = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4));
            uint height = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4));
            if (width is < 64 or > 1024 || width % 64 != 0 || height < width / 2 || height > 1024) throw new InvalidDataException("Размер скина не поддерживается");
            Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
            var temporary = cache + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { await File.WriteAllBytesAsync(temporary, bytes); File.Move(temporary, cache, true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return bytes;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException or JsonException or UriFormatException)
        { return await CachedOrFallback(); }
    }
    static async Task<byte[]> ReadLimited(HttpContent content, int maximum, CancellationToken cancellation)
    {
        if (content.Headers.ContentLength > maximum) throw new InvalidDataException("Ответ сервиса слишком большой");
        await using var stream = await content.ReadAsStreamAsync(cancellation);
        using var output = new MemoryStream();
        var buffer = new byte[16384]; int count;
        while ((count = await stream.ReadAsync(buffer, cancellation)) > 0)
        {
            if (output.Length + count > maximum) throw new InvalidDataException("Ответ сервиса слишком большой");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }
    internal static string? SkinUrl(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return null;
        if (value.TryGetProperty("textures", out var textures)) return SkinUrl(textures);
        foreach (var key in new[] { "SKIN", "skin" })
            if (value.TryGetProperty(key, out var skin) && skin.ValueKind == JsonValueKind.Object && skin.TryGetProperty("url", out var url)) return url.GetString();
        return null;
    }
    internal static Uri ResolveSkinUri(int provider, string url)
    {
        var uri = new Uri(new Uri(provider == 2 ? "https://auth.tlauncher.org/" : "https://skinsystem.ely.by/"), url);
        bool trusted = uri.Host == "textures.minecraft.net" || (provider == 2
            ? uri.Host == "auth.tlauncher.org" || uri.Host.EndsWith(".tlauncher.org") || uri.Host.EndsWith(".tlauncher.ru") || uri.Host.EndsWith(".tl.vg")
            : uri.Host == "ely.by" || uri.Host.EndsWith(".ely.by"));
        if (!trusted || uri.Scheme is not ("http" or "https")) throw new InvalidDataException("Неизвестный сервер скина");
        return new UriBuilder(uri) { Scheme = "https", Port = -1 }.Uri;
    }
    public void SavePreferences(string nick, int memoryMb, string javaPath = "")
    {
        if (memoryMb is < 2048 or > 16384) throw new ArgumentException("Память: от 2048 до 16384 МБ");
        if (!string.IsNullOrWhiteSpace(javaPath))
        {
            javaPath = Path.GetFullPath(javaPath, Root);
            if (javaPath.StartsWith(Root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) javaPath = Path.GetRelativePath(Root, javaPath);
        }
        Write(Path.Combine(Data, "settings.json"), new Preferences(nick, memoryMb / 1024, javaPath, memoryMb));
    }

    public static async Task<string> CheckJava(string path)
    {
        path = Path.GetFullPath(path);
        if (Path.GetFileName(path).Equals("javaw.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(Path.GetDirectoryName(path)!, "java.exe"))) path = Path.Combine(Path.GetDirectoryName(path)!, "java.exe");
        if (!File.Exists(path)) throw new FileNotFoundException("Не найден исполняемый файл Java");
        using var process = new Process { StartInfo = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true } };
        process.StartInfo.ArgumentList.Add("-XshowSettings:properties");
        process.StartInfo.ArgumentList.Add("-version");
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(true); throw new InvalidOperationException("Java не ответила за 10 секунд"); }
        var text = await stdout + "\n" + await stderr;
        var version = Regex.Match(text, @"java\.version\s*=\s*(\S+)");
        var bits = Regex.Match(text, @"sun\.arch\.data\.model\s*=\s*(\d+)");
        if (process.ExitCode != 0 || !version.Success) throw new InvalidOperationException("Не удалось проверить версию Java");
        if (!Regex.IsMatch(version.Groups[1].Value, @"^21(?:\.|$|-)")) throw new InvalidOperationException("Для этой сборки нужна Java 21. Найдена " + version.Groups[1].Value);
        if (bits.Groups[1].Value != "64") throw new InvalidOperationException("Для сборки нужна 64-битная Java");
        return "Java " + version.Groups[1].Value + " · 64 бит";
    }

    public IEnumerable<string> FindJavaCandidates()
    {
        var name = OperatingSystem.IsWindows() ? "java.exe" : "java";
        var candidates = new List<string>();
        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrEmpty(javaHome)) candidates.Add(Path.Combine(javaHome, "bin", name));
        candidates.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => Path.Combine(p.Trim('"'), name)));
        var roots = new[] { Path.Combine(Game, "runtime"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ElyPrismLauncher", "java"), "/usr/lib/jvm" };
        foreach (var root in roots)
        {
            try { if (Directory.Exists(root)) candidates.AddRange(Directory.EnumerateFiles(root, name, SearchOption.AllDirectories).Take(30)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return candidates.Where(File.Exists).Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).Take(30);
    }

    public static MSession Offline(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || !Regex.IsMatch(name, "^[A-Za-z0-9_]{3,16}$")) throw new ArgumentException("Ник: 3–16 латинских букв, цифр или знаков _");
        var hash = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + name));
        hash[6] = (byte)((hash[6] & 0x0f) | 0x30);
        hash[8] = (byte)((hash[8] & 0x3f) | 0x80);
        return new MSession { Username = name, UUID = Convert.ToHexStringLower(hash), AccessToken = "0", UserType = "mojang" };
    }

    public static async Task<MSession> Ely(string login, string password, string totp)
    {
        if (string.IsNullOrWhiteSpace(login) || string.IsNullOrEmpty(password)) throw new ArgumentException("Введите логин и пароль Ely.by");
        using var response = await Http.PostAsJsonAsync("https://authserver.ely.by/auth/authenticate", new {
            username = login, password = password + (string.IsNullOrWhiteSpace(totp) ? "" : ":" + totp.Trim()),
            clientToken = Guid.NewGuid().ToString(), requestUser = true
        });
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var value = body.RootElement;
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(value.TryGetProperty("errorMessage", out var error) ? error.GetString() : "Ошибка входа Ely.by");
        var profile = value.GetProperty("selectedProfile");
        return new MSession { Username = profile.GetProperty("name").GetString(), UUID = profile.GetProperty("id").GetString(), AccessToken = value.GetProperty("accessToken").GetString(), UserType = "mojang" };
    }

    public static async Task<MSession> TLauncher(string login, string password)
    {
        if (string.IsNullOrWhiteSpace(login) || string.IsNullOrEmpty(password)) throw new ArgumentException("Введите логин и пароль TLauncher.org");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var response = await Http.PostAsJsonAsync("https://auth.tlauncher.org/skin/authentication/authenticate", new {
            agent = new { name = "Minecraft", version = 1 }, username = login, password,
            clientToken = Guid.NewGuid().ToString("N"), requestUser = true
        }, timeout.Token);
        if (response.Content.Headers.ContentType?.MediaType?.Contains("json") != true) throw new InvalidOperationException("Сервис TLauncher не вернул ответ авторизации. Попробуйте позже.");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        return ParseTLauncher(body.RootElement, response.IsSuccessStatusCode);
    }
    internal static MSession ParseTLauncher(JsonElement value, bool success)
    {
        if (!success) throw new InvalidOperationException(value.TryGetProperty("errorMessage", out var error) ? error.GetString() : "TLauncher отклонил вход");
        if (!value.TryGetProperty("selectedProfile", out var profile) || profile.ValueKind != JsonValueKind.Object || !value.TryGetProperty("accessToken", out var token)) throw new InvalidDataException("TLauncher не вернул игровой профиль");
        var name = profile.GetProperty("name").GetString();
        var id = profile.GetProperty("id").GetString();
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(id) || string.IsNullOrEmpty(token.GetString())) throw new InvalidDataException("TLauncher вернул неполный профиль");
        return new MSession { Username = name, UUID = id.Replace("-", ""), AccessToken = token.GetString(), UserType = "mojang" };
    }

    static string Hash(string path)
    {
        using var input = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(input));
    }
    public static string PackPath(string root, string name)
    {
        var allowed = new[] { "mods", "config", "defaultconfigs", "coremods", "resourcepacks", "shaderpacks", "texturepacks", "moonlight-global-datapacks" };
        var parts = name.Split('/');
        bool rootSettings = parts.Length == 1 && name == "options.txt";
        if (name.Contains('\\') || name.Contains(':') || parts.Any(p => p is ".." or "." or "") || (!rootSettings && !allowed.Contains(parts[0]))) throw new InvalidDataException("Недопустимый путь пакета: " + name);
        var target = Path.GetFullPath(Path.Combine(root, name));
        if (!target.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new InvalidDataException("Файл вне папки клиента");
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(target)!); dir != null && dir.FullName.Length >= Path.GetFullPath(root).Length; dir = dir.Parent)
            if (dir.Exists && (dir.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Ссылки внутри папки клиента не поддерживаются");
        if (File.Exists(target) && (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Файл клиента является ссылкой");
        return target;
    }

    public static void ApplyPack(string archive, string game, string state, LauncherConfig config)
    {
        using var z = ZipFile.OpenRead(archive);
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        if (z.Entries.Select(e => e.FullName).Distinct(comparer).Count() != z.Entries.Count || z.Entries.Sum(e => e.Length) > 4L * 1024 * 1024 * 1024) throw new InvalidDataException("Некорректный или слишком большой пакет");
        using var manifestStream = (z.GetEntry("manifest.json") ?? throw new InvalidDataException("Нет manifest.json")).Open();
        var manifest = JsonSerializer.Deserialize<PackManifest>(manifestStream, Json) ?? throw new InvalidDataException("Пустой манифест");
        if (manifest.Minecraft != config.Minecraft || manifest.Loader != config.Loader) throw new InvalidDataException("Версии сборки отличаются от launcher.json");
        if (manifest.Files.Keys.Distinct(comparer).Count() != manifest.Files.Count) throw new InvalidDataException("Повторяющиеся пути пакета");
        var previous = File.Exists(state) ? Read<PackManifest>(state).Files : new Dictionary<string, string>();
        foreach (var (name, digest) in manifest.Files)
        {
            PackPath(game, name);
            using var entry = (z.GetEntry(name) ?? throw new InvalidDataException("Нет файла " + name)).Open();
            if (!Convert.ToHexStringLower(SHA256.HashData(entry)).Equals(digest, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Повреждён файл " + name);
        }
        foreach (var (name, digest) in manifest.Files)
        {
            var target = PackPath(game, name);
            var current = File.Exists(target) ? Hash(target) : null;
            previous.TryGetValue(name, out var old);
            if (current == digest || (!name.StartsWith("mods/") && current != null && current != old)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            z.GetEntry(name)!.ExtractToFile(target + ".tmp", true);
            File.Move(target + ".tmp", target, true);
        }
        foreach (var (name, digest) in previous.Where(p => p.Key.StartsWith("mods/") && !manifest.Files.ContainsKey(p.Key)))
        {
            var target = PackPath(game, name);
            if (File.Exists(target) && Hash(target) == digest) File.Delete(target);
        }
        Write(state, manifest);
    }

    public static void AddServer(string game, string address, string name)
    {
        Directory.CreateDirectory(game);
        var path = Path.Combine(game, "servers.dat");
        var file = File.Exists(path) ? new NbtFile(path) : new NbtFile(new NbtCompound(""));
        var servers = file.RootTag.Get<NbtList>("servers");
        if (servers == null) { servers = new NbtList("servers", NbtTagType.Compound); file.RootTag.Add(servers); }
        if (servers.Cast<NbtCompound>().Any(s => s.Get<NbtString>("ip")?.Value == address)) return;
        servers.Add(new NbtCompound { new NbtString("name", name), new NbtString("ip", address) });
        if (File.Exists(path)) File.Copy(path, path + ".launcher-backup", true);
        file.SaveToFile(path + ".tmp", NbtCompression.None);
        File.Move(path + ".tmp", path, true);
    }

    public static async Task Download(string url, string target, string? digest = null, CancellationToken cancellation = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https") throw new ArgumentException("Нужен HTTPS-адрес загрузки");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            await using (var file = File.Create(target + ".part")) await response.Content.CopyToAsync(file, timeout.Token);
            if (digest != null && !Hash(target + ".part").Equals(digest, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("SHA-256 файла не совпадает");
            File.Move(target + ".part", target, true);
        }
        finally { if (File.Exists(target + ".part")) File.Delete(target + ".part"); }
    }

    internal static PackDownload ParsePackRelease(JsonElement release)
    {
        if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean())
            throw new InvalidDataException("Нужен опубликованный стабильный релиз клиента");
        var asset = release.GetProperty("assets").EnumerateArray().SingleOrDefault(a => a.GetProperty("name").GetString() == "client.zip");
        if (asset.ValueKind != JsonValueKind.Object) throw new InvalidDataException("В релизе GitHub нет client.zip");
        var url = asset.GetProperty("browser_download_url").GetString();
        var digest = asset.TryGetProperty("digest", out var hash) ? hash.GetString() : null;
        if (digest == null || !Regex.IsMatch(digest, "^sha256:[a-fA-F0-9]{64}$") ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https")
            throw new InvalidDataException("Релиз клиента не содержит HTTPS-адрес и SHA-256");
        return new(url!, digest[7..].ToLowerInvariant());
    }

    internal async Task<string> GetPackArchive(Action<string> report)
    {
        var bundled = Path.Combine(Root, "client.zip");
        PackDownload? pack = null;
        if (!string.IsNullOrWhiteSpace(Config.PackUrl))
        {
            if (!Regex.IsMatch(Config.PackSha256, "^[a-fA-F0-9]{64}$")) throw new InvalidDataException("Для скачивания пакета нужен SHA-256");
            pack = new(Config.PackUrl, Config.PackSha256);
        }
        else if (!string.IsNullOrWhiteSpace(Config.PackReleaseUrl))
        {
            if (!Uri.TryCreate(Config.PackReleaseUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https")
                throw new InvalidDataException("Нужен HTTPS-адрес релиза клиента");
            report("Проверяем релиз клиента на GitHub…");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("CreateLauncher/0.3.1");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var response = await Http.SendAsync(request, timeout.Token);
            response.EnsureSuccessStatusCode();
            using var release = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            pack = ParsePackRelease(release.RootElement);
        }
        if (pack == null)
        {
            if (!File.Exists(bundled)) throw new FileNotFoundException("Положите client.zip рядом с лаунчером или настройте источник загрузки");
            return bundled;
        }
        var cached = Path.Combine(Data, "client.zip");
        foreach (var path in new[] { cached, bundled })
            if (File.Exists(path) && Hash(path).Equals(pack.Sha256, StringComparison.OrdinalIgnoreCase)) return path;
        report("Загружаем клиентскую сборку…");
        await Download(pack.Url, cached, pack.Sha256);
        return cached;
    }

    MinecraftLauncher CreateLauncher(Action<string>? report = null, Action<double>? progress = null)
    {
        var launcher = new MinecraftLauncher(new MinecraftPath(Game));
        launcher.FileProgressChanged += (_, e) => { report?.Invoke($"{e.ProgressedTasks} / {e.TotalTasks}  ·  {e.Name}"); progress?.Invoke(e.TotalTasks == 0 ? 0 : (double)e.ProgressedTasks / e.TotalTasks); };
        return launcher;
    }

    public async Task Install(Action<string> report, Action<double> progress)
    {
        if (IsGameRunning) throw new InvalidOperationException("Закройте Minecraft перед проверкой или обновлением файлов");
        var archive = await GetPackArchive(report);
        report("Проверяем сборку…");
        // Verify and install the pack before downloading large official game dependencies.
        await Task.Run(() => ApplyPack(archive, Game, Path.Combine(Data, "pack-state.json"), Config));
        var launcher = CreateLauncher(report, progress);
        report("Устанавливаем Minecraft и Java 21…");
        await launcher.InstallAsync(Config.Minecraft);
        var java = launcher.GetJavaPath(await launcher.GetVersionAsync(Config.Minecraft));
        RequirePortableJava(java);
        report("Устанавливаем NeoForge " + Config.Loader + "…");
        var installer = new NeoForgeInstaller(launcher);
        var version = await installer.Install(new NeoForgeVersion(Config.Minecraft, Config.Loader), new ForgeInstallOptions {
            JavaPath = java, SkipIfAlreadyInstalled = false,
            FileProgress = new Progress<CmlLib.Core.Installers.InstallerProgressChangedEventArgs>(e => report($"NeoForge  ·  {e.ProgressedTasks} / {e.TotalTasks}  ·  {e.Name}")),
            InstallerOutput = new Progress<string>(_ => { })
        });
        await launcher.InstallAsync(version);
        AddServer(Game, Config.Server, Config.Name);
        Write(Path.Combine(Data, "installed.json"), new Installed(version, Config.Minecraft, Config.Loader));
        progress(1);
        report("Клиент готов. Увидимся на сервере!");
    }

    void RequirePortableJava(string? java)
    {
        if (string.IsNullOrWhiteSpace(java) || !File.Exists(java)) throw new InvalidOperationException("Java не найдена. Нажмите «Проверить файлы».");
        if (!Path.GetFullPath(java).StartsWith(Game + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new InvalidOperationException("Java должна находиться в папке лаунчера");
    }

    public async Task<Process> BuildProcess(MSession session, int memory, bool ely)
    {
        if (memory is < 2048 or > 16384) throw new ArgumentException("Память: от 2048 до 16384 МБ");
        var installed = Read<Installed>(Path.Combine(Data, "installed.json"));
        if (installed.Minecraft != Config.Minecraft || installed.Loader != Config.Loader) throw new InvalidOperationException("Сначала обновите клиент");
        var launcher = CreateLauncher();
        var version = await launcher.GetVersionAsync(installed.Version);
        var preference = LoadPreferences();
        var java = string.IsNullOrWhiteSpace(preference.JavaPath) ? launcher.GetJavaPath(version) : Path.GetFullPath(preference.JavaPath, Root);
        if (string.IsNullOrWhiteSpace(preference.JavaPath)) RequirePortableJava(java);
        else await CheckJava(java!);
        var args = new List<MArgument> {
            new($"-Djava.io.tmpdir={Path.Combine(Data, "tmp")}"), new($"-Duser.home={Path.Combine(Data, "home")}"),
            new($"-Djava.util.prefs.userRoot={Path.Combine(Data, "home", "prefs")}")
        };
        if (ely)
        {
            var jar = Path.Combine(Data, "authlib-injector.jar");
            if (!File.Exists(jar))
            {
                using var info = JsonDocument.Parse(await Http.GetStringAsync("https://authlib-injector.yushi.moe/artifact/latest.json"));
                await Download(info.RootElement.GetProperty("download_url").GetString()!, jar, info.RootElement.GetProperty("checksums").GetProperty("sha256").GetString());
            }
            args.Add(new MArgument($"-javaagent:{jar}=ely.by"));
        }
        AddServer(Game, Config.Server, Config.Name);
        var process = await launcher.BuildProcessAsync(installed.Version, new MLaunchOption {
            Session = session, JavaPath = java, MaximumRamMb = memory, MinimumRamMb = 1024,
            ExtraJvmArguments = args, GameLauncherName = "CreateServer", GameLauncherVersion = "0.3.1", ScreenWidth = 1280, ScreenHeight = 720
        });
        process.StartInfo.WorkingDirectory = Game;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        return process;
    }

    public async Task Launch(MSession session, int memory, bool ely)
    {
        if (IsGameRunning) throw new InvalidOperationException("Minecraft уже запущен");
        var process = await BuildProcess(session, memory, ely);
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.Start();
        GameProcess = process;
        _ = SaveLog(process);
    }
    async Task SaveLog(Process process)
    {
        try
        {
            await using var output = File.Create(Path.Combine(Data, "logs", "game.log"));
            // stdout and stderr are drained independently to avoid pipe deadlocks.
            var errors = process.StandardError.ReadToEndAsync();
            await process.StandardOutput.BaseStream.CopyToAsync(output);
            var tail = Encoding.UTF8.GetBytes(await errors);
            await output.WriteAsync(tail);
        }
        catch (IOException) { }
    }

    public static void WriteVarInt(Stream stream, int value)
    {
        uint n = (uint)value;
        do { byte b = (byte)(n & 127); n >>= 7; stream.WriteByte((byte)(b | (n != 0 ? 128 : 0))); } while (n != 0);
    }
    public static int ReadVarInt(Stream stream)
    {
        int result = 0;
        for (int i = 0; i < 5; i++)
        {
            int b = stream.ReadByte();
            if (b < 0) throw new EndOfStreamException();
            result |= (b & 127) << (i * 7);
            if ((b & 128) == 0) return result;
        }
        throw new InvalidDataException("Некорректный VarInt");
    }
    public static async Task<ServerInfo> Status(string address)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        var parts = address.Split(':');
        var host = parts[0]; var port = int.Parse(parts[1]);
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(host, port, timeout.Token);
        using var network = tcp.GetStream();
        using var packet = new MemoryStream();
        packet.WriteByte(0); WriteVarInt(packet, 767);
        var bytes = Encoding.UTF8.GetBytes(host); WriteVarInt(packet, bytes.Length); packet.Write(bytes);
        packet.WriteByte((byte)(port >> 8)); packet.WriteByte((byte)(port & 255)); packet.WriteByte(1);
        using var request = new MemoryStream(); WriteVarInt(request, (int)packet.Length); packet.Position = 0; packet.CopyTo(request); request.Write([1, 0]);
        await network.WriteAsync(request.ToArray(), timeout.Token);
        // A read timeout bounds the small synchronous VarInt reads as well.
        network.ReadTimeout = 6000;
        int length = ReadVarInt(network);
        if (length is < 1 or > 2097152) throw new InvalidDataException("Размер пакета статуса");
        var response = new byte[length]; await network.ReadExactlyAsync(response, timeout.Token);
        using var payload = new MemoryStream(response);
        if (ReadVarInt(payload) != 0) throw new InvalidDataException("Пакет статуса");
        int jsonLength = ReadVarInt(payload);
        if (jsonLength < 0 || jsonLength > payload.Length - payload.Position) throw new InvalidDataException("Неполный статус");
        using var status = JsonDocument.Parse(response.AsMemory((int)payload.Position, jsonLength));
        var players = status.RootElement.GetProperty("players");
        return new(players.GetProperty("online").GetInt32(), players.GetProperty("max").GetInt32());
    }
}

