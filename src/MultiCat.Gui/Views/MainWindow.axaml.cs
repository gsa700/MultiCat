using Avalonia.Controls;
using Avalonia.Interactivity;
using MultiCat.Contracts;
using MultiCat.Gui.ViewModels;

namespace MultiCat.Gui.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Closed += (_, _) => (DataContext as MainViewModel)?.Shutdown();
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private async void OnAddRadio(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { CanEdit: true } vm)
        {
            return;
        }

        var editor = new RadioEditorViewModel(existing: null, comPorts: await vm.GetComPortsAsync());
        await ShowEditorAsync(editor);
    }

    private async void OnEditRadio(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { CanEdit: true } vm || vm.SelectedRadio is null)
        {
            return;
        }

        var config = await vm.GetConfigAsync(vm.SelectedRadio.Name);
        if (config is null)
        {
            return;
        }

        var editor = new RadioEditorViewModel(config, await vm.GetComPortsAsync());
        await ShowEditorAsync(editor);
    }

    private async Task ShowEditorAsync(RadioEditorViewModel editor)
    {
        var dialog = new RadioEditorWindow(editor);
        var result = await dialog.ShowDialog<SaveRadioRequest?>(this);
        if (result is not null && ViewModel is { } vm)
        {
            await vm.SaveRadioAsync(result);
        }
    }

    private async void OnDeleteRadio(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { CanEdit: true } vm || vm.SelectedRadio is null)
        {
            return;
        }

        var name = vm.SelectedRadio.Name;
        var confirm = new Window
        {
            Title = "Remove radio",
            Width = 340,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var yes = new Button { Content = "Remove", MinWidth = 80, Background = Avalonia.Media.Brush.Parse("#C0392B"), Foreground = Avalonia.Media.Brushes.White };
        var no = new Button { Content = "Cancel", MinWidth = 80 };
        yes.Click += (_, _) => confirm.Close(true);
        no.Click += (_, _) => confirm.Close(false);
        confirm.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(18),
            Spacing = 14,
            Children =
            {
                new TextBlock { Text = $"Remove \"{name}\"? Apps connected to it will lose the radio.", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { no, yes },
                },
            },
        };

        if (await confirm.ShowDialog<bool>(this))
        {
            await vm.DeleteRadioAsync(name);
        }
    }
}
