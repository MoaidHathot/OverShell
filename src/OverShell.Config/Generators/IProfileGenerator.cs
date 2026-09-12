namespace OverShell.Config.Generators;

public interface IProfileGenerator
{
    string Source { get; }

    IReadOnlyList<TerminalProfile> Generate(IList<string>? diagnostics = null);
}
