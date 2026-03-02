namespace ArchiveAutomator.Views;

public partial class ExecutionView : System.Windows.Controls.UserControl
{
    public ExecutionView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Keeps the log TextBox scrolled to the latest entry whenever new text is appended.
    /// </summary>
    private void LogTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        => ((System.Windows.Controls.TextBox)sender).ScrollToEnd();
}
