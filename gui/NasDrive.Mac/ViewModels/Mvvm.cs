using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using NasDrive.Mac.Views;
using NasDrive.Services;

namespace NasDrive.ViewModels;

/// <summary>Base class implementing INotifyPropertyChanged.</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>Refreshes every binding of this object.</summary>
    protected void RaiseAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

/// <summary>
/// Commands re-evaluate CanExecute when <see cref="InvalidateAll"/> is called
/// (Avalonia has no equivalent of WPF's CommandManager.RequerySuggested).
/// </summary>
public abstract class CommandBase : ICommand
{
    private static event EventHandler? RequerySuggested;

    public static void InvalidateAll()
    {
        if (Dispatcher.UIThread.CheckAccess()) RequerySuggested?.Invoke(null, EventArgs.Empty);
        else Dispatcher.UIThread.Post(() => RequerySuggested?.Invoke(null, EventArgs.Empty));
    }

    protected CommandBase() => RequerySuggested += (_, _) => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    public event EventHandler? CanExecuteChanged;
    public abstract bool CanExecute(object? parameter);
    public abstract void Execute(object? parameter);
}

/// <summary>ICommand backed by delegates.</summary>
public sealed class RelayCommand : CommandBase
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute == null ? null : _ => canExecute()) { }

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public override bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
    public override void Execute(object? parameter) => _execute(parameter);
}

/// <summary>Async command that disables itself while running and reports errors.</summary>
public sealed class AsyncCommand : CommandBase
{
    private readonly Func<Task> _execute;
    private readonly Func<bool>? _canExecute;
    private bool _running;

    public AsyncCommand(Func<Task> execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public override bool CanExecute(object? parameter) => !_running && (_canExecute?.Invoke() ?? true);

    public override async void Execute(object? parameter)
    {
        _running = true;
        InvalidateAll();
        try
        {
            await _execute();
        }
        catch (Exception ex)
        {
            Log.Error("Command failed", ex);
            await Dialogs.ErrorAsync(ex.Message);
        }
        finally
        {
            _running = false;
            InvalidateAll();
        }
    }
}

/// <summary>Pages of the main window.</summary>
public enum Page { List, Editor, Settings }
