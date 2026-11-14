using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using OTPSoftware.App.Services;

namespace OTPSoftware.App.ViewModels;

/// <summary>
/// Base class for WPF MVVM ViewModels providing <see cref="INotifyPropertyChanged"/> notifications.
/// </summary>
public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Synchronous <see cref="ICommand"/> implementation supporting both parameterless and parameterized delegates.
/// </summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;
    private EventHandler? _canExecuteChanged;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        ArgumentNullException.ThrowIfNull(execute);
        _execute = _ => execute();
        if (canExecute is not null) _canExecute = _ => canExecute();
    }

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        ArgumentNullException.ThrowIfNull(execute);
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add
        {
            _canExecuteChanged += value;
            CommandManager.RequerySuggested += value;
        }
        remove
        {
            _canExecuteChanged -= value;
            CommandManager.RequerySuggested -= value;
        }
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => _execute(parameter);

    public void RaiseCanExecuteChanged()
    {
        _canExecuteChanged?.Invoke(this, EventArgs.Empty);
        CommandManager.InvalidateRequerySuggested();
    }
}

/// <summary>
/// Asynchronous <see cref="ICommand"/> implementation guarding against concurrent re-execution.
/// </summary>
public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<object?, Task> _execute;
    private readonly Func<object?, bool>? _canExecute;
    private readonly Action<Exception>? _onError;
    private EventHandler? _canExecuteChanged;
    private int _isExecuting;

    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null, Action<Exception>? onError = null)
    {
        ArgumentNullException.ThrowIfNull(execute);
        _execute = _ => execute();
        if (canExecute is not null) _canExecute = _ => canExecute();
        _onError = onError;
    }

    public AsyncRelayCommand(Func<object?, Task> execute, Func<object?, bool>? canExecute = null, Action<Exception>? onError = null)
    {
        ArgumentNullException.ThrowIfNull(execute);
        _execute = execute;
        _canExecute = canExecute;
        _onError = onError;
    }

    public bool IsExecuting => Volatile.Read(ref _isExecuting) != 0;

    public event EventHandler? CanExecuteChanged
    {
        add
        {
            _canExecuteChanged += value;
            CommandManager.RequerySuggested += value;
        }
        remove
        {
            _canExecuteChanged -= value;
            CommandManager.RequerySuggested -= value;
        }
    }

    public bool CanExecute(object? parameter) =>
        Volatile.Read(ref _isExecuting) == 0 && (_canExecute?.Invoke(parameter) ?? true);

    public async void Execute(object? parameter)
    {
        try
        {
            await ExecuteAsync(parameter);
        }
        catch (Exception ex)
        {
            if (_onError is not null)
            {
                try { _onError(ex); } catch (Exception logEx) { System.Diagnostics.Trace.TraceError($"AsyncRelayCommand _onError failure: {logEx}"); }
            }
            else
            {
                System.Diagnostics.Trace.TraceError($"Unhandled AsyncRelayCommand exception: {ex}");
                try { ErrorLoggerService.Default.LogException(ex, "AsyncRelayCommand"); } catch { }
            }
        }
    }

    public async Task ExecuteAsync(object? parameter = null)
    {
        if (!(_canExecute?.Invoke(parameter) ?? true)) return;
        if (Interlocked.CompareExchange(ref _isExecuting, 1, 0) != 0) return;

        RaiseCanExecuteChanged();
        try
        {
            await _execute(parameter);
        }
        catch (Exception ex) when (_onError is not null)
        {
            try { _onError(ex); } catch (Exception logEx) { System.Diagnostics.Trace.TraceError($"AsyncRelayCommand _onError failure: {logEx}"); }
        }
        finally
        {
            Volatile.Write(ref _isExecuting, 0);
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged()
    {
        _canExecuteChanged?.Invoke(this, EventArgs.Empty);
        CommandManager.InvalidateRequerySuggested();
    }
}
