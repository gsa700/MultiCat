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
        SignalFlow.ClientClicked += OnClientRename;
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private async void OnClientRename(ClientConnectionViewModel client)
    {
        if (ViewModel is not { CanEdit: true } vm)
        {
            return;
        }

        var box = new TextBox { Text = client.DisplayName, Watermark = client.ProcessName, MinWidth = 240 };
        var dialog = new Window
        {
            Title = "Rename connection",
            Width = 320,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var save = new Button { Content = "Save", MinWidth = 80, IsDefault = true };
        var cancel = new Button { Content = "Cancel", MinWidth = 80, IsCancel = true };
        save.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);
        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(18),
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = $"Nickname for \"{client.ProcessName}\"", FontSize = 12, Foreground = Avalonia.Media.Brush.Parse("#8E8E8E") },
                box,
                new TextBlock { Text = "Applies to every connection from this app. Clear to reset.", FontSize = 11, Foreground = Avalonia.Media.Brush.Parse("#8E8E8E"), TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancel, save },
                },
            },
        };

        if (await dialog.ShowDialog<bool>(this))
        {
            await vm.SetClientNicknameAsync(client.ProcessName, box.Text ?? string.Empty);
        }
    }

    private async void OnToggleFlexAdvertising(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { CanEdit: true } vm || vm.SelectedRadio is not { } radio)
        {
            return;
        }

        // Starting is the consequential direction: the stack begins following this
        // radio and moves real antenna and amplifier state, so say so first. Stopping
        // is the safe direction and needs no ceremony.
        if (!radio.FlexAdvertising)
        {
            var targets = radio.FlexTargets.Length > 0 ? radio.FlexTargets : "the local network";
            if (!await ConfirmAsync(
                    "Advertise to the Genius stack?",
                    $"MultiCAT will announce \"{radio.Name}\" as a radio to {targets}.\n\n" +
                    "Any 4O3A box that follows it will switch antennas and set amplifier band " +
                    "from this radio's transmit frequency.",
                    "Start advertising"))
            {
                return;
            }
        }

        await vm.ToggleFlexAdvertisingAsync();
    }

    private async Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var yes = new Button { Content = confirmText, MinWidth = 120, IsDefault = true };
        var no = new Button { Content = "Cancel", MinWidth = 80, IsCancel = true };
        yes.Click += (_, _) => dialog.Close(true);
        no.Click += (_, _) => dialog.Close(false);
        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(18),
            Spacing = 14,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { no, yes },
                },
            },
        };

        return await dialog.ShowDialog<bool>(this);
    }

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

    private async void OnAddPort(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { CanEdit: true } vm || vm.SelectedRadio is null)
        {
            return;
        }

        var type = new ComboBox
        {
            ItemsSource = new[]
            {
                "rigctld (WSJT-X, fldigi, hamlib)",
                "raw CAT over TCP",
                "Genius stack (Flex)",
                "OmniRig Rig 1 — connects but does not track",
                "OmniRig Rig 2 — connects but does not track",
            },
            SelectedIndex = 0,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
        };
        var port = new TextBox { PlaceholderText = "auto (leave blank)", Width = 160, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
        var label = new TextBox { PlaceholderText = "optional", Width = 260, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };

        // Flex-only fields, hidden until that endpoint is chosen.
        var callsign = new TextBox { PlaceholderText = "station callsign", Width = 160, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
        var targets = new TextBox { PlaceholderText = "10.0.1.20, 10.0.1.21 (blank = broadcast)", Width = 300, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
        var flexFields = new StackPanel
        {
            Spacing = 10,
            IsVisible = false,
            Children =
            {
                new TextBlock { Text = "Callsign", FontSize = 11, Foreground = Avalonia.Media.Brush.Parse("#8E8E8E") },
                callsign,
                new TextBlock { Text = "Genius box addresses", FontSize = 11, Foreground = Avalonia.Media.Brush.Parse("#8E8E8E") },
                targets,
                new TextBlock
                {
                    Text = "Listing boxes keeps the radio invisible to everything else on the network. "
                         + "Adding the port does not start advertising.",
                    FontSize = 11,
                    Foreground = Avalonia.Media.Brush.Parse("#8E8E8E"),
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                },
            },
        };
        type.SelectionChanged += (_, _) => flexFields.IsVisible = type.SelectedIndex == 2;

        var dialog = new Window
        {
            Title = "Add network port",
            Width = 360,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var add = new Button { Content = "Add", MinWidth = 80, IsDefault = true };
        var cancel = new Button { Content = "Cancel", MinWidth = 80, IsCancel = true };
        add.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);
        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(18),
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = "Endpoint", FontSize = 11, Foreground = Avalonia.Media.Brush.Parse("#8E8E8E") },
                type,
                new TextBlock { Text = "TCP port", FontSize = 11, Foreground = Avalonia.Media.Brush.Parse("#8E8E8E") },
                port,
                new TextBlock { Text = "Label", FontSize = 11, Foreground = Avalonia.Media.Brush.Parse("#8E8E8E") },
                label,
                flexFields,
                new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Spacing = 8,
                    Margin = new Avalonia.Thickness(0, 6, 0, 0),
                    Children = { cancel, add },
                },
            },
        };

        if (await dialog.ShowDialog<bool>(this))
        {
            var (endpointType, omnirigRig) = type.SelectedIndex switch
            {
                1 => ("rawtcp", 0),
                2 => ("flex", 0),
                3 => ("omnirig", 1),
                4 => ("omnirig", 2),
                _ => ("rigctld", 0),
            };
            _ = int.TryParse(port.Text, out var portNumber);
            await vm.AddPortAsync(
                endpointType, portNumber, label.Text ?? string.Empty, omnirigRig,
                callsign.Text ?? string.Empty, targets.Text ?? string.Empty);
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
