using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Orpheus.Android;

public partial class MainView : UserControl
{
    public MainView()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
