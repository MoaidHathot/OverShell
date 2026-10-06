using OverShell.Core.Git;
using OverShell.Core.Herd;
using Xunit;

namespace OverShell.Tests;

public class HerdLogAndGitStatusTests
{
    [Fact]
    public void Log_entries_round_trip_through_the_file_and_a_cut_line_is_skipped()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"overshell-herdlog-{Guid.NewGuid():N}");
        try
        {
            var path = Path.Combine(dir, HerdLog.FileName(new DateTimeOffset(2026, 10, 6, 21, 5, 7, TimeSpan.Zero)));
            Assert.EndsWith(".jsonl", path);
            var at = new DateTimeOffset(2026, 10, 6, 21, 5, 9, 123, TimeSpan.FromHours(3));
            using (var log = new HerdLog(path))
            {
                log.Append(new HerdLogEntry(at, "state", "t1", "api", "opencode", "Working", "Blocked", "Run `git push`?"));
                log.Append(new HerdLogEntry(at.AddSeconds(1), "attention", "t1", "api", "opencode", null, "Blocked", "needs approval"));
                log.Append(new HerdLogEntry(at.AddSeconds(2), "closed", "t2", null, null, "Idle", null, null));
            }

            File.AppendAllText(path, "{\"at\":\"2026-10-06T21:05:1");
            var read = HerdLog.Read(path);

            Assert.Equal(3, read.Count);
            Assert.Equal(at, read[0].At);
            Assert.Equal("state", read[0].Kind);
            Assert.Equal("Run `git push`?", read[0].Detail);
            Assert.Equal("opencode", read[0].Harness);
            Assert.Null(read[2].Label);
            Assert.Equal("Idle", read[2].From);
            Assert.Contains("api  Working -> Blocked", read[0].Describe());
            Assert.Contains("needs you: Blocked", read[1].Describe());
            Assert.Contains("t2  closed", read[2].Describe());
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void Files_sort_newest_first_and_prune_keeps_that_many()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"overshell-herdlog-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(dir);
            foreach (var stamp in new[] { "20261001-090000", "20261003-090000", "20261002-090000", "20261004-090000" })
            {
                File.WriteAllText(Path.Combine(dir, $"{stamp}.jsonl"), string.Empty);
            }

            File.WriteAllText(Path.Combine(dir, "notes.txt"), "not a log");
            var files = HerdLog.Files(dir).Select(Path.GetFileName).ToList();
            Assert.Equal(["20261004-090000.jsonl", "20261003-090000.jsonl", "20261002-090000.jsonl", "20261001-090000.jsonl"], files);

            Assert.Equal(2, HerdLog.Prune(dir, 2));
            Assert.Equal(["20261004-090000.jsonl", "20261003-090000.jsonl"], HerdLog.Files(dir).Select(Path.GetFileName).ToList());
            Assert.True(File.Exists(Path.Combine(dir, "notes.txt")));
            Assert.Empty(HerdLog.Files(Path.Combine(dir, "missing")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Porcelain_parses_renames_quotes_and_describes_each_status()
    {
        var parsed = GitStatus.Parse(" M src/a.cs\n?? new.txt\nR  old.cs -> new.cs\nA  \"with space.md\"\n D gone.cs\nUU both.cs\n\n");
        Assert.Equal(6, parsed.Count);
        Assert.Equal(new GitChange(" M", "src/a.cs"), parsed[0]);
        Assert.Equal("modified", parsed[0].Describe());
        Assert.Equal("untracked", parsed[1].Describe());
        Assert.Equal(new GitChange("R ", "new.cs"), parsed[2]);
        Assert.Equal("renamed", parsed[2].Describe());
        Assert.Equal("with space.md", parsed[3].Path);
        Assert.Equal("added", parsed[3].Describe());
        Assert.Equal("deleted", parsed[4].Describe());
        Assert.Equal("conflict", parsed[5].Describe());
        Assert.Empty(GitStatus.Parse(null));
        Assert.Empty(GitStatus.Parse("\r\n"));
    }

    [Fact]
    public void Compare_reports_what_appeared_changed_or_left()
    {
        var before = GitStatus.Parse(" M same.cs\n M was-modified.cs\n?? will-commit.txt");
        var now = GitStatus.Parse(" M same.cs\nM  was-modified.cs\n M fresh.cs");

        var changed = GitStatus.Compare(before, now);

        Assert.Equal(3, changed.Count);
        Assert.Contains(changed, c => c.Path == "was-modified.cs" && c.Status == "M ");
        Assert.Contains(changed, c => c.Path == "fresh.cs" && c.Describe() == "modified");
        Assert.Contains(changed, c => c.Path == "will-commit.txt" && c.Describe() == "clean");
        Assert.DoesNotContain(changed, c => c.Path == "same.cs");
        Assert.Empty(GitStatus.Compare(now, now));
    }
}
