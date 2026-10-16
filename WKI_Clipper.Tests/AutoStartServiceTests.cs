using System;
using Microsoft.Win32;
using WKI_Clipper.Services;
using Xunit;

namespace WKI_Clipper.Tests;

/// <summary>
/// Autostart against a throw-away registry root (never the real Run key). Windows starts an
/// entry only if it is in Run AND its switch under Explorer\StartupApproved\Run (Settings →
/// Apps → Autostart, Task Manager) is not off — the app has to look at both.
/// </summary>
public sealed class AutoStartServiceTests : IDisposable
{
    private const string Exe = @"C:\Programs\WKI Clipper\WKI_Clipper.exe";
    private static readonly byte[] SwitchedOff = { 0x03, 0, 0, 0, 0x9D, 0xA1, 0x00, 0x21, 0x75, 0xE4, 0xDB, 0x01 };
    private static readonly byte[] SwitchedOn = { 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };

    private const string TestsKey = @"Software\WKI_Clipper_Tests";
    private readonly string _rootPath = TestsKey + @"\AutoStart_" + Guid.NewGuid().ToString("N");
    private readonly RegistryKey _root;
    private readonly AutoStartService _service;

    public AutoStartServiceTests()
    {
        _root = Registry.CurrentUser.CreateSubKey(_rootPath);
        _service = new AutoStartService(_root, () => Exe);
    }

    public void Dispose()
    {
        _root.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(_rootPath, throwOnMissingSubKey: false);
        using (var parent = Registry.CurrentUser.OpenSubKey(TestsKey))
            if (parent is null || parent.SubKeyCount > 0 || parent.ValueCount > 0) return;
        Registry.CurrentUser.DeleteSubKey(TestsKey, throwOnMissingSubKey: false);
    }

    private object? RunValue()
    {
        using var k = _root.OpenSubKey(AutoStartService.RunKeyPath);
        return k?.GetValue(AutoStartService.ValueName);
    }

    private object? SwitchValue()
    {
        using var k = _root.OpenSubKey(AutoStartService.ApprovedKeyPath);
        return k?.GetValue(AutoStartService.ValueName);
    }

    private void SetRun(string value)
    {
        using var k = _root.CreateSubKey(AutoStartService.RunKeyPath);
        k.SetValue(AutoStartService.ValueName, value);
    }

    private void SetSwitch(byte[] value)
    {
        using var k = _root.CreateSubKey(AutoStartService.ApprovedKeyPath);
        k.SetValue(AutoStartService.ValueName, value, RegistryValueKind.Binary);
    }

    [Fact]
    public void Enabling_writes_the_quoted_exe_path()
    {
        _service.SetEnabled(true);

        Assert.Equal($"\"{Exe}\"", RunValue());
        Assert.True(_service.IsEnabled());
    }

    [Fact]
    public void Entry_switched_off_in_windows_is_not_reported_as_on()
    {
        SetRun($"\"{Exe}\"");
        SetSwitch(SwitchedOff);

        Assert.False(_service.IsEnabled());
    }

    [Fact]
    public void Entry_switched_on_in_windows_counts_as_on()
    {
        SetRun($"\"{Exe}\"");
        SetSwitch(SwitchedOn);

        Assert.True(_service.IsEnabled());
    }

    [Fact]
    public void Enabling_clears_an_earlier_off_switch()
    {
        SetRun($"\"{Exe}\"");
        SetSwitch(SwitchedOff);

        _service.SetEnabled(true);

        Assert.Null(SwitchValue());
        Assert.True(_service.IsEnabled());
    }

    [Fact]
    public void Disabling_removes_the_entry()
    {
        _service.SetEnabled(true);

        _service.SetEnabled(false);

        Assert.Null(RunValue());
        Assert.False(_service.IsEnabled());
    }

    [Fact]
    public void Nothing_registered_is_off()
    {
        Assert.False(_service.IsEnabled());
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(new byte[0], false)]
    [InlineData(new byte[] { 0x02, 0, 0, 0 }, false)]
    [InlineData(new byte[] { 0x03, 0, 0, 0 }, true)]
    public void Windows_switch_reads_odd_first_byte_as_off(byte[]? flag, bool off)
    {
        Assert.Equal(off, AutoStartService.IsSwitchedOff(flag));
    }
}
