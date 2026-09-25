using System;
using Avalonia.Controls;
using DeskNest.App.ViewModels;

namespace DeskNest.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        var vm = new MainWindowViewModel();
        DataContext = vm;

        Closed += async (s, e) =>
        {
            if (DataContext is MainWindowViewModel mvm)
            {
                await mvm.DisposeAsync();
            }
        };
    }

    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        Closed += async (s, e) =>
        {
            if (DataContext is MainWindowViewModel mvm)
            {
                await mvm.DisposeAsync();
            }
        };
    }
}
