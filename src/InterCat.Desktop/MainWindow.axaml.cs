using Avalonia.Controls;
using Avalonia.Interactivity;

namespace InterCat.Desktop;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        var viewModel = new WorkspaceViewModel();
        DataContext = viewModel;
        viewModel.PropertyChanged += (_, _) =>
        {
            GraphSurface.InvalidateVisual();
            TimelineSurface.InvalidateVisual();
        };
    }

    private void ClearSelection(object? sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is WorkspaceViewModel viewModel)
        {
            viewModel.ClearSelection();
        }
    }
}
