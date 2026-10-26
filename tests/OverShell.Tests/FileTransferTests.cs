using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OverShell.App.Input;
using OverShell.Core.Input;
using Xunit;

namespace OverShell.Tests;

public class FileTransferTests
{
    [Fact]
    public void Explorer_files_preserve_every_type_order_and_directory() => WithStore(store =>
    {
        string[] paths = [@"C:\a b\photo.jpg", @"C:\report.pdf", @"C:\movie.mp4", @"C:\song.flac", @"C:\archive.zip", @"C:\unknown.xyz", @"C:\folder"];
        var data = new DataObject(DataFormats.FileDrop, paths);
        data.SetText("a misleading preview");
        var content = TerminalClipboard.Read(data, store);
        Assert.Equal(paths, content.Files);
        paths[0] = "changed after capture";
        Assert.Equal(@"C:\a b\photo.jpg", content.Files[0]);
        Assert.False(content.ApplicationPaste);
        Assert.Empty(content.Text);
        Assert.False(Directory.Exists(store.Root));
    });

    [Theory]
    [InlineData("audio/mpeg", ".mp3")]
    [InlineData("WaveAudio", ".wav")]
    [InlineData("video/mp4", ".mp4")]
    [InlineData("application/pdf", ".pdf")]
    [InlineData("image/avif", ".avif")]
    [InlineData("application/x-custom-content", ".bin")]
    public void Clipboard_binary_is_preserved_byte_for_byte(string format, string extension) => WithStore(store =>
    {
        byte[] bytes = [0, 255, 1, 128, 0, 13, 10, 27, 99];
        using var stream = new MemoryStream(bytes);
        stream.Position = stream.Length;
        var data = new DataObject(format, stream, autoConvert: false);
        var content = TerminalClipboard.Read(data, store);
        var path = Assert.Single(content.Files);
        Assert.Equal(extension, Path.GetExtension(path));
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(stream.Length, stream.Position);
        Assert.True(stream.CanRead);
        Assert.StartsWith(store.Root, path);
    });

    [Fact]
    public void Bitmap_can_be_delivered_as_a_real_png_without_application_support() => WithStore(store =>
    {
        var data = new DataObject();
        data.SetImage(BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 255, 255, 0, 255, 0, 255 }, 8));
        var content = TerminalClipboard.Read(data, store, asFiles: true);
        var path = Assert.Single(content.Files);
        using var stream = File.OpenRead(path);
        var decoded = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        Assert.Equal(2, decoded.Frames[0].PixelWidth);
        Assert.False(content.ApplicationPaste);
    });

    [Fact]
    public void Rich_text_defaults_to_plain_text_and_can_be_saved_in_its_original_format() => WithStore(store =>
    {
        const string rtf = @"{\rtf1\ansi bold \b text\b0}";
        var data = new DataObject(DataFormats.Rtf, rtf);
        data.SetText("bold text");
        Assert.Equal("bold text", TerminalClipboard.Read(data, store).Text);
        var path = Assert.Single(TerminalClipboard.Read(data, store, asFiles: true).Files);
        Assert.EndsWith(".rtf", path);
        Assert.Equal(rtf, File.ReadAllText(path));
    });

    [Fact]
    public void Drop_materializes_images_but_keeps_plain_text_as_text() => WithStore(store =>
    {
        var data = new DataObject();
        data.SetText("dropped text");
        Assert.Equal("dropped text", TerminalClipboard.Read(data, store, materializeImage: true).Text);
    });

    [Fact]
    public void Failed_export_leaves_no_partial_attachment() => WithStore(store =>
    {
        Assert.Throws<IOException>(() => store.Save("movie.mp4", stream => { stream.WriteByte(1); throw new IOException("source failed"); }));
        Assert.Empty(Directory.EnumerateFileSystemEntries(store.Root));
    });

    [Fact]
    public void Provider_filenames_cannot_escape_the_store_or_create_devices() => WithStore(store =>
    {
        var first = store.SaveText(@"..\..\NUL", "first");
        var second = store.SaveText(@"..\..\NUL", "second");
        Assert.NotEqual(first, second);
        Assert.StartsWith(store.Root + Path.DirectorySeparatorChar, first);
        Assert.Equal("clipboard-NUL", Path.GetFileName(first));
        Assert.Equal("first", File.ReadAllText(first));
        Assert.Equal("second", File.ReadAllText(second));
    });

    [Theory]
    [InlineData("pwsh.exe", false, FilePasteStyle.PowerShell)]
    [InlineData("C:\\Program Files\\PowerShell\\7\\pwsh.exe -NoLogo", false, FilePasteStyle.PowerShell)]
    [InlineData("\"C:\\Program Files\\PowerShell\\7\\pwsh.exe\" -NoLogo", false, FilePasteStyle.PowerShell)]
    [InlineData("bash.exe", false, FilePasteStyle.Posix)]
    [InlineData("cmd.exe", false, FilePasteStyle.Windows)]
    [InlineData("pwsh.exe", true, FilePasteStyle.Windows)]
    public void Target_quoting_uses_the_program_not_its_arguments(string command, bool agent, FilePasteStyle expected) =>
        Assert.Equal(expected, FilePaste.StyleFor(command, agent));

    [Fact]
    public void Nested_shell_takes_precedence_over_the_profile() =>
        Assert.Equal(FilePasteStyle.Windows, FilePaste.StyleFor("pwsh.exe", false, ["pwsh.exe", "cmd.exe"]));

    [Fact]
    public void PowerShell_receives_special_paths_as_literal_arguments()
    {
        string[] paths = [@"C:\a b\it's $HOME `; & [x].pdf", @"C:\日本語\🚀.mp4", @"C:\"];
        var source = "& { $args | ForEach-Object { [Console]::WriteLine([Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($_))) } } " + FilePaste.Format(paths, FilePasteStyle.PowerShell);
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(source)) }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(15000), "PowerShell did not exit");
        Assert.True(process.ExitCode == 0, error);
        Assert.Equal(paths, output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(line => Encoding.UTF8.GetString(Convert.FromBase64String(line))));
    }

    [Fact]
    public async Task Clipboard_contention_is_retried_and_eventually_reported()
    {
        var attempts = 0;
        Assert.Equal("payload", await ClipboardAccess.RetryAsync(() => ++attempts < 3 ? throw new ExternalException("busy") : "payload"));
        Assert.Equal(3, attempts);
        attempts = 0;
        await Assert.ThrowsAsync<ExternalException>(() => ClipboardAccess.RetryAsync<string>(() => { attempts++; throw new ExternalException("busy"); }));
        Assert.Equal(4, attempts);
    }

    [Fact]
    public async Task Failed_provider_does_not_poison_later_clipboard_operations()
    {
        var queue = new ClipboardWorkQueue();
        var order = new List<string>();
        var failures = new List<Exception>();
        queue.Enqueue(async () => { await Task.Delay(20); order.Add("first"); throw new FormatException("bad provider"); }, failures.Add);
        queue.Enqueue(() => { order.Add("second"); return Task.CompletedTask; }, failures.Add);
        await queue.Completion;
        Assert.Equal(["first", "second"], order);
        Assert.IsType<FormatException>(Assert.Single(failures));
    }

    private static void WithStore(Action<TransferFileStore> action) => Sta.Run(() =>
    {
        var root = Path.Combine(Path.GetTempPath(), "overshell-transfer-" + Guid.NewGuid().ToString("N"));
        try { action(new TransferFileStore(root)); }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    });
}
