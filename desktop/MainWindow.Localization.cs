using Avalonia.Controls;
using Avalonia.LogicalTree;

namespace CreateLauncher;

public partial class MainWindow
{
    bool changingLanguage;
    string LanguagePath => Path.Combine(service.Data, "language.json");

    void InitializeLanguage()
    {
        string language = "ru";
        try { language = LauncherService.Read<string>(LanguagePath); }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException) { }
        ApplyLanguage(language);
    }

    void ApplyLanguage(string language)
    {
        changingLanguage = true;
        try
        {
            Localization.SetLanguage(language);
            int index = 0;
            foreach (var russian in Localization.English.Keys)
                Resources["L" + index++] = Localization.Text(russian);
            LanguageSelector.SelectedIndex = Localization.Language == "en" ? 1 : 0;
            // Only launcher-owned labels: account names, mod metadata and paths are user data.
            foreach (var name in new[] { "PageTitle", "ProfileDetail", "LoginLabel", "AuthHint", "LoginStatus",
                "LauncherUpdateStatus", "LauncherUpdateBannerText", "ServerState", "OperationTitle",
                "OperationDetail", "JavaHint", "SettingsHint", "OptionalModsStatus" })
                if (this.FindControl<TextBlock>(name) is { Text: { } text } label)
                    label.Text = Localization.Translate(text);
            foreach (var name in new[] { "PlayButton", "SignInButton", "RefreshServerButton" })
                if (this.FindControl<Button>(name) is { Content: string text } button)
                    button.Content = Localization.Translate(text);
            Nickname.PlaceholderText = Localization.Translate(Nickname.PlaceholderText ?? "");
            var greeting = Greetings.Format(greetingIndex, currentProfile?.Name);
            GreetingTitle.Text = greeting.Title;
            GreetingSubtitle.Text = greeting.Subtitle;
            if (currentProfile == null) ProfileName.Text = Localization.Text("Выбрать аккаунт");
        }
        finally { changingLanguage = false; }
    }

    void LanguageChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (changingLanguage || service == null || LanguageSelector.SelectedIndex < 0) return;
        var language = LanguageSelector.SelectedIndex == 1 ? "en" : "ru";
        try { LauncherService.Write(LanguagePath, language); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            LanguageSelector.SelectedIndex = Localization.Language == "en" ? 1 : 0;
            SettingsHint.Text = error.Message;
            return;
        }
        ApplyLanguage(language);
        BuildSavedProfiles();
        if (this.FindControl<StackPanel>("OptionalModsList") is { } mods)
            foreach (var control in mods.GetLogicalDescendants())
            {
                if (control is ToggleSwitch toggle)
                {
                    toggle.OnContent = Localization.Text("Вкл");
                    toggle.OffContent = Localization.Text("Выкл");
                }
                if (control is TextBlock { FontSize: 11, Text: { } text } label)
                    label.Text = Localization.Translate(text);
            }
    }
}
