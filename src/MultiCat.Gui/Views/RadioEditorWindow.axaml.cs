using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using MultiCat.Contracts;
using MultiCat.Gui.ViewModels;

namespace MultiCat.Gui.Views;

public partial class RadioEditorWindow : Window
{
    public RadioEditorWindow()
    {
        InitializeComponent();
    }

    public RadioEditorWindow(RadioEditorViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        if (DataContext is RadioEditorViewModel vm && vm.Build() is { } config)
        {
            Close(new SaveRadioRequest { OriginalName = vm.OriginalName ?? string.Empty, Radio = config });
        }
    }
}
