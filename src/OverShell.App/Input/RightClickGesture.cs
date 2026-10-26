namespace OverShell.App.Input;

/// <summary>Owns a right-button gesture from press to release, never running an action twice.</summary>
internal sealed class RightClickGesture
{
    private nint _owner;

    public bool Press(nint hwnd, Func<bool> take)
    {
        _owner = 0;
        if (hwnd == 0 || !take())
        {
            return false;
        }

        _owner = hwnd;
        return true;
    }

    public bool Release(nint hwnd)
    {
        var taken = hwnd != 0 && _owner == hwnd;
        _owner = 0;
        return taken;
    }

    public void Cancel() => _owner = 0;
}
