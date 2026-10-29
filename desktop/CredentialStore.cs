using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CreateLauncher;

internal sealed record SavedLogin(string Login, string Password)
{
    public override string ToString() => "SavedLogin { [redacted] }";
}

internal sealed class CredentialStoreException : Exception
{
    public CredentialStoreException() : base(Localization.Text("Системное хранилище паролей недоступно. Разблокируй его или введи пароль вручную.")) { }
}

internal sealed class CredentialStore(string root)
{
    const int MaximumSecretBytes = 32768;
    const string SecretTool = "/usr/bin/secret-tool";
    readonly string installation = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
        OperatingSystem.IsWindows() ? Path.GetFullPath(root).ToUpperInvariant() : Path.GetFullPath(root))));
    public static bool Available => OperatingSystem.IsWindows() || OperatingSystem.IsLinux() && File.Exists(SecretTool);
    static bool IsOnlineProfile(PlayerProfile profile) => profile.Provider is 1 or 2 && Guid.TryParse(profile.Uuid, out _);
    internal bool HasRecord(PlayerProfile profile) => IsOnlineProfile(profile) && File.Exists(FilePath(profile));
    internal string Key(PlayerProfile profile)
    {
        if (!IsOnlineProfile(profile)) throw new ArgumentException("Invalid credential profile");
        return installation + ":" + LauncherService.ProfileKey(profile);
    }
    internal string FilePath(PlayerProfile profile) => Path.Combine(root, "data", "credentials",
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(LauncherService.ProfileKey(profile)))) + (OperatingSystem.IsWindows() ? ".bin" : ".secret-service"));

    public async Task Save(PlayerProfile profile, string login, string password)
    {
        if (!Available) throw new CredentialStoreException();
        if (!IsOnlineProfile(profile)) throw new CredentialStoreException();
        if (string.IsNullOrWhiteSpace(login) || login.Length > 100 || string.IsNullOrEmpty(password) || password.Length > 4096)
            throw new CredentialStoreException();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new SavedLogin(login, password));
        try
        {
            if (bytes.Length > MaximumSecretBytes) throw new CredentialStoreException();
            var path = FilePath(profile);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (OperatingSystem.IsWindows())
            {
                var encrypted = Protect(bytes, Key(profile), false);
                try
                {
                    await File.WriteAllBytesAsync(path + ".tmp", encrypted);
                    File.Move(path + ".tmp", path, true);
                }
                finally { if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp"); }
            }
            else
            {
                await SaveSecretService(path, SecretToolCommand("store", Key(profile)), Encoding.UTF8.GetString(bytes));
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or CryptographicException)
        { throw new CredentialStoreException(); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public async Task<SavedLogin?> Load(PlayerProfile profile)
    {
        if (!HasRecord(profile)) return null;
        if (!Available) throw new CredentialStoreException();
        byte[]? bytes = null;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var file = new FileInfo(FilePath(profile));
                if (file.Length > MaximumSecretBytes + 4096) throw new CredentialStoreException();
                bytes = Protect(await File.ReadAllBytesAsync(file.FullName), Key(profile), true);
            }
            else
            {
                var result = await RunSecretTool(SecretToolCommand("lookup", Key(profile)));
                if (result.Code == 1 && !result.HasError) return null;
                if (result.Code != 0) throw new CredentialStoreException();
                bytes = Encoding.UTF8.GetBytes(result.Output);
            }
            if (bytes.Length > MaximumSecretBytes) throw new CredentialStoreException();
            var saved = JsonSerializer.Deserialize<SavedLogin>(bytes);
            if (saved == null || string.IsNullOrWhiteSpace(saved.Login) || saved.Login.Length > 100 || string.IsNullOrEmpty(saved.Password) || saved.Password.Length > 4096)
                throw new CredentialStoreException();
            return saved;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or CryptographicException or JsonException)
        { throw new CredentialStoreException(); }
        finally { if (bytes != null) CryptographicOperations.ZeroMemory(bytes); }
    }

    public async Task Remove(PlayerProfile profile)
    {
        if (!IsOnlineProfile(profile) || !File.Exists(FilePath(profile)) && !File.Exists(FilePath(profile) + ".tmp")) return;
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                if (!Available) throw new CredentialStoreException();
                var result = await RunSecretTool(SecretToolCommand("clear", Key(profile)));
                if (result.Code != 0 && (result.Code != 1 || result.HasError)) throw new CredentialStoreException();
            }
            File.Delete(FilePath(profile));
            File.Delete(FilePath(profile) + ".tmp");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception)
        { throw new CredentialStoreException(); }
    }

    internal static ProcessStartInfo SecretToolCommand(string action, string key)
    {
        var start = new ProcessStartInfo(SecretTool) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8 };
        start.ArgumentList.Add(action);
        if (action == "store") start.ArgumentList.Add("--label=Create Launcher");
        start.ArgumentList.Add("application"); start.ArgumentList.Add("CreateLauncher");
        start.ArgumentList.Add("account"); start.ArgumentList.Add(key);
        return start;
    }

    internal static async Task SaveSecretService(string marker, ProcessStartInfo command, string secret)
    {
        // Only an existence marker is stored beside the portable launcher.
        // Create it first: a failed marker write must not leave an untracked secret.
        await File.WriteAllTextAsync(marker, "Secret Service");
        var result = await RunSecretTool(command, secret);
        if (result.Code != 0) throw new CredentialStoreException();
    }

    internal static async Task<(int Code, string Output, bool HasError)> RunSecretTool(ProcessStartInfo start, string? secret = null, TimeSpan? timeoutLimit = null)
    {
        using var process = new Process { StartInfo = start };
        using var timeout = new CancellationTokenSource(timeoutLimit ?? TimeSpan.FromSeconds(30));
        try
        {
            process.Start();
            var output = ReadBounded(process.StandardOutput, MaximumSecretBytes, timeout.Token);
            var error = ReadBounded(process.StandardError, 4096, timeout.Token);
            if (secret != null) await process.StandardInput.WriteAsync(secret.AsMemory(), timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            return (process.ExitCode, await output, (await error).Length != 0);
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or Win32Exception or InvalidOperationException)
        { throw new CredentialStoreException(); }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception error) when (error is InvalidOperationException or Win32Exception) { }
        }
    }
    static async Task<string> ReadBounded(StreamReader reader, int limit, CancellationToken cancellation)
    {
        var result = new StringBuilder();
        var buffer = new char[2048];
        int count;
        while ((count = await reader.ReadAsync(buffer, cancellation)) != 0)
        {
            if (result.Length + count > limit) throw new IOException("Credential response too large");
            result.Append(buffer, 0, count);
            Array.Clear(buffer);
        }
        return result.ToString();
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Blob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CryptProtectData(ref Blob input, string? description, ref Blob entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CryptUnprotectData(ref Blob input, IntPtr description, ref Blob entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr memory);

    static byte[] Protect(byte[] bytes, string key, bool decrypt)
    {
        var entropyBytes = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        var input = new Blob { Length = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        var entropy = new Blob { Length = entropyBytes.Length, Data = Marshal.AllocHGlobal(entropyBytes.Length) };
        Blob output = default;
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            Marshal.Copy(entropyBytes, 0, entropy.Data, entropyBytes.Length);
            // CurrentUser is the DPAPI default. Never use CRYPTPROTECT_LOCAL_MACHINE.
            bool success = decrypt ? CryptUnprotectData(ref input, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptProtectData(ref input, null, ref entropy, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!success) throw new CryptographicException();
            if (output.Length is < 1 or > MaximumSecretBytes + 4096) throw new CryptographicException();
            var result = new byte[output.Length];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            Marshal.Copy(new byte[input.Length], 0, input.Data, input.Length); Marshal.FreeHGlobal(input.Data);
            Marshal.FreeHGlobal(entropy.Data);
            CryptographicOperations.ZeroMemory(entropyBytes);
            if (output.Data != IntPtr.Zero) { Marshal.Copy(new byte[output.Length], 0, output.Data, output.Length); LocalFree(output.Data); }
        }
    }
}
