using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System.Diagnostics;
using CmlLib.Core.Auth;

namespace CreateLauncher;

public partial class MainWindow : Window
{
    readonly LauncherService service;
    readonly DispatcherTimer statusTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    readonly int greetingIndex;
    MSession? activeSession;
    readonly Dictionary<string, MSession> sessions = new();
    readonly List<SkinHead> savedHeads = [];
    int profilesRevision;
    PlayerProfile? currentProfile;
    int skinRequest;
    bool busy, statusPending;
    bool memorySync;
    LauncherUpdate? launcherUpdate;
    bool checkingLauncherUpdate;
    Flyout AccountFlyout => (Flyout)AccountButton.Flyout!;

    public MainWindow()
    {
        InitializeComponent();
        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "logo.ico");
        if (File.Exists(icon)) Icon = new WindowIcon(icon);
        service = new LauncherService();
        LauncherVersionLabel.Text = LauncherUpdater.VersionLabel;
        ServerAddress.Text = service.Config.Server;
        BlueMapButton.IsVisible = Uri.TryCreate(service.Config.BlueMapUrl, UriKind.Absolute, out var mapUri) && mapUri.Scheme is "http" or "https";
        VersionLabel.Text = service.Config.Minecraft;
        LoaderLabel.Text = "NeoForge " + service.Config.Loader;
        var prefs = service.LoadPreferences();
        Nickname.Text = prefs.Nickname;
        MemoryInput.Value = prefs.MemoryMb;
        MemorySlider.Value = prefs.MemoryMb;
        MemoryInput.ValueChanged += (_, _) => { if (memorySync || MemoryInput.Value is not decimal value) return; memorySync = true; MemorySlider.Value = (double)value; memorySync = false; };
        MemorySlider.PropertyChanged += (_, e) => { if (memorySync || e.Property.Name != "Value") return; memorySync = true; MemoryInput.Value = (decimal)(Math.Round(MemorySlider.Value / 256) * 256); memorySync = false; };
        MemoryScale.SizeChanged += (_, _) => DrawMemoryScale();
        JavaPathBox.Text = string.IsNullOrWhiteSpace(prefs.JavaPath) ? "" : Path.GetFullPath(prefs.JavaPath, service.Root);
        JavaMode.SelectedIndex = string.IsNullOrWhiteSpace(prefs.JavaPath) ? 0 : 1;
        int previous = -1;
        try { previous = LauncherService.Read<int>(Path.Combine(service.Data, "greeting.json")); } catch (Exception e) when (e is IOException or System.Text.Json.JsonException) { }
        greetingIndex = Greetings.Next(previous);
        LauncherService.Write(Path.Combine(service.Data, "greeting.json"), greetingIndex);
        currentProfile = service.LoadProfile();
        if (currentProfile == null && !string.IsNullOrWhiteSpace(prefs.Nickname))
        {
            try { var session = LauncherService.Offline(prefs.Nickname); currentProfile = new(session.Username!, session.UUID!, 0); } catch (ArgumentException) { }
        }
        if (currentProfile?.Provider == 0)
        {
            try { activeSession = LauncherService.Offline(currentProfile.Name); } catch (ArgumentException) { currentProfile = null; }
        }
        if (currentProfile != null) { AuthMode.SelectedIndex = Math.Clamp(currentProfile.Provider, 0, 2); Nickname.Text = currentProfile.Name; }
        if (activeSession != null && currentProfile != null) sessions[LauncherService.ProfileKey(currentProfile)] = activeSession;
        RefreshProfile();
        BuildSavedProfiles();
        AuthForm.IsVisible = service.LoadProfiles().Count == 0;
        UpdateInstalled();
        statusTimer.Tick += async (_, _) => await RefreshStatus();
        Opened += async (_, _) => {
            if (!Program.Arguments.Any(a => a is "--preview" or "--smoke-test" or "--ui-check" or "--make-icon")) _ = CheckLauncherUpdate();
            if (Program.Arguments.Contains("--settings")) ShowPage(Page.Settings);
            if (Program.Arguments.Contains("--client-mods")) ShowPage(Page.ClientMods);
            if (Program.Arguments.Contains("--ely")) AuthMode.SelectedIndex = 1;
            if (Program.Arguments.Contains("--tlauncher")) AuthMode.SelectedIndex = 2;
            if (Program.Arguments.Contains("--ui-check"))
            {
                foreach (var page in Enum.GetValues<Page>())
                {
                    ShowPage(page);
                    if (OverviewPanel.IsVisible != (page == Page.Overview) || SettingsPanel.IsVisible != (page == Page.Settings)
                        || ClientModsPanel.IsVisible != (page == Page.ClientMods)
                        || ClientModsButton.Classes.Contains("active") != (page == Page.ClientMods))
                        throw new Exception("Page navigation failed: " + page);
                }
                ShowPage(Program.Arguments.Contains("--client-mods") ? Page.ClientMods
                    : Program.Arguments.Contains("--settings") ? Page.Settings : Page.Overview);
                var saved = MemoryInput.Value;
                MemoryInput.Value = 7425;
                if (Math.Abs(MemorySlider.Value - 7425) > .01) throw new Exception("Memory input did not update slider");
                MemorySlider.Value = 8192;
                if (MemoryInput.Value != 8192) throw new Exception("Memory slider did not update input");
                MemoryInput.Value = saved;
                AuthMode.SelectedIndex = 1;
                if (!PasswordFields.IsVisible || !TotpPanel.IsVisible) throw new Exception("Ely.by fields missing");
                AuthMode.SelectedIndex = 2;
                if (!PasswordFields.IsVisible || TotpPanel.IsVisible) throw new Exception("TLauncher fields incorrect");
                AuthMode.SelectedIndex = 0;
                if (PasswordFields.IsVisible) throw new Exception("Offline password shown");
            }
            statusTimer.Start(); await RefreshStatus();
            if (currentProfile != null)
            {
                var skin = LoadSkin(currentProfile);
                if (Program.Arguments.Contains("--preview")) await skin;
            }
            if (Program.Arguments.Contains("--account")) AccountFlyout.ShowAt(AccountButton);
            var preview = Array.IndexOf(Program.Arguments, "--preview");
            var makeIcon = Array.IndexOf(Program.Arguments, "--make-icon");
            if (preview >= 0 || makeIcon >= 0 || Program.Arguments.Contains("--smoke-test"))
            {
                await Task.Delay(200);
                if (preview >= 0)
                {
                    if (Program.Arguments.Contains("--account"))
                    {
                        var content = (Control)((Flyout)AccountButton.Flyout!).Content!;
                        using var account = new RenderTargetBitmap(new PixelSize((int)content.Bounds.Width, (int)content.Bounds.Height), new Vector(96, 96));
                        account.Render(content); account.Save(Program.Arguments[preview + 1] + ".account.png", PngBitmapEncoderOptions.Default);
                    }
                    using var bitmap = new RenderTargetBitmap(new PixelSize((int)Bounds.Width, (int)Bounds.Height), new Vector(96, 96));
                    bitmap.Render(this); bitmap.Save(Program.Arguments[preview + 1], PngBitmapEncoderOptions.Default);
                }
                if (makeIcon >= 0) MakeIcon(Program.Arguments[makeIcon + 1]);
                LauncherService.Write(Path.Combine(service.Data, "smoke-test.json"), new { ok = true, width = Bounds.Width, height = Bounds.Height });
                Close();
            }
        };
        Closed += (_, _) => { statusTimer.Stop(); ++skinRequest; ++profilesRevision; Avatar.Reset(); foreach (var head in savedHeads) head.Reset(); };
        Closing += (_, e) => { if (busy) { e.Cancel = true; Detail("Дождись завершения текущей операции перед закрытием."); } };
    }

    void Detail(string text) => OperationDetail.Text = text;
    void DrawMemoryScale()
    {
        MemoryScale.Children.Clear();
        double width = MemoryScale.Bounds.Width;
        if (width <= 0) return;
        for (int memory = 2048; memory <= 16384; memory += 2048)
        {
            var label = new TextBlock { Text = memory.ToString(), FontSize = 10, Foreground = SolidColorBrush.Parse("#B9D2BF") };
            label.Measure(new Size(double.PositiveInfinity, 17));
            double center = (memory - 2048d) / (16384 - 2048) * width;
            Canvas.SetLeft(label, Math.Clamp(center - label.DesiredSize.Width / 2, 0, Math.Max(0, width - label.DesiredSize.Width)));
            MemoryScale.Children.Add(label);
        }
    }
    void UpdateInstalled()
    {
        PlayButton.Content = service.IsInstalled ? "Играть  →" : "Установить и играть  →";
        OperationTitle.Text = service.IsInstalled ? "Готово к запуску" : "Клиент не установлен";
        if (service.IsInstalled) Detail("Клиент установлен");
        UpdateGameActions();
        if (ClientModsPanel.IsVisible) LoadOptionalMods();
    }
    void UpdateGameActions()
    {
        InstallButton.IsEnabled = PlayButton.IsEnabled = !busy && !service.IsGameRunning;
        ApplyLauncherUpdateButton.IsEnabled = BannerUpdateButton.IsEnabled = !busy && !service.IsGameRunning;
        RefreshOptionalModsButton.IsEnabled = OptionalModsList.IsEnabled = !busy && !service.IsGameRunning;
    }

    async void CheckLauncherUpdateClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => await CheckLauncherUpdate();
    async Task CheckLauncherUpdate()
    {
        if (checkingLauncherUpdate) return;
        if (!LauncherUpdater.Supported)
        {
            LauncherUpdateStatus.Text = "Проверка доступна в установленной сборке лаунчера.";
            return;
        }
        checkingLauncherUpdate = true;
        CheckLauncherUpdateButton.IsEnabled = false;
        LauncherUpdateStatus.Text = "Проверяем обновления…";
        try
        {
            launcherUpdate = await LauncherUpdater.Check();
            ApplyLauncherUpdateButton.IsVisible = LauncherUpdateBanner.IsVisible = launcherUpdate != null;
            LauncherUpdateStatus.Text = launcherUpdate == null ? "Установлена последняя версия " + LauncherUpdater.VersionLabel
                : $"Доступна версия {launcherUpdate.Version}. Настройки и файлы игры сохранятся.";
            LauncherUpdateBannerText.Text = launcherUpdate == null ? "" : $"Доступен Create Launcher {launcherUpdate.Version}";
            UpdateGameActions();
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidDataException)
        { LauncherUpdateStatus.Text = "Не удалось проверить обновление. Попробуй позже."; }
        finally { checkingLauncherUpdate = false; CheckLauncherUpdateButton.IsEnabled = true; }
    }

    async void ApplyLauncherUpdateClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (busy || launcherUpdate == null) return;
        if (service.IsGameRunning) { LauncherUpdateStatus.Text = "Закрой Minecraft перед обновлением лаунчера."; return; }
        bool restart = false;
        await Operation(async () =>
        {
            OperationTitle.Text = "Обновляем лаунчер";
            CheckLauncherUpdateButton.IsEnabled = false;
            try
            {
                await LauncherUpdater.Start(launcherUpdate, service.Root, Detail);
                restart = true;
            }
            finally { CheckLauncherUpdateButton.IsEnabled = !checkingLauncherUpdate; }
        });
        if (restart) Close();
    }
    void AuthChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (PasswordFields == null) return;
        var mode = AuthMode.SelectedIndex;
        PasswordFields.IsVisible = mode != 0;
        TotpPanel.IsVisible = mode == 1;
        Grid.SetColumnSpan(PasswordInputPanel, mode == 2 ? 3 : 1);
        LoginLabel.Text = mode switch { 1 => "Логин или email Ely.by", 2 => "Логин или email TLauncher.org", _ => "Ник игрока" };
        Nickname.PlaceholderText = mode == 0 ? "Как тебя будут звать в этом мире?" : "Логин аккаунта";
        AuthHint.Text = mode switch { 1 => "Пароль и токен не сохраняются. Код 2FA нужен, если включён в аккаунте.", 2 => "Вход в аккаунт TLauncher.org. Пароль не сохраняется. Подходит для нашего офлайн-сервера.", _ => "От 3 до 16 символов: латинские буквы, цифры и _" };
        SignInButton.Content = mode == 0 ? "Применить ник" : "Проверить и войти";
        LoginStatus.Text = "";
        Password.Text = Totp.Text = "";
    }
    void JavaModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (JavaPathPanel == null) return;
        bool custom = JavaMode.SelectedIndex == 1;
        JavaPathPanel.IsVisible = custom;
        JavaHint.Text = custom ? "Выбери Java 21 x64. Внешнюю Java нужно выбирать заново после переноса на другой ПК." : "Java скачивается и хранится в папке лаунчера.";
    }
    enum Page { Overview, Settings, ClientMods }
    void ShowPage(Page page)
    {
        OverviewPanel.IsVisible = page == Page.Overview;
        SettingsPanel.IsVisible = page == Page.Settings;
        ClientModsPanel.IsVisible = page == Page.ClientMods;
        OverviewButton.Classes.Set("active", OverviewPanel.IsVisible);
        SettingsButton.Classes.Set("active", SettingsPanel.IsVisible);
        ClientModsButton.Classes.Set("active", ClientModsPanel.IsVisible);
        PageTitle.Text = page switch { Page.Settings => "Настройки", Page.ClientMods => "Клиентские моды", _ => "Обзор" };
        if (page == Page.ClientMods) LoadOptionalMods();
    }

    void LoadOptionalMods()
    {
        OptionalModsList.Children.Clear();
        try
        {
            var mods = OptionalMods.Scan(service);
            var state = OptionalMods.Load(service);
            OptionalModsEmpty.IsVisible = mods.Count == 0;
            OptionalModsStatus.Text = mods.Count == 0 ? "Папка клиента: optional-mods/"
                : $"Найдено: {mods.Count} · Выбор сохраняется автоматически";
            foreach (var mod in mods)
            {
                var toggle = new ToggleSwitch { IsChecked = state.Enabled.Contains(mod.Id), IsEnabled = mod.Error == null,
                    OnContent = "Вкл", OffContent = "Выкл", VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
                Avalonia.Automation.AutomationProperties.SetName(toggle, mod.Name);
                var details = new StackPanel { Spacing = 5, Margin = new Thickness(0, 0, 18, 0), Children = {
                    new TextBlock { Text = mod.Name, FontWeight = FontWeight.SemiBold, FontSize = 15, TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = mod.Error ?? (string.IsNullOrEmpty(mod.Version) ? mod.FileName : mod.Version + " · " + mod.FileName),
                        Foreground = SolidColorBrush.Parse(mod.Error == null ? "#B9D2BF" : "#FFB8A6"), FontSize = 11, TextWrapping = TextWrapping.Wrap }
                } };
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { details, toggle } };
                Grid.SetColumn(toggle, 1);
                OptionalModsList.Children.Add(new Border { Classes = { "card" }, Padding = new Thickness(20, 16), Child = row });
                toggle.IsCheckedChanged += (_, _) =>
                {
                    try
                    {
                        OptionalMods.SetEnabled(service, mod.Id, toggle.IsChecked == true);
                        OptionalModsStatus.Text = "Сохранено. Изменения применятся при следующем запуске игры.";
                    }
                    catch (Exception error) { LoadOptionalMods(); OptionalModsStatus.Text = error.Message; }
                };
            }
            UpdateGameActions();
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            OptionalModsEmpty.IsVisible = false;
            OptionalModsStatus.Text = "Не удалось прочитать список: " + error.Message;
        }
    }

    async void RefreshOptionalModsClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => await Operation(async () =>
    {
        OperationTitle.Text = "Обновляем список модов";
        await Task.Run(() => service.RefreshOptionalMods(text => Dispatcher.UIThread.Post(() => Detail(text))));
        LoadOptionalMods();
        OperationTitle.Text = "Список модов обновлён";
        Detail("Опциональные моды загружены. Выбери, какие включить.");
    });
    void OverviewClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => ShowPage(Page.Overview);
    void SettingsClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => ShowPage(Page.Settings);
    void ClientModsClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => ShowPage(Page.ClientMods);
    void OpenBlueMapClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (Uri.TryCreate(service.Config.BlueMapUrl, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }
    void OpenGameClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Process.Start(new ProcessStartInfo(service.Game) { UseShellExecute = true });

    async void RefreshServerClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => await RefreshStatus();

    async Task RefreshStatus()
    {
        if (statusPending) return;
        statusPending = true;
        RefreshServerButton.IsEnabled = false;
        RefreshServerButton.Content = "Проверяем…";
        ServerState.Text = "Проверяем…";
        ServerDot.Fill = SolidColorBrush.Parse("#C8A46E");
        try { var state = await Task.Run(() => LauncherService.Status(service.Config.Server)); ServerState.Text = "Сервер доступен"; ServerDot.Fill = SolidColorBrush.Parse("#A8CD93"); PlayerCount.Text = $"{state.Online} / {state.Max}"; }
        catch { ServerState.Text = "Нет ответа"; ServerDot.Fill = SolidColorBrush.Parse("#C48A73"); PlayerCount.Text = "— / —"; }
        finally
        {
            statusPending = false;
            RefreshServerButton.IsEnabled = true;
            RefreshServerButton.Content = "↻ Обновить";
        }
    }
    async Task Operation(Func<Task> work)
    {
        if (busy) return;
        busy = true;
        UpdateGameActions();
        InstallButton.IsEnabled = PlayButton.IsEnabled = AccountButton.IsEnabled = SettingsPanel.IsEnabled = false;
        try { await work(); }
        catch (Exception error)
        {
            OperationTitle.Text = "Не получилось завершить";
            var text = error is TaskCanceledException ? "Сервис не ответил вовремя. Попробуй ещё раз." : error.Message;
            Detail(text);
            var window = new Window { Title = "Create Launcher", Width = 470, Height = 235, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = SolidColorBrush.Parse("#1B2921") };
            var close = new Button { Content = "Понятно", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
            close.Click += (_, _) => window.Close();
            window.Content = new StackPanel { Margin = new Thickness(24), Spacing = 20, Children = { new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxHeight = 130 }, close } };
            await window.ShowDialog(this);
        }
        finally { busy = false; AccountButton.IsEnabled = SettingsPanel.IsEnabled = true; UpdateGameActions(); }
    }
    Task InstallClient()
    {
        OperationTitle.Text = "Готовим твой клиент";
        return Task.Run(() => service.Install(text => Dispatcher.UIThread.Post(() => Detail(text)), ratio => Dispatcher.UIThread.Post(() => InstallProgress.Value = Math.Clamp(ratio * 100, 0, 100))));
    }
    async void InstallClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => await Operation(async () => { await InstallClient(); UpdateInstalled(); });
    async void PlayClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (activeSession == null || currentProfile == null) { AuthForm.IsVisible = true; AccountFlyout.ShowAt(AccountButton); LoginStatus.Text = "Выбери аккаунт или проверь его данные перед запуском."; return; }
        var session = activeSession;
        var profile = currentProfile;
        await Operation(async () => {
            OperationTitle.Text = "Заходим в игру";
            if (!service.IsInstalled) { await InstallClient(); UpdateInstalled(); }
            var prefs = service.LoadPreferences();
            Detail("Запускаем Minecraft…");
            await service.Launch(session, prefs.MemoryMb, profile.Provider == 1);
            OperationTitle.Text = "Хорошей игры, " + session.Username + "!";
            Detail("Minecraft запущен · сервер в списке мультиплеера");
            PlayButton.Content = "Играть  →";
            _ = ObserveGame();
        });
    }
    async void SignInClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => await SignIn();
    async Task SignIn()
    {
        if (busy) return;
        var login = Nickname.Text?.Trim() ?? "";
        var password = Password.Text ?? "";
        var totp = Totp.Text ?? "";
        int mode = AuthMode.SelectedIndex;
        Password.Text = Totp.Text = "";
        busy = true;
        SignInButton.IsEnabled = AuthMode.IsEnabled = Nickname.IsEnabled = InstallButton.IsEnabled = PlayButton.IsEnabled = SettingsPanel.IsEnabled = false;
        LoginStatus.Foreground = SolidColorBrush.Parse("#D1EDB5");
        LoginStatus.Text = mode == 0 ? "Проверяем ник…" : "Проверяем данные аккаунта…";
        try
        {
            var session = mode switch { 1 => await LauncherService.Ely(login, password, totp), 2 => await LauncherService.TLauncher(login, password), _ => LauncherService.Offline(login) };
            LauncherService.VerifyProfile(session);
            var profile = new PlayerProfile(session.Username!, session.UUID!, mode);
            var prefs = service.LoadPreferences();
            service.SavePreferences(mode == 0 ? session.Username! : "", prefs.MemoryMb, prefs.JavaPath);
            service.SaveProfile(profile);
            activeSession = session; currentProfile = profile;
            sessions[LauncherService.ProfileKey(profile)] = session;
            RefreshProfile();
            BuildSavedProfiles(); AuthForm.IsVisible = false;
            LoginStatus.Text = mode == 0 ? "Ник применён" : "Аккаунт проверен";
            Detail(mode == 0 ? "Ник выбран. Можно запускать игру." : "Данные аккаунта подтверждены сервисом.");
            AccountFlyout.Hide();
            _ = LoadSkin(profile);
        }
        catch (Exception error)
        {
            LoginStatus.Foreground = SolidColorBrush.Parse("#FFB8A6");
            LoginStatus.Text = error is TaskCanceledException ? "Сервис не ответил вовремя. Повтори попытку." : error.Message;
        }
        finally { busy = false; SignInButton.IsEnabled = AuthMode.IsEnabled = Nickname.IsEnabled = SettingsPanel.IsEnabled = true; UpdateGameActions(); }
    }
    void RefreshProfile()
    {
        ProfileName.Text = currentProfile?.Name ?? "Выбрать аккаунт";
        ProfileDetail.Text = currentProfile == null ? "Войти в игру" : (activeSession == null ? "Войти · " : "") + (currentProfile.Provider switch { 1 => "Ely.by", 2 => "TLauncher", _ => "По нику" });
        var greeting = Greetings.Format(greetingIndex, currentProfile?.Name);
        GreetingTitle.Text = greeting.Title; GreetingSubtitle.Text = greeting.Subtitle;
        Avatar.Reset(); ToolTip.SetTip(Avatar, "Скин пока не загружен");
    }
    async Task LoadSkin(PlayerProfile profile)
    {
        int request = ++skinRequest;
        try
        {
            var bytes = await service.GetSkin(profile);
            if (bytes != null && request == skinRequest) { Avatar.Load(bytes); ToolTip.SetTip(Avatar, "Скин " + profile.Name); }
        }
        catch (Exception error) when (error is IOException or ArgumentException) { }
    }
    void NewAccountClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (busy) return;
        AuthForm.IsVisible = true; Nickname.Text = ""; Password.Text = Totp.Text = ""; LoginStatus.Text = ""; Nickname.Focus();
    }
    static string ProviderName(int provider) => provider switch { 1 => "Ely.by", 2 => "TLauncher", _ => "По нику" };
    void BuildSavedProfiles()
    {
        int revision = ++profilesRevision;
        foreach (var head in savedHeads) head.Reset(); savedHeads.Clear(); SavedProfiles.Children.Clear();
        var profiles = service.LoadProfiles();
        SavedProfilesSection.IsVisible = profiles.Count > 0;
        foreach (var profile in profiles)
        {
            var key = LauncherService.ProfileKey(profile);
            var head = new SkinHead { Width = 30, Height = 30 }; savedHeads.Add(head);
            var name = new TextBlock { Text = profile.Name, FontSize = 12, FontWeight = FontWeight.SemiBold };
            var status = profile.Provider == 0 || sessions.ContainsKey(key) ? ProviderName(profile.Provider) : ProviderName(profile.Provider) + " · нужен вход";
            var labels = new StackPanel { Spacing = 3, Children = { name, new TextBlock { Text = status, FontSize = 9, Foreground = SolidColorBrush.Parse("#B9D2BF") } } };
            var content = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 10, Children = { head, labels } };
            var select = new Button { Content = content, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, Padding = new Thickness(10, 8), Background = SolidColorBrush.Parse(currentProfile != null && LauncherService.ProfileKey(currentProfile) == key ? "#3A6445" : "#294B36") };
            select.Click += (_, _) => SelectSavedProfile(profile);
            var remove = new Button { Content = "×", Padding = new Thickness(9, 5), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            ToolTip.SetTip(remove, "Удалить сохранённый аккаунт");
            remove.Click += (_, _) => {
                if (busy) return;
                service.RemoveProfile(profile); sessions.Remove(key);
                if (currentProfile != null && LauncherService.ProfileKey(currentProfile) == key) { currentProfile = null; activeSession = null; ++skinRequest; RefreshProfile(); }
                BuildSavedProfiles(); AuthForm.IsVisible = service.LoadProfiles().Count == 0;
            };
            var row = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto"), ColumnSpacing = 6 };
            row.Children.Add(select); Grid.SetColumn(remove, 1); row.Children.Add(remove); SavedProfiles.Children.Add(row);
            _ = LoadSavedHead(profile, head, revision);
        }
    }
    async Task LoadSavedHead(PlayerProfile profile, SkinHead head, int revision)
    {
        try { var bytes = await service.GetSkin(profile); if (bytes != null && revision == profilesRevision) head.Load(bytes); }
        catch (Exception e) when (e is IOException or ArgumentException) { }
    }
    void SelectSavedProfile(PlayerProfile profile)
    {
        if (busy) return;
        var key = LauncherService.ProfileKey(profile);
        MSession? session = profile.Provider == 0 ? LauncherService.Offline(profile.Name) : sessions.GetValueOrDefault(key);
        currentProfile = profile; activeSession = session;
        service.SaveProfile(profile);
        AuthMode.SelectedIndex = profile.Provider; Nickname.Text = profile.Name;
        Password.Text = Totp.Text = "";
        RefreshProfile(); BuildSavedProfiles(); _ = LoadSkin(profile);
        if (session != null) { AuthForm.IsVisible = false; AccountFlyout.Hide(); Detail("Выбран аккаунт " + profile.Name); }
        else { AuthForm.IsVisible = true; LoginStatus.Text = "Введи пароль для проверки и входа в сохранённый аккаунт."; Password.Focus(); }
    }
    async Task ObserveGame()
    {
        var process = service.GameProcess; if (process == null) return;
        await process.WaitForExitAsync(); UpdateGameActions(); if (busy) return;
        OperationTitle.Text = process.ExitCode == 0 ? "До новых встреч" : "Minecraft завершился с ошибкой";
        Detail(process.ExitCode == 0 ? "Клиент закрыт. Можно вернуться в игру." : "Журнал запуска: data/logs/game.log");
    }
    string SelectedJava() => JavaMode.SelectedIndex == 0 ? "" : JavaPathBox.Text?.Trim() ?? "";
    async void BrowseJavaClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Выбрать Java 21", AllowMultiple = false });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path != null) JavaPathBox.Text = path;
    }
    async void CheckJavaClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => await Operation(async () => {
        var path = SelectedJava();
        if (string.IsNullOrWhiteSpace(path))
        {
            foreach (var candidate in service.FindJavaCandidates().Where(x => x.StartsWith(service.Game)))
            {
                try { JavaHint.Text = await LauncherService.CheckJava(candidate) + " · портативная"; return; }
                catch (Exception error) when (error is IOException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
            JavaHint.Text = "Портативная Java 21 пока не установлена. Она загрузится вместе с клиентом.";
            return;
        }
        JavaHint.Text = await LauncherService.CheckJava(Path.GetFullPath(path, service.Root));
    });
    async void FindJavaClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => await Operation(async () => {
        OperationTitle.Text = "Ищем Java 21";
        var found = new List<(string Path, string Description)>();
        foreach (var candidate in service.FindJavaCandidates())
        {
            Detail("Проверяем " + candidate);
            try { found.Add((candidate, await LauncherService.CheckJava(candidate))); }
            catch (Exception error) when (error is IOException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        if (found.Count == 0) { JavaHint.Text = "Java 21 x64 не найдена. Выбери автоматический режим или укажи путь вручную."; return; }
        var dialog = new Window { Title = "Найденные Java 21", Width = 660, Height = 350, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = SolidColorBrush.Parse("#1B2921") };
        var list = new ListBox { ItemsSource = found.Select(x => x.Description + "\n" + x.Path).ToArray(), SelectedIndex = 0 };
        var choose = new Button { Content = "Использовать выбранную", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        choose.Click += (_, _) => { if (list.SelectedIndex < 0) return; JavaMode.SelectedIndex = 1; JavaPathBox.Text = found[list.SelectedIndex].Path; JavaHint.Text = found[list.SelectedIndex].Description; dialog.Close(); };
        var grid = new Grid { Margin = new Thickness(20), RowDefinitions = RowDefinitions.Parse("*,Auto"), RowSpacing = 16 };
        grid.Children.Add(list); Grid.SetRow(choose, 1); grid.Children.Add(choose); dialog.Content = grid;
        await dialog.ShowDialog(this);
        OperationTitle.Text = "Java найдена"; Detail("Сохрани настройки, чтобы применить выбранную Java.");
    });
    async void SaveSettingsClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => await Operation(async () => {
        if (MemoryInput.Value is not decimal memory || memory != decimal.Truncate(memory)) throw new ArgumentException("Укажи целое число МБ памяти");
        var java = SelectedJava();
        if (JavaMode.SelectedIndex == 1 && string.IsNullOrWhiteSpace(java)) throw new ArgumentException("Выбери исполняемый файл Java");
        if (!string.IsNullOrWhiteSpace(java)) JavaHint.Text = await LauncherService.CheckJava(Path.GetFullPath(java, service.Root));
        var prefs = service.LoadPreferences();
        service.SavePreferences(prefs.Nickname, (int)memory, java);
        SettingsHint.Text = "Настройки сохранены"; OperationTitle.Text = "Настройки сохранены"; Detail("Выбранные Java и память применятся при следующем запуске.");
    });
    static void MakeIcon(string path)
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Assets", "logo.ico"));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllBytes(path, bytes);
    }
}
