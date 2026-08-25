using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Aerochat.Presentation;

namespace Aerochat.Windows;

public partial class ChangeScene : Window
{
    private readonly PresentationState _state;
    public ObservableCollection<SceneChoice> Scenes { get; } = [];

    public ChangeScene(PresentationState state)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        foreach (ScenePresentation scene in state.Scenes)
            Scenes.Add(new SceneChoice(scene, scene == state.CurrentScene));
        InitializeComponent();
        DataContext = this;
    }

    private void Border_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border { DataContext: SceneChoice choice })
        {
            foreach (SceneChoice item in Scenes) item.Selected = false;
            choice.Selected = true;
            _state.SelectScene(choice.Scene);
        }
    }

    private void Button_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Name: "CloseButton" or "OkButton" })
            Close();
    }
}

public sealed class SceneChoice(ScenePresentation scene, bool selected)
{
    public ScenePresentation Scene { get; } = scene;
    public bool Selected { get; set; } = selected;
}
