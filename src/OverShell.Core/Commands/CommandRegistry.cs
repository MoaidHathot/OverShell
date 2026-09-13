namespace OverShell.Core.Commands;

/// <summary>
/// One thing the user can ask OverShell to do, by name. Everything the chrome offers —
/// menu items, keybindings, palette entries — resolves to one of these, so a command is
/// bindable and searchable the moment it exists.
/// </summary>
/// <param name="Id">Stable dotted identifier, e.g. <c>tab.new</c>. Keybindings and skins refer to it.</param>
/// <param name="Title">Human title for the palette.</param>
/// <param name="Category">Palette grouping: Tabs, View, Clipboard, Agents, …</param>
/// <param name="Execute">Runs the command. Returns false when it declined (e.g. copy with no selection), so a chord can fall through to the shell.</param>
/// <param name="CanExecute">Null means always.</param>
/// <param name="Description">Optional second line in the palette.</param>
public sealed record CommandDescriptor(
    string Id,
    string Title,
    string Category,
    Func<bool> Execute,
    Func<bool>? CanExecute = null,
    string? Description = null)
{
    public bool IsEnabled => CanExecute?.Invoke() ?? true;
}

/// <summary>The set of commands the running application knows. Registration is additive; ids are unique.</summary>
public sealed class CommandRegistry
{
    private readonly Dictionary<string, CommandDescriptor> _commands = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<CommandDescriptor> All => _commands.Values;

    public void Register(CommandDescriptor command)
    {
        if (_commands.ContainsKey(command.Id))
        {
            throw new InvalidOperationException($"Command '{command.Id}' is already registered.");
        }

        _commands[command.Id] = command;
    }

    public void Register(string id, string title, string category, Action execute, Func<bool>? canExecute = null, string? description = null) =>
        Register(new CommandDescriptor(id, title, category, () => { execute(); return true; }, canExecute, description));

    public void Register(string id, string title, string category, Func<bool> execute, Func<bool>? canExecute = null, string? description = null) =>
        Register(new CommandDescriptor(id, title, category, execute, canExecute, description));

    public CommandDescriptor? Find(string id) => _commands.GetValueOrDefault(id);

    /// <summary>Removes a command; false when there was none. For commands that come from files that reload (snippets).</summary>
    public bool Remove(string id) => _commands.Remove(id);

    /// <summary>Runs a command by id. False when unknown, disabled, or the command declined.</summary>
    public bool TryExecute(string id)
    {
        var command = Find(id);
        return command is not null && command.IsEnabled && command.Execute();
    }
}
