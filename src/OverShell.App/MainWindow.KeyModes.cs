using System.Windows;
using System.Windows.Input;
using OverShell.Core.Input;

namespace OverShell.App;

public partial class MainWindow
{
    private KeyMode? _keyMode;
    internal KeyMode? CurrentKeyMode => _keyMode;

    internal KeyMode EnterKeyMode(IEnumerable<Keybinding> bindings, Action? closed, TimeSpan? timeout)
    {
        var mode = new KeyMode(bindings, timeout ?? TimeSpan.FromMilliseconds(Math.Max(300, _settings.Keys.SequenceTimeoutMs)), DateTimeOffset.Now);
        CancelKeyMode();
        _shell.RaiseModeEntered();
        _sequences.Cancel();
        _shell.RaisePendingChanged([]);
        _keyMode = mode;
        var owner = CommandOwner();
        void Cancel(object? sender, EventArgs e) => mode.Dispose();
        void Click(object sender, MouseButtonEventArgs e) => mode.Dispose();
        void FocusChanged(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (e.NewFocus is System.Windows.Controls.Primitives.TextBoxBase) mode.Dispose();
        }
        owner.Deactivated += Cancel;
        owner.Closed += Cancel;
        owner.PreviewMouseDown += Click;
        owner.GotKeyboardFocus += FocusChanged;
        mode.Closed += () =>
        {
            owner.Deactivated -= Cancel;
            owner.Closed -= Cancel;
            owner.PreviewMouseDown -= Click;
            owner.GotKeyboardFocus -= FocusChanged;
            if (ReferenceEquals(_keyMode, mode)) _keyMode = null;
            closed?.Invoke();
        };
        return mode;
    }

    internal void CancelKeyMode() => _keyMode?.Dispose();
    internal void RefreshExtensionKeys() => LoadKeybindings();
}
