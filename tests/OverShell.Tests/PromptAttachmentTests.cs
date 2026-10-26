using System.Windows.Controls;
using System.Windows.Input;
using OverShell.App.Chrome;
using OverShell.App.Input;
using OverShell.Core.Input;
using Xunit;

namespace OverShell.Tests;

public class PromptAttachmentTests
{
    [Fact]
    public void Successful_send_and_history_preserve_attachments_and_the_unsent_draft() => Sta.WpfRun(async () =>
    {
        var bar = new PromptBar { Text = "inspect" };
        bar.AttachFiles([@"C:\one.pdf", @"C:\two.mp4", @"C:\one.pdf"]);
        PromptDraft? received = null;
        bar.SendRequested = (draft, _) => { received = draft; return true; };
        await bar.SendAsync();
        Assert.Equal(2, received!.Files.Count);
        Assert.Empty(bar.Text);
        Assert.Empty(bar.Attachments);
        Assert.Equal(received.Files, Assert.Single(bar.HistoryEntries).Files);

        bar.Text = "unsent";
        bar.AttachFiles([@"C:\audio.wav"]);
        bar.RecallHistory(previous: true);
        Assert.Equal("inspect", bar.Text);
        Assert.Equal(2, bar.Attachments.Count);
        Assert.True(bar.RemoveAttachment(bar.Attachments[0]));
        Assert.Equal(2, bar.HistoryEntries[0].Files.Count);
        bar.RecallHistory(previous: false);
        Assert.Equal("unsent", bar.Text);
        Assert.Equal(@"C:\audio.wav", Assert.Single(bar.Attachments).Path);
    });

    [Fact]
    public void Rejected_send_retains_text_attachments_and_empty_history() => Sta.WpfRun(async () =>
    {
        var bar = new PromptBar { Text = "@missing inspect" };
        bar.AttachFiles([@"C:\unknown.format"]);
        bar.SendRequested = (_, _) => false;
        await bar.SendAsync();
        Assert.Equal("@missing inspect", bar.Text);
        Assert.Single(bar.Attachments);
        Assert.Empty(bar.HistoryEntries);
    });

    [Fact]
    public void Attachment_only_prompt_is_submitted() => Sta.WpfRun(async () =>
    {
        var bar = new PromptBar();
        bar.AttachFiles([@"C:\folder"]);
        PromptDraft? received = null;
        bar.SendRequested = (draft, _) => { received = draft; return true; };
        await bar.SendAsync();
        Assert.Equal(@"C:\folder", Assert.Single(received!.Files));
        Assert.Empty(received.Text);
    });

    [Fact]
    public void Enter_waits_for_pending_media_paste_and_submits_once() => Sta.WpfRun(async () =>
    {
        var read = new TaskCompletionSource<TransferContent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bar = new PromptBar { Text = "inspect", ClipboardReader = _ => read.Task };
        var deliveries = new List<PromptDraft>();
        bar.SendRequested = (draft, _) => { deliveries.Add(draft); return true; };
        bar.PasteFromClipboard();
        var send = bar.SendAsync();
        Assert.False(send.IsCompleted);
        await bar.SendAsync(); // A second Enter while waiting must not submit again.
        read.SetResult(new TransferContent(string.Empty, [@"C:\clip.png"]));
        await send;
        Assert.Equal(@"C:\clip.png", Assert.Single(Assert.Single(deliveries).Files));
    });

    [Fact]
    public void Failed_pending_paste_does_not_send_a_prompt_missing_its_attachment() => Sta.WpfRun(async () =>
    {
        var read = new TaskCompletionSource<TransferContent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bar = new PromptBar { Text = "inspect", ClipboardReader = _ => read.Task };
        var deliveries = 0;
        bar.SendRequested = (_, _) => { deliveries++; return true; };
        bar.PasteFromClipboard();
        var send = bar.SendAsync();
        read.SetException(new IOException("clipboard provider failed"));
        await send;
        Assert.Equal(0, deliveries);
        Assert.Equal("inspect", bar.Text);
        Assert.Contains("clipboard provider failed", bar.TransferErrorText);
        Assert.Empty(bar.HistoryEntries);
    });

    [Fact]
    public void Text_transfer_replaces_the_selection_and_leaves_existing_attachments() => Sta.WpfRun(async () =>
    {
        var bar = new PromptBar { Text = "one TWO three" };
        bar.AttachFiles([@"C:\movie.mp4"]);
        var input = (TextBox)bar.FindName("Input");
        input.Select(4, 3);
        bar.ClipboardReader = _ => Task.FromResult(new TransferContent("日本語", []));
        ApplicationCommands.Paste.Execute(null, input);
        await bar.ClipboardIdle;
        Assert.Equal("one 日本語 three", bar.Text);
        Assert.Single(bar.Attachments);
        Assert.Equal(7, input.CaretIndex);
    });

    [Theory]
    [InlineData("first\nsecond")]
    [InlineData("first\r\nsecond")]
    [InlineData("first\rsecond")]
    public void Multiline_paste_leaves_the_caret_after_the_inserted_text(string pasted) => Sta.WpfRun(async () =>
    {
        var bar = new PromptBar { Text = "start OLD tail" };
        var input = (TextBox)bar.FindName("Input");
        input.Select(6, 3);
        bar.ClipboardReader = _ => Task.FromResult(new TransferContent(pasted, []));
        ApplicationCommands.Paste.Execute(null, input);
        await bar.ClipboardIdle;
        Assert.Equal("start first\nsecond tail", bar.Text.ReplaceLineEndings("\n"));
        Assert.Equal(bar.Text.Length - " tail".Length, input.CaretIndex);
    });

    [Fact]
    public void Draft_uses_each_targets_path_format_and_preserves_a_trailing_newline()
    {
        var files = new[] { @"C:\it's $literal.pdf" };
        var draft = new PromptDraft("inspect\n", files);
        files[0] = "mutated";
        Assert.Equal("inspect\n'C:\\it''s $literal.pdf'", draft.Compose(paths => FilePaste.Format(paths, FilePasteStyle.PowerShell)));
        Assert.Equal("inspect\n\"C:\\it's $literal.pdf\"", draft.Compose(paths => FilePaste.Format(paths, FilePasteStyle.Windows)));
    }
}
