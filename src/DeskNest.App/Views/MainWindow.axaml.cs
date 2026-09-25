using Avalonia.Controls;
using DeskNest.App.ViewModels;

namespace DeskNest.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainWindowViewModel();
    }
}
