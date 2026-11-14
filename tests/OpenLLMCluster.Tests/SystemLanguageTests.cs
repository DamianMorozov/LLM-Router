// Проверяет настоящий список языков, язык интерфейса ОС и сохранение системного режима вместо разрешённого языка
namespace OpenLLMCluster.Tests;

public sealed class SystemLanguageTests
{
    [AvaloniaTheory]
    [InlineData("ru-RU", "ru")]
    [InlineData("en-US", "en")]
    [InlineData("de-DE", "en")]
    public void SystemOptionUsesOsUiLanguageAndRestoresModeOnNextStartup(string culture, string expected)
    {
        var root = Path.Combine(Path.GetTempPath(), "open-llm-cluster-system-language-" + Guid.NewGuid().ToString("N"));
        var original = System.Globalization.CultureInfo.CurrentUICulture;
        var store = new ProfileStore(Path.Combine(root, "profile.json"));
        System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo(culture);
        using var vm = new MainWindowViewModel(store);
        var window = new MainWindow { DataContext = vm };
        try
        {
            System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo(culture);
            window.Show();
            var selector = window.FindControl<ComboBox>("LanguageSelector")!;
            Assert.Equal(3, selector.ItemCount);
            selector.SelectedIndex = 0;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, vm.LanguageIndex);
            Assert.Equal(expected, vm.Text.Language);
            Assert.Equal(vm.Text["LanguageSystem"], ((ComboBoxItem)selector.Items[0]!).Content);
            vm.SaveOnClose();
            Assert.Equal("system", store.Load().Language);
            System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo(expected == "ru" ? "en-US" : "ru-RU");
            using var reopened = new MainWindowViewModel(store);
            reopened.Initialize();
            Assert.Equal(0, reopened.LanguageIndex);
            Assert.Equal(expected == "ru" ? "en" : "ru", reopened.Text.Language);
        }
        finally
        {
            window.Close();
            System.Globalization.CultureInfo.CurrentUICulture = original;
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
