using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
namespace WinixWinUI;
public partial class App : Application
{
    private Window? window;
    public App() { InitializeComponent(); }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var result = new TextBox { Text = "Ready" }; AutomationProperties.SetAutomationId(result, "Result");
        var button = new Button { Content = "Change" }; AutomationProperties.SetAutomationId(button, "Change");
        button.Click += (_, _) => result.Text = "Passed";
        var panel = new StackPanel { Spacing = 12 }; panel.Children.Add(button); panel.Children.Add(result);
        window = new Window { Title = "Winix WinUI", Content = panel }; window.Activate();
    }
}
