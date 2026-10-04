using OverShell.Core.Integrations;
using Xunit;

namespace OverShell.Tests;

public class ShellLaunchTests
{
    private const string Script = @"C:\Users\me\AppData\Local\OverShell\shell\overshell-prompt.ps1";

    [Theory]
    [InlineData("pwsh.exe")]
    [InlineData("pwsh")]
    [InlineData("powershell.exe -NoLogo")]
    [InlineData(@"C:\Program Files\PowerShell\7\pwsh.exe -NoLogo -NoProfile")]
    [InlineData(@"""C:\Program Files\PowerShell\7\pwsh.exe"" -nologo")]
    [InlineData(@"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe")]
    [InlineData("pwsh -ExecutionPolicy Bypass -WorkingDirectory C:\\src -NoLogo")]
    [InlineData("pwsh -wd \"C:\\my src\" -ep RemoteSigned")]
    [InlineData("pwsh -Login -NoLogo")]
    [InlineData("powershell.exe -ExecutionPolicy RemoteSigned -WindowStyle Hidden -InputFormat Text -OutputFormat Text -Sta")]
    [InlineData("pwsh -inp Text -o XML -w Normal -psc C:\\x.psc1 -settings C:\\s.json -config Default -custom pipe")]
    public void Plain_powershell_launches_get_the_script(string commandLine)
    {
        var plan = ShellLaunch.Inject(commandLine, Script);

        Assert.True(plan.Injected, plan.Reason);
        Assert.Null(plan.Reason);
        Assert.StartsWith(commandLine, plan.CommandLine, StringComparison.Ordinal);
        Assert.EndsWith($" -NoExit -Command \"try {{ . '{Script}' }} catch {{ }}\"", plan.CommandLine, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("pwsh -NoExit -NoLogo")]
    [InlineData("pwsh -noe -nol -nop")]
    public void An_existing_NoExit_is_not_doubled(string commandLine)
    {
        var plan = ShellLaunch.Inject(commandLine, Script);

        Assert.True(plan.Injected);
        Assert.Equal(1, CountOf(plan.CommandLine, "-NoExit") + CountOf(plan.CommandLine, "-noe "));
        Assert.EndsWith(" -Command \"try { . '" + Script + "' } catch { }\"", plan.CommandLine, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("pwsh -Command Get-Date")]
    [InlineData("pwsh -c Get-Date")]
    [InlineData("pwsh -com \"& { 1 }\"")]
    [InlineData("pwsh -NoExit -Command \"opencode\"")]
    [InlineData("pwsh -File C:\\x.ps1")]
    [InlineData("pwsh -f C:\\x.ps1")]
    [InlineData("powershell.exe -EncodedCommand ZQBjAGgAbwA=")]
    [InlineData("pwsh -e ZQBjAGgAbwA=")]
    [InlineData("pwsh -ec ZQBjAGgAbwA=")]
    [InlineData("pwsh C:\\scripts\\start.ps1")]
    [InlineData("pwsh -NoLogo -- C:\\scripts\\start.ps1")]
    [InlineData("pwsh -")]
    [InlineData("pwsh -co X")]
    [InlineData("pwsh -ea ZQBjAGgAbwA=")]
    [InlineData("pwsh -encodeda ZQBjAGgAbwA=")]
    [InlineData("powershell.exe -Version 2")]
    [InlineData("C:\\Program Files\\PowerShell\\7\\pwsh.exe -File C:\\My Scripts\\run.ps1")]
    public void A_launch_that_runs_something_is_left_alone(string commandLine)
    {
        var plan = ShellLaunch.Inject(commandLine, Script);

        Assert.False(plan.Injected);
        Assert.Equal(commandLine, plan.CommandLine);
        Assert.Equal("the profile runs a command", plan.Reason);
    }

    [Theory]
    [InlineData("cmd.exe", "cmd")]
    [InlineData(@"C:\Program Files\Git\bin\bash.exe -i -l", "bash")]
    [InlineData("wsl.exe -d Ubuntu", "wsl")]
    [InlineData(@"""C:\Users\me\opencode.exe"" --continue", "opencode")]
    [InlineData("nu.exe", "nu")]
    [InlineData(@"C:\Program Files\Nushell\bin\nu.exe --login", "nu")]
    public void Other_programs_are_left_alone(string commandLine, string program)
    {
        var plan = ShellLaunch.Inject(commandLine, Script);

        Assert.False(plan.Injected);
        Assert.Equal(commandLine, plan.CommandLine);
        Assert.Equal($"{program} is not PowerShell", plan.Reason);
    }

    [Fact]
    public void An_apostrophe_in_the_script_path_is_doubled_for_the_single_quoted_string()
    {
        var plan = ShellLaunch.Inject("pwsh", @"C:\Users\O'Brien\AppData\Local\OverShell\shell\overshell-prompt.ps1");

        Assert.True(plan.Injected);
        Assert.Contains(@". 'C:\Users\O''Brien\AppData\Local\OverShell\shell\overshell-prompt.ps1'", plan.CommandLine, StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_command_line_is_reported()
    {
        var plan = ShellLaunch.Inject("   ", Script);

        Assert.False(plan.Injected);
        Assert.Equal("empty command line", plan.Reason);
    }

    [Theory]
    [InlineData("pwsh.exe", "pwsh")]
    [InlineData(@"""C:\Program Files\PowerShell\7\pwsh.exe""", "pwsh")]
    [InlineData(@"C:\Windows\System32\WindowsPowerShell\v1.0\PowerShell.EXE", "powershell")]
    [InlineData("C:/tools/pwsh", "pwsh")]
    public void Program_name_strips_directory_quotes_and_extension(string token, string expected) =>
        Assert.Equal(expected, ShellLaunch.ProgramName(token));

    [Fact]
    public void Tokenizer_keeps_quoted_groups_together()
    {
        var tokens = ShellLaunch.Tokenize(@"""C:\Program Files\PowerShell\7\pwsh.exe"" -wd ""C:\my src""   -NoLogo");

        Assert.Equal([@"""C:\Program Files\PowerShell\7\pwsh.exe""", "-wd", @"""C:\my src""", "-NoLogo"], tokens);
    }

    private static int CountOf(string text, string needle)
    {
        var count = 0;
        for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
