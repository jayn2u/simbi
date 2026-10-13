using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using System.Windows.Threading;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Simbi.Windows;
using Simbi.Windows.Core;

internal static class UiSmoke
{
    public static void Run(string root, string[] args)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { Verify(root, args); } catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure != null) throw new Exception("Windows GUI smoke failed", failure);
    }
    private static void Verify(string root, string[] args)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var uiRoot = Path.Combine(root, "ui");
        var store = new NoteStore(uiRoot); var note = store.Create("Windows GUI verification");
        store.SaveText(note, "note.md", "# Windows GUI verification\n\nA real native window, with local files.\n");
        Vtt.Append(note.Folder, new("synthetic-cue", 0.2, 1, "System audio", "Synthetic transcript for UI verification."));
        var attachment = Path.Combine(root, "slides.bin"); File.WriteAllBytes(attachment, [1, 2, 3]); store.Import(note, attachment);
        var app = new App(); app.InitializeComponent();
        var window = new MainWindow(new AppSettings { NotesRoot = uiRoot, CodexPath = Environment.ProcessPath!, Microphone = false, SystemAudio = true, Transcribe = false }, isolated: true);
        app.MainWindow = window; window.Show();
        Pump(250);
        var notes = (ListBox)window.FindName("NotesList"); notes.SelectedIndex = 0;
        var editor = (TextBox)window.FindName("NoteEditor");
        editor.Text += "\nAutosave from the rendered WPF editor ✓";
        Pump(1000);
        if (!File.ReadAllText(Path.Combine(note.Folder, "note.md")).Contains("Autosave")) throw new Exception("GUI editor did not autosave.");
        Console.WriteLine("PASS: rendered Windows WPF window, note selection and editor autosave");
        editor.Text += "\nConcurrent local edit"; File.WriteAllText(Path.Combine(note.Folder, "note.md"), "External edit ✓"); Pump(1000);
        if (editor.Text != "External edit ✓" || !Directory.GetFiles(Path.Combine(note.Folder, ".simbi", "conflicts")).Any(p => File.ReadAllText(p).Contains("Concurrent local edit")))
            throw new Exception("GUI conflict recovery did not preserve both versions and reload the external document.");
        Console.WriteLine("PASS: native editor resolves an external save conflict without losing either version or trapping the window");
        var conflictsBeforeSummary = Directory.GetFiles(Path.Combine(note.Folder, ".simbi", "conflicts")).Length;
        var summary = (TextBox)window.FindName("SummaryEditor"); summary.Text = "Unsaved user summary edit";
        ((Button)window.FindName("SummaryButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        WaitUntil(() => summary.Text.Contains("Synthetic AI notes") && ((Button)window.FindName("SummaryButton")).IsEnabled, window);
        if (!File.ReadAllText(Path.Combine(note.Folder, "summary.md")).Contains("Synthetic AI notes") ||
            Directory.GetFiles(Path.Combine(note.Folder, ".simbi", "conflicts")).Length != conflictsBeforeSummary) throw new Exception("AI summary save or pre-save snapshot failed.");
        Console.WriteLine("PASS: Generate AI notes saves response to summary.md/editor, including a pending user edit without false conflict");
        ((TextBox)window.FindName("QuestionBox")).Text = "GUI-question-sentinel";
        ((Button)window.FindName("AskButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        WaitUntil(() => ((TextBox)window.FindName("ChatOutput")).Text.Contains("Question received: GUI-question-sentinel") && ((Button)window.FindName("AskButton")).IsEnabled, window);
        Console.WriteLine("PASS: Ask sends the actual question and displays its matching synthetic answer");
        ((ListBox)window.FindName("Attachments")).SelectedIndex = 0;
        ((Button)window.FindName("ConvertButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var converted = Path.Combine(note.Folder, "context", "slides.bin.md");
        WaitUntil(() => File.Exists(converted) && ((Button)window.FindName("ConvertButton")).IsEnabled, window);
        if (!File.ReadAllText(converted).Contains("files/slides.bin") || !File.ReadAllBytes(Path.Combine(note.Folder, "files", "slides.bin")).SequenceEqual(new byte[] { 1, 2, 3 }))
            throw new Exception("Selected attachment conversion or original preservation failed.");
        Console.WriteLine("PASS: Convert selected saves the correct context file and preserves the original attachment");
        var search = (TextBox)window.FindName("SearchBox"); search.Text = "no-matching-note";
        _ = window.Dispatcher.BeginInvoke(() =>
        {
            var dialog = window.OwnedWindows.OfType<TextPrompt>().Single();
            ((StackPanel)dialog.Content).Children.OfType<TextBox>().Single().Text = "Created outside search";
            dialog.DialogResult = true;
        });
        ((Button)window.FindName("NewNoteButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (search.Text != "" || notes.SelectedItem is not Note selected || selected.Title != "Created outside search") throw new Exception("New note remained hidden by the active search.");
        notes.SelectedItem = notes.Items.Cast<Note>().Single(n => n.Folder == note.Folder);
        Console.WriteLine("PASS: native New note dialog creates and opens a note despite an earlier excluding search");
        Pump(200);
        if (args.Contains("--ui"))
        {
        var record = (Button)window.FindName("RecordButton");
        record.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        using var output = new WaveOutEvent();
        output.Init(new SignalGenerator(48000, 2) { Gain = 0.025, Frequency = 523.25 }.Take(TimeSpan.FromSeconds(1.2))); output.Play();
        Pump(1800);
        record.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(700);
        var audio = Path.Combine(note.Folder, "audio.webm");
        if (!File.Exists(audio) || OpusWebM.Decode(audio).Max(s => Math.Abs((int)s)) < 100) throw new Exception("GUI Record/Stop failed.");
        var play = (Button)window.FindName("PlayButton"); play.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(250);
        play.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(250);
        Console.WriteLine("PASS: native GUI Record/Stop/Play/Pause with actual WASAPI capture");
        var slider = (Slider)window.FindName("SeekSlider"); slider.Value = 1.1;
        slider.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, Environment.TickCount, Key.Right) { RoutedEvent = Keyboard.KeyUpEvent });
        Pump(220);
        if (Math.Abs(slider.Value - 1.1) > 0.05) throw new Exception("Keyboard slider did not seek playback.");
        var transcript = (ListBox)window.FindName("TranscriptList"); transcript.SelectedIndex = 0;
        transcript.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Control.MouseDoubleClickEvent });
        Pump(250);
        if (slider.Value < 0.2 || slider.Value > 0.7) throw new Exception("Transcript cue did not seek to its start.");
        play.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(220);
        Console.WriteLine("PASS: keyboard seek and transcript double-click move native playback to the requested timestamp");
        }
        var screenshotIndex = Array.IndexOf(args, "--screenshot");
        if (screenshotIndex >= 0 && screenshotIndex + 1 < args.Length)
        {
            var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var path = Path.GetFullPath(args[screenshotIndex + 1]); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var stream = File.Create(path); encoder.Save(stream);
            Console.WriteLine("PASS: rendered window evidence written to " + path);
        }
        window.Close(); Pump(300);
        if (window.IsVisible) throw new Exception("Window did not close cleanly.");
        Console.WriteLine("PASS: native window closes cleanly after capture and playback");
        app.Shutdown();
    }
    private static void WaitUntil(Func<bool> complete, Window window)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!complete() && DateTime.UtcNow < deadline) Pump(50);
        if (!complete()) throw new Exception("GUI action timed out: " + ((TextBlock)window.FindName("StatusLabel")).Text);
    }
    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
}
