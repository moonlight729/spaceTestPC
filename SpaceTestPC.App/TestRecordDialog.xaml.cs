using System.Windows;
using SpaceTestPC.App.Models;

namespace SpaceTestPC.App;

public partial class TestRecordDialog : Window
{
    public TestRecordDialog(TestSessionRecord record)
    {
        InitializeComponent();
        DataContext = record;
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e) => Close();
}
