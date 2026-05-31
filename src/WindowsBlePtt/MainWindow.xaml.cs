using System.Windows;
using WindowsBlePtt.ViewModels;

namespace WindowsBlePtt;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private MainViewModel ViewModel => (MainViewModel)DataContext;

    private void OnScanClick(object sender, RoutedEventArgs e) => ViewModel.StartScan();

    private async void OnDisconnectClick(object sender, RoutedEventArgs e) => await ViewModel.DisconnectAsync();
}
