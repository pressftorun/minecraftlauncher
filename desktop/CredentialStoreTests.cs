using System.Text;

namespace CreateLauncher;

internal static class CredentialStoreTests
{
    static void Check(bool value, string message) { if (!value) throw new Exception("CREDENTIAL CHECK FAILED: " + message); }
    public static void Run(string parent)
    {
        var root = Path.Combine(parent, "credential-tests");
        var profile = new PlayerProfile("Player", "b50ad385829d3141a2167e7d7539ba7f", 2);
        var store = new CredentialStore(root);
        Check(store.Load(profile).GetAwaiter().GetResult() == null, "An account starts without a saved password");
        Check(store.Load(profile with { Uuid = "invalid-local-profile" }).GetAwaiter().GetResult() == null, "Corrupt local profiles do not crash password lookup");
        store.Remove(profile with { Provider = 0 }).GetAwaiter().GetResult();
        Check(store.Key(profile) != new CredentialStore(root + "-other").Key(profile), "Portable installations have separate credentials");
        Check(store.Key(profile) != store.Key(profile with { Provider = 1 }), "Providers have separate credentials");
        var command = CredentialStore.SecretToolCommand("store", store.Key(profile));
        Check(!command.UseShellExecute && command.RedirectStandardInput && command.ArgumentList.Contains("--label=Create Launcher"), "Secret-tool uses redirected stdin and no shell");
        Check(!new SavedLogin("private-login", "private-password").ToString().Contains("private-"), "Credentials are redacted in diagnostic formatting");
        var blockedMarker = Path.Combine(root, "marker-is-a-directory");
        Directory.CreateDirectory(blockedMarker);
        try
        {
            // The deliberately invalid executable must never run if the marker cannot be written.
            command.FileName = "nonexistent-credential-test-helper";
            CredentialStore.SaveSecretService(blockedMarker, command, "test-only").GetAwaiter().GetResult();
            throw new Exception("A failed marker write was accepted");
        }
        catch (UnauthorizedAccessException) { }
        var pipe = CredentialStore.SecretToolCommand("lookup", "test-only");
        pipe.ArgumentList.Clear();
        if (OperatingSystem.IsWindows())
        {
            pipe.FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command", "[Console]::InputEncoding=[Text.UTF8Encoding]::new($false); [Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); [Console]::Out.Write([Console]::In.ReadToEnd())" }) pipe.ArgumentList.Add(argument);
        }
        else pipe.FileName = "/bin/cat";
        var piped = CredentialStore.RunSecretTool(pipe, "test-only Unicode пароль \"\\\"").GetAwaiter().GetResult();
        Check(piped.Code == 0 && piped.Output == "test-only Unicode пароль \"\\\"", "The secret travels through stdin without shell expansion or encoding loss");
        if (OperatingSystem.IsWindows()) pipe.ArgumentList[3] = "Start-Sleep -Seconds 10";
        else { pipe.FileName = "/bin/sleep"; pipe.ArgumentList.Add("10"); }
        try
        {
            CredentialStore.RunSecretTool(pipe, timeoutLimit: TimeSpan.FromMilliseconds(200)).GetAwaiter().GetResult();
            throw new Exception("A stuck credential helper was accepted");
        }
        catch (CredentialStoreException) { }
        if (OperatingSystem.IsWindows())
        {
            const string password = "Test-only пароль \\\" & < > 123";
            store.Save(profile, "login@example.test", password).GetAwaiter().GetResult();
            Check(store.Load(profile).GetAwaiter().GetResult() == new SavedLogin("login@example.test", password), "DPAPI round trip preserves the original login and password");
            var bytes = File.ReadAllBytes(store.FilePath(profile));
            Check(!Encoding.UTF8.GetString(bytes).Contains(password) && !Encoding.UTF8.GetString(bytes).Contains("login@example.test"), "The local file contains no plaintext credentials");
            var moved = new CredentialStore(root + "-other");
            Directory.CreateDirectory(Path.GetDirectoryName(moved.FilePath(profile))!);
            File.Copy(store.FilePath(profile), moved.FilePath(profile));
            try { moved.Load(profile).GetAwaiter().GetResult(); throw new Exception("A credential moved to another installation was accepted"); }
            catch (CredentialStoreException) { }
            store.Save(profile, "new@example.test", "updated-test-password").GetAwaiter().GetResult();
            Check(store.Load(profile).GetAwaiter().GetResult()?.Login == "new@example.test", "Saving replaces a previous credential");
            File.WriteAllBytes(store.FilePath(profile), [1, 2, 3, 4]);
            try { store.Load(profile).GetAwaiter().GetResult(); throw new Exception("Corrupt DPAPI data was accepted"); }
            catch (CredentialStoreException) { }
            store.Remove(profile).GetAwaiter().GetResult();
            Check(store.Load(profile).GetAwaiter().GetResult() == null, "Removing a corrupt credential does not require decrypting it");
            LauncherService.Write(Path.Combine(root, "launcher.json"), new LauncherConfig());
            var service = new LauncherService(root);
            service.SaveProfile(profile);
            service.Credentials.Save(profile, "login@example.test", password).GetAwaiter().GetResult();
            service.RemoveProfile(profile).GetAwaiter().GetResult();
            Check(!File.Exists(store.FilePath(profile)) && service.LoadProfiles().Count == 0, "Removing a profile also removes its saved password");
        }
        Console.WriteLine("PASS: credential isolation, secret-tool stdin, DPAPI round trip, corruption and profile removal (native DPAPI checks run on Windows)");
    }
}
