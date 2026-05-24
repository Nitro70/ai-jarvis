using System.Windows.Controls;
using Jarvis.Setup.Models;

namespace Jarvis.Setup.Views;

public partial class ToolsView : UserControl
{
    public ToolsView() { InitializeComponent(); }
    public void Bind(InstallConfig cfg) => DataContext = cfg;
}
