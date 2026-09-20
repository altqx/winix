using Windows.ApplicationModel.Activation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Automation;
namespace WinixUwp
{
    sealed partial class App : Application
    {
        public App() { InitializeComponent(); }
        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            var result = new TextBox { Text = "Ready" }; AutomationProperties.SetAutomationId(result, "Result");
            var button = new Button { Content = "Change" }; AutomationProperties.SetAutomationId(button, "Change");
            button.Click += (sender, e) => result.Text = "Passed";
            var panel = new StackPanel(); panel.Children.Add(button); panel.Children.Add(result);
            Window.Current.Content = panel; Window.Current.Activate();
        }
    }
}
