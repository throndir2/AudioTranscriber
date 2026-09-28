using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using System.Xml.Linq;
using Xunit;

namespace AudioTranscriber.App.Tests;

// Mirrors the "Speaker for selected lines" combo: editable, text search off, Text bound to the view model.
public sealed class EditableComboBoxTests
{
    private static readonly string[] Speakers = ["Alice", "Speaker 1", "Speaker 2", "Unknown / unassigned"];

    [Fact]
    public void PickingTheSameSpeakerForAnotherLineChangesTheText() => OnSta(() =>
    {
        var line = new LineSpeaker();
        WithCombo(line, combo =>
        {
            Pick(combo, "Alice");
            Assert.Equal("Alice", line.Value);
            line.Value = "Speaker 2"; // selecting another line shows that line's speaker
            Assert.Equal("Speaker 2", combo.Text);
            Pick(combo, "Alice");
            Assert.Equal("Alice", line.Value);
            Assert.Equal("Alice", combo.Text);
        });
    });

    [Fact]
    public void PickingTheSameSpeakerAfterTypingChangesTheText() => OnSta(() =>
    {
        var line = new LineSpeaker();
        WithCombo(line, combo =>
        {
            Pick(combo, "Speaker 1");
            combo.Text = "Bob";
            Pick(combo, "Speaker 1");
            Assert.Equal("Speaker 1", line.Value);
        });
    });

    [Fact]
    public void OpeningTheListKeepsTextAndSelectsItsEntry() => OnSta(() =>
    {
        var line = new LineSpeaker();
        WithCombo(line, combo =>
        {
            Pick(combo, "Alice");
            line.Value = "A new name";
            combo.IsDropDownOpen = true;
            Assert.Equal("A new name", line.Value);
            Assert.Equal(-1, combo.SelectedIndex);
            combo.IsDropDownOpen = false;
            line.Value = "Speaker 2";
            combo.IsDropDownOpen = true;
            Assert.Equal("Speaker 2", combo.SelectedItem);
            Assert.Equal("Speaker 2", line.Value);
            combo.IsDropDownOpen = false;
        });
    });

    [Fact]
    public void EveryEditableComboInTheMainWindowSyncsItsSelection()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(directory.FullName, "AudioTranscriber.slnx"))) directory = directory.Parent!;
        var xaml = XDocument.Load(Path.Combine(directory.FullName, "src", "AudioTranscriber.App", "MainWindow.xaml"));
        XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace local = "clr-namespace:AudioTranscriber.App";
        var editable = xaml.Descendants(wpf + "ComboBox").Where(combo => (string?)combo.Attribute("IsEditable") == "True").ToList();
        Assert.NotEmpty(editable);
        Assert.All(editable, combo => Assert.Equal("True", (string?)combo.Attribute(local + "EditableComboBox.SyncSelection")));
    }

    // What a click on a list entry does: open the list, select the entry, close the list.
    private static void Pick(ComboBox combo, string item)
    {
        combo.IsDropDownOpen = true;
        Flush();
        combo.SelectedItem = item;
        combo.IsDropDownOpen = false;
        Flush();
    }

    private static void WithCombo(LineSpeaker line, Action<ComboBox> test)
    {
        var combo = new ComboBox { IsEditable = true, IsTextSearchEnabled = false, ItemsSource = Speakers };
        EditableComboBox.SetSyncSelection(combo, true);
        combo.SetBinding(ComboBox.TextProperty, new Binding(nameof(LineSpeaker.Value))
            { Source = line, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        var window = new Window
        {
            Content = combo, Width = 240, Height = 120, Left = -10_000, Top = -10_000,
            ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None
        };
        window.Show();
        Flush();
        try { test(combo); }
        finally { window.Close(); }
    }

    private static void Flush() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);

    private static void OnSta(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception error) { failure = ExceptionDispatchInfo.Capture(error); }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }

    private sealed class LineSpeaker : INotifyPropertyChanged
    {
        private string value = "";
        public event PropertyChangedEventHandler? PropertyChanged;

        public string Value
        {
            get => value;
            set
            {
                if (this.value == value) return;
                this.value = value ?? "";
                PropertyChanged?.Invoke(this, new(nameof(Value)));
            }
        }
    }
}
