using System.Xml.Linq;
using Xunit;

namespace AudioTranscriber.App.Tests;

public sealed class EditableComboBoxTests
{
    [Fact]
    public void EveryEditableComboInTheMainWindowSyncsItsSelection()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AudioTranscriber.slnx"))) directory = directory.Parent;
        Assert.NotNull(directory);
        var xaml = XDocument.Load(Path.Combine(directory!.FullName, "src", "AudioTranscriber.App", "MainWindow.axaml"));
        XNamespace av = "https://github.com/avaloniaui";
        XNamespace local = "using:AudioTranscriber.App";
        var editable = xaml.Descendants(av + "ComboBox").Where(combo => (string?)combo.Attribute("IsEditable") == "True").ToList();
        Assert.NotEmpty(editable);
        Assert.All(editable, combo => Assert.Equal("True", (string?)combo.Attribute(local + "EditableComboBox.SyncSelection")));
    }
}
