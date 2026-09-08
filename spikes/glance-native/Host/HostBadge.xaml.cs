namespace DeskBox.Glance.NativeHost;

public sealed partial class HostBadge : Microsoft.UI.Xaml.Controls.UserControl
{
    public HostBadge()
    {
        InitializeComponent();
    }

    public string Label
    {
        get => LabelText.Text;
        set => LabelText.Text = value;
    }
}
