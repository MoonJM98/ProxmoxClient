using System.Windows;
using System.Windows.Controls;
using ProxmoxClient.App.Localization;

namespace ProxmoxClient.App.Views.Shared;

/// <summary>
///     서버의 텍스트 파일(/etc/hosts 등)을 고치는 탭. 읽을 때 받은 digest 를 저장 때 돌려보내
///     그사이 다른 곳에서 바뀌었으면 서버가 거절하게 한다(덮어쓰기 방지).
/// </summary>
public partial class TextEditTab : UserControl
{
    private readonly Func<Task<(string Text, string Digest)>> _load;
    private readonly Func<string, string, Task>? _save;
    private string _digest = string.Empty;
    private bool _busy;

    /// <param name="save">(내용, digest) 를 저장한다. null 이면 읽기 전용.</param>
    public TextEditTab(Func<Task<(string Text, string Digest)>> load, Func<string, string, Task>? save,
        string hintKey)
    {
        InitializeComponent();
        _load = load;
        _save = save;
        HintText.Text = Loc.T(hintKey);
        Editor.IsReadOnly = save is null;
        SaveButton.Visibility = save is null ? Visibility.Collapsed : Visibility.Visible;
        Loaded += async (_, _) =>
        {
            if (Editor.Text.Length == 0) await ReloadAsync();
        };
    }

    private async void OnReload(object sender, RoutedEventArgs e)
    {
        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        if (_busy) return;

        _busy = true;
        try
        {
            var (text, digest) = await _load();
            Editor.Text = text.Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
            _digest = digest;
            StatusText.Text = string.Empty;
        }
        catch (Exception ex)
        {
            StatusText.Text = Loc.T("MainViewModel_M07", ex.Message);
        }
        finally
        {
            _busy = false;
        }
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        if (_busy || _save is null) return;

        _busy = true;
        try
        {
            // 서버 쪽 파일은 리눅스 줄바꿈
            await _save(Editor.Text.Replace("\r\n", "\n"), _digest);
            StatusText.Text = Loc.T("TextEditTab_Saved");
        }
        catch (Exception ex)
        {
            StatusText.Text = Loc.T("TableTab_ActionFailed", ex.Message);
            return;
        }
        finally
        {
            _busy = false;
        }

        await ReloadAsync(); // 새 digest 를 받아 둔다
        StatusText.Text = Loc.T("TextEditTab_Saved");
    }
}
