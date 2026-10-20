using System;
using System.Text.RegularExpressions;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

public sealed class ShortcutIconKeyTests
{
    private static readonly DateTime T0 = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Icon_key_changes_with_timestamp()
    {
        var before = ShortcutIconKey.For(@"C:\Program Files (x86)\Steam\steam.exe", T0);
        var after = ShortcutIconKey.For(@"C:\Program Files (x86)\Steam\steam.exe", T0.AddSeconds(1));

        Assert.NotEqual(before, after);   // an updated exe gets its icon read again
    }

    [Fact]
    public void Icon_key_is_stable_for_the_same_input()
        => Assert.Equal(ShortcutIconKey.For(@"C:\a\b.exe", T0), ShortcutIconKey.For(@"C:\a\b.exe", T0));

    [Fact]
    public void Icon_key_ignores_path_case_and_slash_spelling()
        => Assert.Equal(ShortcutIconKey.For(@"C:\Program Files\X\x.exe", T0), ShortcutIconKey.For("c:/program files//x/X.EXE", T0));

    [Fact]
    public void Icon_key_differs_per_path()
        => Assert.NotEqual(ShortcutIconKey.For(@"C:\a\one.exe", T0), ShortcutIconKey.For(@"C:\a\two.exe", T0));

    [Fact]
    public void Icon_key_ignores_the_kind_of_timestamp()
    {
        // File.GetLastWriteTimeUtc is Utc, but a caller with a Local or Unspecified value must not get another key.
        var utc = new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
        var unspecified = new DateTime(utc.Ticks, DateTimeKind.Unspecified);
        Assert.Equal(ShortcutIconKey.For(@"C:\a\b.exe", utc), ShortcutIconKey.For(@"C:\a\b.exe", unspecified));
    }

    [Theory]
    [InlineData(@"C:\a\b.exe")]
    [InlineData(@"shell:AppsFolder\Microsoft.GamingApp_8wekyb3d8bbwe!Microsoft.Xbox.App")]
    [InlineData("")]
    public void Icon_key_is_a_safe_file_name(string path)
        => Assert.Matches(new Regex("^[0-9a-f]{16}$"), ShortcutIconKey.For(path, T0));
}
