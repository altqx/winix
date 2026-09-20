using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
internal static class Program
{
    [System.STAThread] static void Main()
    {
        var result = new TextBox { Text = "Ready", Margin = new Thickness(12) }; AutomationProperties.SetAutomationId(result, "Result");
        var button = new Button { Content = "Change", Margin = new Thickness(12) }; AutomationProperties.SetAutomationId(button, "Change");
        button.Click += (_, _) => result.Text = "Passed";
        var panel = new StackPanel(); panel.Children.Add(button); panel.Children.Add(result);
        new Application().Run(new Window { Title = "Winix WPF", Width = 400, Height = 220, Content = panel });
    }
}
