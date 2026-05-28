using System.Windows.Controls;
using Jarvis.Setup.NET.Models;

namespace Jarvis.Settings.NET.Views;

public partial class ToolsView : UserControl
{
    public ToolsView() { InitializeComponent(); }
    public void Bind(InstallConfig cfg) => DataContext = cfg;
}
