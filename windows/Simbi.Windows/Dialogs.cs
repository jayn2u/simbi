using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Simbi.Windows.Core;

namespace Simbi.Windows;

public sealed class TextPrompt : Window
{
    private readonly TextBox input;
    public string Value => input.Text;
    public TextPrompt(string title, string label, string initial)
    {
        Title = title; Width = 440; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = label, Margin = new(0, 0, 0, 12) });
        input = new TextBox { Text = initial, Margin = new(0, 0, 0, 16) }; panel.Children.Add(input);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", IsCancel = true }; var create = new Button { Content = "Create", IsDefault = true };
        create.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(input.Text)) DialogResult = true; };
        buttons.Children.Add(cancel); buttons.Children.Add(create); panel.Children.Add(buttons); Content = panel;
        Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
    }
}

public sealed class SettingsWindow : Window
{
    public SettingsWindow(AppSettings settings)
    {
        Title = "Simbi settings"; Width = 630; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new(26) };
        panel.Children.Add(new TextBlock { Text = "Recording & account", FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new(0, 0, 0, 20) });
        var mic = new CheckBox { Content = "Record microphone by default", IsChecked = settings.Microphone, Margin = new(0, 0, 0, 10) };
        var system = new CheckBox { Content = "Record system audio by default", IsChecked = settings.SystemAudio, Margin = new(0, 0, 0, 16) };
        panel.Children.Add(mic); panel.Children.Add(system);
        panel.Children.Add(new TextBlock { Text = "Microphone", Margin = new(0, 0, 0, 6) });
        var devices = new ComboBox { Margin = new(0, 0, 0, 16), Padding = new(8) };
        devices.Items.Add(new InputDevice("", "Windows default communications device"));
        try { foreach (var device in AudioCapture.Microphones()) devices.Items.Add(device); } catch { }
        devices.SelectedItem = devices.Items.Cast<InputDevice>().FirstOrDefault(d => d.Id == settings.MicrophoneId) ?? devices.Items[0];
        panel.Children.Add(devices);
        var transcribe = new CheckBox { Content = "Enable live transcription through my ChatGPT account", IsChecked = settings.Transcribe, Margin = new(0, 0, 0, 8) };
        panel.Children.Add(transcribe);
        panel.Children.Add(new TextBlock { Text = "When enabled, speech segments are sent directly to ChatGPT using your existing Codex login. Offline segments stay on disk until retried. No Simbi server or separate API key is used.", TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.DimGray, Margin = new(0, 0, 0, 20) });
        panel.Children.Add(new TextBlock { Text = "Codex executable (blank = automatic discovery)", Margin = new(0, 0, 0, 6) });
        var executable = new TextBox { Text = settings.CodexPath, Margin = new(0, 0, 0, 8) }; panel.Children.Add(executable);
        var browse = new Button { Content = "Select codex.exe…", HorizontalAlignment = HorizontalAlignment.Left, Margin = new(0, 0, 0, 16) };
        browse.Click += (_, _) => { var picker = new OpenFileDialog { Filter = "Executable|*.exe", Title = "Select the Codex CLI executable" }; if (picker.ShowDialog(this) == true) executable.Text = picker.FileName; };
        panel.Children.Add(browse);
        panel.Children.Add(new TextBlock { Text = "Windows differences: source labels instead of individual speaker recognition; no embedded terminal or automatic updater. Notes, AI notes, transcripts and original attachments remain ordinary files.", TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.DimGray, Margin = new(0, 0, 0, 20) });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", IsCancel = true }; var save = new Button { Content = "Save", IsDefault = true };
        save.Click += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(executable.Text) && CodexLocator.Find(executable.Text) == null) { MessageBox.Show(this, "Select an existing codex.exe executable.", "Codex path"); return; }
            settings.Microphone = mic.IsChecked == true; settings.SystemAudio = system.IsChecked == true;
            settings.Transcribe = transcribe.IsChecked == true; settings.CodexPath = executable.Text.Trim();
            settings.MicrophoneId = ((InputDevice)devices.SelectedItem).Id; DialogResult = true;
        };
        buttons.Children.Add(cancel); buttons.Children.Add(save); panel.Children.Add(buttons); Content = panel;
    }
}
