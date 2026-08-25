using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Aerochat.Presentation;

namespace Aerochat.Windows;

public partial class Settings : Window
{
    private readonly PresentationState _state;
    public SettingsPresentationView ViewModel { get; }

    public Settings(PresentationState state)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        ViewModel = SettingsPresentationView.Create(state);
        InitializeComponent();
        DataContext = ViewModel;
        CategoriesListBox.SelectedIndex = 0;
    }

    private void ListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CategoriesListBox.SelectedItem is SettingsCategoryPresentation category)
            ViewModel.Select(category);
    }

    private void TextBox_PreviewTextInput(object sender, TextCompositionEventArgs e) =>
        e.Handled = !int.TryParse(e.Text, out _);

    private void TextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox box && box.DataContext is SettingPresentation item)
            Apply(item.Key, box.Text);
    }

    private void CheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox box && box.DataContext is SettingPresentation item)
            Apply(item.Key, box.IsChecked == true);
    }

    private void ComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox box && box.DataContext is SettingPresentation item)
            Apply(item.Key, box.SelectedItem?.ToString() ?? "");
    }

    private void StringList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ComboBox_SelectionChanged(sender, e);

    private void Apply(string key, object value)
    {
        switch (key)
        {
            case nameof(VisualSettingsPresentation.ShowAds): _state.Settings.ShowAds = Convert.ToBoolean(value); break;
            case nameof(VisualSettingsPresentation.ShowNews): _state.Settings.ShowNews = Convert.ToBoolean(value); break;
            case nameof(VisualSettingsPresentation.ShowEyecandy): _state.Settings.ShowEyecandy = Convert.ToBoolean(value); break;
            case nameof(VisualSettingsPresentation.ShowTimestamps): _state.Settings.ShowTimestamps = Convert.ToBoolean(value); break;
            case nameof(VisualSettingsPresentation.EnableAnimations): _state.Settings.EnableAnimations = Convert.ToBoolean(value); break;
            case nameof(VisualSettingsPresentation.Language): _state.Settings.Language = value.ToString() ?? "en-US"; break;
            case nameof(VisualSettingsPresentation.TimeFormat): _state.Settings.TimeFormat = value.ToString() ?? "24-hour"; break;
        }
    }
}

public sealed class SettingsPresentationView
{
    private SettingsPresentationView(PresentationState state)
    {
        State = state;
        Categories.Add(new SettingsCategoryPresentation("General", "General"));
        Categories.Add(new SettingsCategoryPresentation("Visual", "Visual"));
        Categories.Add(new SettingsCategoryPresentation("Chat", "Chat"));
        foreach (SettingsCategoryPresentation category in Categories)
            category.Items = CreateItems(category.Key, state);
        SelectedCategory = Categories[0];
    }

    public PresentationState State { get; }
    public ObservableCollection<SettingsCategoryPresentation> Categories { get; } = [];
    public ObservableCollection<SettingPresentation> SettingsItems { get; } = [];
    public SettingsCategoryPresentation SelectedCategory { get; private set; }

    public void Select(SettingsCategoryPresentation category)
    {
        SelectedCategory = category;
        SettingsItems.Clear();
        foreach (SettingPresentation item in category.Items)
            SettingsItems.Add(item);
    }

    public static SettingsPresentationView Create(PresentationState state)
    {
        var view = new SettingsPresentationView(state);
        view.Select(view.SelectedCategory);
        return view;
    }

    private static ObservableCollection<SettingPresentation> CreateItems(string key, PresentationState state) => key switch
    {
        "General" => [
            SettingPresentation.String(nameof(VisualSettingsPresentation.Language), "Language", state.Settings.Language, ["en-US", "ja-JP"]),
            SettingPresentation.String(nameof(VisualSettingsPresentation.TimeFormat), "Time format", state.Settings.TimeFormat, ["12-hour", "24-hour"])],
        "Visual" => [
            SettingPresentation.Boolean(nameof(VisualSettingsPresentation.ShowAds), "Show advertisements", state.Settings.ShowAds),
            SettingPresentation.Boolean(nameof(VisualSettingsPresentation.ShowNews), "Show news", state.Settings.ShowNews),
            SettingPresentation.Boolean(nameof(VisualSettingsPresentation.ShowEyecandy), "Show visual effects", state.Settings.ShowEyecandy),
            SettingPresentation.Boolean(nameof(VisualSettingsPresentation.ShowTimestamps), "Show timestamps", state.Settings.ShowTimestamps),
            SettingPresentation.Boolean(nameof(VisualSettingsPresentation.EnableAnimations), "Enable animations", state.Settings.EnableAnimations)],
        "Chat" => [SettingPresentation.Boolean(nameof(VisualSettingsPresentation.ShowTimestamps), "Show timestamps", state.Settings.ShowTimestamps)],
        _ => []
    };
}

public sealed class SettingsCategoryPresentation(string key, string name)
{
    public string Key { get; } = key;
    public string Name { get; } = name;
    public ObservableCollection<SettingPresentation> Items { get; set; } = [];
}

public sealed class SettingPresentation
{
    private SettingPresentation(string key, string name, string type, object value)
    {
        Key = key;
        Name = name;
        Type = type;
        DefaultValue = value;
        StringValues = [];
    }

    public string Key { get; }
    public string Name { get; }
    public string Type { get; }
    public object DefaultValue { get; set; }
    public string? Note { get; init; }
    public ObservableCollection<string> StringValues { get; }
    public ObservableCollection<string> EnumValues { get; } = [];
    public string? SelectedEnumValue { get; set; }

    public static SettingPresentation Boolean(string key, string name, bool value) =>
        new(key, name, "Boolean", value);

    public static SettingPresentation String(string key, string name, string value, IEnumerable<string> values)
    {
        var item = new SettingPresentation(key, name, "StringList", value);
        foreach (string option in values) item.StringValues.Add(option);
        return item;
    }
}
