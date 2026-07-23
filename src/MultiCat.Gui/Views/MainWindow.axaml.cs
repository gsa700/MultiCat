using Avalonia.Controls;
using MultiCat.Gui.ViewModels;

namespace MultiCat.Gui.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Closed += (_, _) => (DataContext as MainViewModel)?.Shutdown();
    }
}
