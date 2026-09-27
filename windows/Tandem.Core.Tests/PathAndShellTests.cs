using Tandem.Core.Adb;

namespace Tandem.Core.Tests;

public class PathAndShellTests
{
    [Theory]
    [InlineData("plain", "'plain'")]
    [InlineData("with space", "'with space'")]
    [InlineData("it's", "'it'\\''s'")]
    [InlineData("$(rm -rf /)", "'$(rm -rf /)'")]
    public void Shell_quote_neutralises_everything(string input, string expected) =>
        Assert.Equal(expected, ShellQuote.Quote(input));

    [Fact]
    public void Shell_join_quotes_each_argument() =>
        Assert.Equal("'a b' 'c'", ShellQuote.Join(["a b", "c"]));

    [Theory]
    [InlineData("/storage/emulated/0", "DCIM", "/storage/emulated/0/DCIM")]
    [InlineData("/", "sdcard", "/sdcard")]
    [InlineData("/storage/emulated/0/", "x", "/storage/emulated/0/x")]
    public void Combine(string dir, string name, string expected) =>
        Assert.Equal(expected, RemotePath.Combine(dir, name));

    [Theory]
    [InlineData("/storage/emulated/0/DCIM", "/storage/emulated/0")]
    [InlineData("/sdcard", "/")]
    [InlineData("/", null)]
    [InlineData("/a/b/", "/a")]
    public void Parent(string path, string? expected) =>
        Assert.Equal(expected, RemotePath.GetParent(path));

    [Fact]
    public void File_name_ignores_trailing_slash()
    {
        Assert.Equal("Camera", RemotePath.GetFileName("/storage/emulated/0/DCIM/Camera/"));
        Assert.Equal("a.jpg", RemotePath.GetFileName("/a.jpg"));
    }

    [Theory]
    [InlineData("Holiday photos", true)]
    [InlineData("a/b", false)]
    [InlineData("what?", false)]
    [InlineData("..", false)]
    [InlineData("", false)]
    public void Name_validation(string name, bool valid) =>
        Assert.Equal(valid, RemotePath.IsValidName(name));

    [Fact]
    public void Unique_name_adds_counter_before_extension()
    {
        var taken = new HashSet<string> { "photo.jpg", "photo (2).jpg", "notes" };
        Assert.Equal("photo (3).jpg", RemotePath.UniqueName("photo.jpg", taken));
        Assert.Equal("notes (2)", RemotePath.UniqueName("notes", taken));
        Assert.Equal("new.txt", RemotePath.UniqueName("new.txt", taken));
    }
}
