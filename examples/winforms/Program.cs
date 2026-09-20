using System;
using System.Windows.Forms;
internal static class Program
{
    [STAThread] static void Main()
    {
        Application.EnableVisualStyles();
        var form = new Form { Text = "Winix WinForms Framework", Width = 400, Height = 220 };
        var result = new TextBox { Name = "Result", AccessibleName = "Result", Text = "Ready", Top = 70, Left = 20, Width = 220 };
        var button = new Button { Name = "Change", AccessibleName = "Change", Text = "Change", Top = 20, Left = 20 };
        button.Click += (_, _) => result.Text = "Passed";
        form.Controls.Add(button); form.Controls.Add(result); Application.Run(form);
    }
}
