using ArchiveAutomator.ViewModels;
using System.Windows;
using System.Windows.Controls;

namespace ArchiveAutomator;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // PasswordBox.Password cannot be data-bound (by design — it's not a DependencyProperty).
        // Wire it manually so the ViewModel receives the value on every keystroke.
        PwdClientSecret.PasswordChanged += OnClientSecretChanged;
    }

    private void OnClientSecretChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            vm.SetBoxClientSecret(PwdClientSecret.Password);
    }
}
