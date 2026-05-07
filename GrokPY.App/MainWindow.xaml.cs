using System.Windows;
using GrokPY.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace GrokPY.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<MainViewModel>();
    }
}
