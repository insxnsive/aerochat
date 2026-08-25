using System.Collections.ObjectModel;
using System.Windows.Controls;
using Aerochat.Presentation;

namespace Aerochat.Controls.AttachmentsEditor;

public partial class AttachmentsStrip : UserControl
{
    public ObservableCollection<PreviewImagePresentation> Items { get; } = [];

    public AttachmentsStrip()
    {
        foreach (PreviewImagePresentation image in DemoData.Create().PreviewImages)
            Items.Add(image);
        InitializeComponent();
        DataContext = this;
    }
}
