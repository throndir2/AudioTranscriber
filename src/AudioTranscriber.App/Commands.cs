using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace AudioTranscriber.App;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Changed(name);
        return true;
    }

    protected void Changed([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

internal static class CommandManager
{
    public static event EventHandler? RequerySuggested;
    public static void InvalidateRequerySuggested() => RequerySuggested?.Invoke(null, EventArgs.Empty);
}

public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;
    public void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        try { execute(); }
        catch { CommandErrors.Show(); }
    }
}

public sealed class AsyncCommand(Func<Task> execute, Func<bool>? canExecute = null) : ICommand
{
    private bool running;
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => !running && (canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        running = true;
        CommandManager.InvalidateRequerySuggested();
        try { await execute(); }
        catch { CommandErrors.Show(); }
        finally { running = false; CommandManager.InvalidateRequerySuggested(); }
    }
}

internal static class CommandErrors
{
    public static void Show() => _ = DesktopDialogs.ShowMessageAsync("AudioTranscriber",
        "The action could not complete. Check your selected session, files, and device configuration. Credentials and remote error bodies are not displayed.",
        NativeDialogIcon.Warning);
}
