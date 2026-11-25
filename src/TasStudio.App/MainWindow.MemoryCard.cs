using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using TasStudio.Emulation;

namespace TasStudio.App;

public sealed partial class MainWindow
{
    internal async Task ManageProjectMemoryCard()
    {
        await FlushInputEditsAsync();
        await PausePlayback();
        if (_projectPath == null) { await SaveProjectAs(); if (_projectPath == null) return; }
        var folder = await _execution.PrepareProjectMemoryCardAsync(_projectPath);
        var live = (await _execution.GetConfigurationAsync())!.Options.Configuration?.UseGciFolder == true;
        var dialog = new Window { Title = "Project memory card · Slot A", Width = 620, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = StudioTheme.Brush(ThemeColor.Panel) };
        var open = new Button { Content = "Open GCI folder", Name = "OpenProjectGciFolder" };
        open.Click += (_, _) => Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        var restart = new Button { Content = "Restart from GCI folder", Name = "RestartProjectGciFolder" };
        var close = new Button { Content = "Close" }; close.Click += (_, _) => dialog.Close();
        var message = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var mode = new TextBlock { Text = live ? "Slot A uses this project's live GCI folder. In-game saves are written here." :
            "This project uses a raw card. Restart from the GCI folder to switch Slot A to a live project folder.", TextWrapping = TextWrapping.Wrap };
        var working = false;
        dialog.Closing += (_, e) => e.Cancel = working;
        restart.Click += async (_, _) =>
        {
            working = true; open.IsEnabled = restart.IsEnabled = close.IsEnabled = false;
            try
            {
                await WithGameLoading(() => _execution.RestartFromProjectMemoryCardAsync(_projectPath), dialog);
                await _execution.SaveProjectAsync(_projectPath);
                _savedRevision = _execution.Revision; _dirty = false;
                _timeline.SetSelection(0, 1); _timeline.FirstFrame = 0; LoadSelectedInput();
                await SaveRecoveryNow();
                mode.Text = "Slot A uses this project's live GCI folder. In-game saves are written here.";
                message.Text = "Restarted with the project's GCI saves. The new starting state is saved; the game is paused.";
            }
            catch (Exception ex) { message.Text = ex.Message; }
            finally { working = false; open.IsEnabled = restart.IsEnabled = close.IsEnabled = true; }
        };
        dialog.Content = new StackPanel { Margin = new Thickness(22), Spacing = 14, Children =
        {
            new TextBlock { Text = "Project memory card", FontSize = 22 },
            mode,
            new TextBox { Text = folder, IsReadOnly = true, TextWrapping = TextWrapping.Wrap },
            new TextBlock { Text = "Copy .gci saves directly into this folder, then restart below to load them. The folder is read at game start; changes are not loaded while the game is running.", TextWrapping = TextWrapping.Wrap },
            new TextBlock { Text = "Restart keeps your inputs and creates a new starting state. Existing state markers are removed. Seeking or loading a state also rewinds the card. Experiment workers use private copies.", TextWrapping = TextWrapping.Wrap },
            Row(open, restart), message, close
        } };
        _dialogOpen = true;
        try { await dialog.ShowDialog(this); }
        finally { _dialogOpen = false; }
    }
}
