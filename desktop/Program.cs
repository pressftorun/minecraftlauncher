using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace CreateLauncher;

internal static class Program
{
    public static string[] Arguments = [];
    [STAThread]
    public static int Main(string[] args)
    {
        Arguments = args;
        try
        {
            if (args.Length == 3 && args[0] == "--apply-launcher-update")
            {
                LauncherUpdater.Apply(int.Parse(args[1]), args[2]);
                return 0;
            }
            if (args.Contains("--self-test")) { SelfTests.Run(); return 0; }
            if (args.Length == 2 && args[0] == "--check-java") { Console.WriteLine(LauncherService.CheckJava(args[1]).GetAwaiter().GetResult()); return 0; }
            if (args.Length is 2 or 3 && args[0] == "--check-skin")
            {
                var service = new LauncherService();
                var player = LauncherService.Offline(args[1]);
                var skin = service.GetSkin(new PlayerProfile(args[1], player.UUID!, args.Length == 3 ? int.Parse(args[2]) : 1)).GetAwaiter().GetResult();
                if (skin == null) throw new Exception("Скин не загрузился");
                Console.WriteLine("Skin PNG verified: " + skin.Length + " bytes");
                return 0;
            }
            if (args.Length == 2 && args[0] == "--verify-install")
            {
                var service = new LauncherService(args[1]);
                service.Install(Console.WriteLine, _ => { }).GetAwaiter().GetResult();
                using var process = service.BuildProcess(LauncherService.Offline("LauncherCheck"), 4096, false).GetAwaiter().GetResult();
                Console.WriteLine("Install and launch command verified: " + process.StartInfo.FileName);
                return 0;
            }
            if (args.Length == 2 && args[0] == "--launch-test")
            {
                var service = new LauncherService(args[1]);
                service.Launch(LauncherService.Offline("MenuCheck"), 4096, false).GetAwaiter().GetResult();
                Console.WriteLine("Minecraft test PID: " + service.GameProcess!.Id);
                service.GameProcess.WaitForExit();
                return service.GameProcess.ExitCode;
            }
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return 0;
        }
        catch (Exception e)
        {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "launcher-error.log"), e.ToString());
            Console.Error.WriteLine(e.Message);
            return 1;
        }
    }
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.MainWindow = new MainWindow();
        base.OnFrameworkInitializationCompleted();
    }
}
