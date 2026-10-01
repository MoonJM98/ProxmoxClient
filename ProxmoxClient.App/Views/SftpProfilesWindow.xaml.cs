using System.Windows;
using ProxmoxClient.App.Views.Shared;
using ProxmoxClient.Core.Files;

namespace ProxmoxClient.App.Views;

public partial class SftpProfilesWindow : Window
{
    private readonly SftpProfilesEditor _editor;
    public Guid? SelectedProfileId => _editor.SelectedProfileId;
    internal string? SelectedPassword => _editor.SelectedPassword;
    internal SftpAccountProfile? SelectedAccount => _editor.SelectedAccount;

    public SftpProfilesWindow(Guid? selectedProfileId)
    {
        InitializeComponent();
        WindowTheme.ApplyDarkTitleBar(this);
        _editor = new SftpProfilesEditor(selectedProfileId, true);
        EditorHost.Children.Add(_editor);
        _editor.ConnectionSelected += (_, _) => DialogResult = true;
    }
}
