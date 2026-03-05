using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Orpheus.iOS;

public partial class MainView : UserControl
{
    public MainView()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
