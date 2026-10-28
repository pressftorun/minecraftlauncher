namespace CreateLauncher;

internal static class LocalizationTests
{
    public static void Run()
    {
        static void Check(bool value) { if (!value) throw new Exception("Localization check failed"); }
        try
        {
            Localization.SetLanguage("en");
            Check(Localization.Text("Настройки") == "Settings");
            Check(Localization.Format("Найдено: {0} · Выбор сохраняется автоматически", 3) == "Found: 3 · Your selection is saved automatically");
            Check(Localization.Translate("Установлена последняя версия 0.3.4") == "You have the latest version: 0.3.4");
            Check(Localization.Translate("Доступен Create Launcher 0.4.0") == "Create Launcher 0.4.0 is available");
            Check(Localization.Translate("Ely.by · нужен вход") == "Ely.by · sign-in required");
            foreach (var pair in Localization.English)
            {
                Check(!string.IsNullOrWhiteSpace(pair.Value));
                Check(!System.Text.RegularExpressions.Regex.IsMatch(pair.Value, "[А-Яа-яЁё]"));
            }
            foreach (var i in Enumerable.Range(0, Greetings.All.Length))
            {
                var greeting = Greetings.Format(i, "Player");
                Check(!System.Text.RegularExpressions.Regex.IsMatch(greeting.Title + greeting.Subtitle, "[А-Яа-яЁё]"));
            }
            Check(Greetings.Format(0, null).Title == "Welcome back, builder.");
            Check(Localization.Translate("C:/Games/Player.jar") == "C:/Games/Player.jar");
            Localization.SetLanguage("ru");
            Check(Localization.Translate("Create Launcher 0.4.0 is available") == "Доступен Create Launcher 0.4.0");
            Check(Localization.Text("Настройки") == "Настройки");
            Localization.SetLanguage("unknown");
            Check(Localization.Language == "ru");
        }
        finally { Localization.SetLanguage("ru"); }
    }
}
