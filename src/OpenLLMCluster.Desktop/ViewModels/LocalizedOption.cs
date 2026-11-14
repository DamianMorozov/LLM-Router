namespace OpenLLMCluster.Desktop.ViewModels;

/// <summary>Обновляет подпись выбранного элемента ComboBox при смене языка</summary>
public sealed class LocalizedOption : ObservableObject
{
    private readonly Localizer _text;
    private readonly string _key;
    public string Label => _text[_key];

    /// <summary>Связывает элемент со словарём на время жизни окна</summary>
    public LocalizedOption(Localizer text, string key)
    {
        _text = text;
        _key = key;
        text.PropertyChanged += (_, _) => OnPropertyChanged(nameof(Label));
    }
}
