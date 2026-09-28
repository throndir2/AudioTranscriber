using System.Windows;
using System.Windows.Controls;

namespace AudioTranscriber.App;

/// <summary>
/// With text search off, an editable ComboBox never updates its selection when its Text changes, so the last picked
/// entry stays selected and picking it again does nothing. SyncSelection re-matches the selection to the text each
/// time the list opens, so every pick changes the text.
/// </summary>
public static class EditableComboBox
{
    public static readonly DependencyProperty SyncSelectionProperty = DependencyProperty.RegisterAttached(
        "SyncSelection", typeof(bool), typeof(EditableComboBox), new PropertyMetadata(false, OnSyncSelectionChanged));

    public static bool GetSyncSelection(DependencyObject element) => (bool)element.GetValue(SyncSelectionProperty);
    public static void SetSyncSelection(DependencyObject element, bool value) => element.SetValue(SyncSelectionProperty, value);

    private static void OnSyncSelectionChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not ComboBox combo) return;
        combo.DropDownOpened -= OnDropDownOpened;
        if ((bool)e.NewValue) combo.DropDownOpened += OnDropDownOpened;
    }

    private static void OnDropDownOpened(object? sender, EventArgs e) => SyncSelectionToText((ComboBox)sender!);

    /// <summary>Selects the entry whose text equals the combo's text, or nothing, keeping the text unchanged.</summary>
    public static void SyncSelectionToText(ComboBox combo)
    {
        var text = combo.Text ?? "";
        var match = -1;
        for (var i = 0; i < combo.Items.Count; i++)
            if (string.Equals(combo.Items[i]?.ToString(), text, StringComparison.CurrentCulture)) { match = i; break; }
        if (combo.SelectedIndex == match) return;
        combo.SelectedIndex = match;
        // Clearing the selection also clears an editable combo's text; put it back.
        if (combo.Text != text) combo.Text = text;
    }
}
