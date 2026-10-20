using System.Collections.Generic;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

public sealed class VdfTests
{
    [Fact]
    public void Parses_libraryfolders_with_nested_blocks_and_escaped_backslashes()
    {
        var root = Vdf.Parse(PhillipsPc.LibraryFoldersVdf);

        var folders = Assert.IsType<Dictionary<string, object>>(root["libraryfolders"]);
        Assert.Equal(2, folders.Count);

        var first = Assert.IsType<Dictionary<string, object>>(folders["0"]);
        Assert.Equal(@"C:\Program Files (x86)\Steam", first["path"]);   // "\\" in the file is one backslash
        Assert.Equal("", first["label"]);

        var second = Assert.IsType<Dictionary<string, object>>(folders["1"]);
        Assert.Equal(@"D:\SteamLibrary", second["path"]);
        var apps = Assert.IsType<Dictionary<string, object>>(second["apps"]);
        Assert.Equal(3, apps.Count);
        Assert.Equal("31384776198", apps["1874880"]);
    }

    [Fact]
    public void Keys_are_case_insensitive()
    {
        var root = Vdf.Parse("\"AppState\" { \"AppID\" \"42\" }");

        var state = Assert.IsType<Dictionary<string, object>>(root["appstate"]);
        Assert.Equal("42", state["APPID"]);
    }

    [Fact]
    public void Quoted_escapes_and_comments_and_unquoted_tokens_are_understood()
    {
        var root = Vdf.Parse("""
            // header comment
            "root"
            {
                "quote"   "say \"hi\""
                "line"    "a\nb"
                unquoted  value
                "tab"     "x\ty"   // trailing comment
            }
            """);

        var r = Assert.IsType<Dictionary<string, object>>(root["root"]);
        Assert.Equal("say \"hi\"", r["quote"]);
        Assert.Equal("a\nb", r["line"]);
        Assert.Equal("value", r["unquoted"]);
        Assert.Equal("x\ty", r["tab"]);
    }

    [Fact]
    public void Old_format_with_single_backslashes_keeps_the_path()
    {
        // Steam before 2021 wrote "1" "D:\SteamLibrary" with a single backslash.
        var root = Vdf.Parse("\"LibraryFolders\"\n{\n\t\"1\"\t\t\"D:\\SteamLibrary\"\n}\n");

        var folders = Assert.IsType<Dictionary<string, object>>(root["LibraryFolders"]);
        Assert.Equal(@"D:\SteamLibrary", folders["1"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n  ")]
    [InlineData("\"a\"")]
    [InlineData("\"a\" {")]
    [InlineData("\"a\" { \"b\" { \"c\" \"d\"")]
    [InlineData("} } \"a\" \"b\"")]
    [InlineData("\"unterminated")]
    [InlineData("{ { {")]
    public void Malformed_input_never_throws(string text)
    {
        var root = Vdf.Parse(text);
        Assert.NotNull(root);
    }

    [Fact]
    public void Truncated_file_keeps_what_was_complete()
    {
        var root = Vdf.Parse("\"AppState\" { \"appid\" \"1874880\" \"name\" \"Arma Reforger\" \"installdir\"");

        var state = Assert.IsType<Dictionary<string, object>>(root["AppState"]);
        Assert.Equal("1874880", state["appid"]);
        Assert.Equal("Arma Reforger", state["name"]);
    }

    [Fact]
    public void Absurd_nesting_does_not_overflow_the_stack()
    {
        var text = string.Concat(System.Linq.Enumerable.Repeat("\"a\" { ", 50_000));
        var root = Vdf.Parse(text);
        Assert.NotNull(root);
    }
}
