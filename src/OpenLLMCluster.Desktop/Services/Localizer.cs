namespace OpenLLMCluster.Desktop.Services;

/// <summary>Обновляет локализованные привязки без перезапуска окна</summary>
public sealed class Localizer : INotifyPropertyChanged
{
    private readonly Dictionary<string, Dictionary<string, string>> _languages = [];
    public string Language { get; private set; } = "ru";
    public event PropertyChangedEventHandler? PropertyChanged;
    public string this[string key] => _languages[Language].GetValueOrDefault(key) ?? _languages["en"].GetValueOrDefault(key) ?? key;

    /// <summary>Загружает встроенные словари русского и английского языков</summary>
    public Localizer()
    {
        foreach (var language in new[] { "ru", "en" })
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"OpenLLMCluster.Desktop.Localization.{language}.json")
                ?? throw new InvalidDataException("Не найден словарь локализации");
            _languages[language] = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
        }
    }

    /// <summary>Переключает язык и уведомляет все индексные привязки</summary>
    public void SetLanguage(string language)
    {
        if (language == "system") language = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        Language = _languages.ContainsKey(language) ? language : "en";
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
    }
}
