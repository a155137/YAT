namespace YAT.Updater.Tests;

// The updater's command line (UpdateInstallProtocol, Task #051.C): exactly what YAT passes, checked before anything else.
public class UpdaterArgumentsTests
{
    private static readonly string Folder = Path.Combine(Path.GetTempPath(), "YAT folder");
    private static readonly string Updates = Path.Combine(Path.GetTempPath(), "updates");

    private static List<string> Install(params (string Name, string? Value)[] changes)
    {
        var options = new List<(string Name, string Value)>
        {
            ("--protocol", "1"), ("--install-dir", Folder), ("--updates-root", Updates), ("--version", "0.3.0"),
            ("--from-version", "0.2.0"), ("--pid", "1234"), ("--pid-start", "638900000000000000"), ("--pipe-in", "1688"), ("--pipe-out", "1692")
        };
        foreach (var (name, value) in changes)
        {
            options.RemoveAll(option => option.Name == name);
            if (value is not null)
            {
                options.Add((name, value));
            }
        }

        return ["install", .. options.SelectMany(option => new[] { option.Name, option.Value })];
    }

    private static string Problem(IReadOnlyList<string> args)
    {
        Assert.False(UpdaterArguments.TryParse(args, out var parsed, out var problem));
        Assert.Null(parsed);
        return problem!;
    }

    [Fact]
    public void WhatYatPassesIsRead()
    {
        Assert.True(UpdaterArguments.TryParse(Install(), out var parsed, out _));

        Assert.True(parsed!.IsInstall);
        Assert.Equal(Folder, parsed.InstallDirectory);
        Assert.Equal(Updates, parsed.UpdatesRoot);
        Assert.Equal(("0.3.0", "0.2.0"), (parsed.Version.ToString(), parsed.FromVersion.ToString()));
        Assert.Equal((1234, 638900000000000000L, "1688", "1692"), (parsed.ProcessId, parsed.ProcessStart, parsed.PipeIn, parsed.PipeOut));
    }

    [Fact]
    public void RecoverNeedsOnlyTheInstallation()
    {
        Assert.True(UpdaterArguments.TryParse(["recover", "--protocol", "1", "--install-dir", Folder + @"\"], out var parsed, out _));

        Assert.False(parsed!.IsInstall);
        Assert.Equal(Folder, parsed.InstallDirectory);
        Assert.Contains("Unknown option", Problem(["recover", "--protocol", "1", "--install-dir", Folder, "--pid", "1"]), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData]
    [InlineData("uninstall")]
    [InlineData("INSTALL")]
    public void OnlyInstallAndRecoverAreCommands(params string[] args)
    {
        Assert.Contains("command", Problem(args), StringComparison.Ordinal);
    }

    [Fact]
    public void AnotherProtocolIsRefused()
    {
        Assert.Contains("Protocol 2 is not supported", Problem(Install(("--protocol", "2"))), StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownMissingOrRepeatedOptionsAreRefused()
    {
        Assert.Contains("Unknown option '--shell'", Problem([.. Install(), "--shell", "cmd"]), StringComparison.Ordinal);
        Assert.Contains("--pid", Problem(Install(("--pid", null))), StringComparison.Ordinal);
        Assert.Contains("given twice", Problem([.. Install(), "--pid", "1"]), StringComparison.Ordinal);
        Assert.Contains("has no value", Problem([.. Install(("--pipe-out", null)), "--pipe-out"]), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"YAT folder")]
    [InlineData(@"..\YAT")]
    [InlineData(@"C:YAT")]
    [InlineData(@"\YAT")]
    [InlineData(@"\\?\C:\YAT")]
    [InlineData(@"\\.\C:\YAT")]
    [InlineData("")]
    public void TheInstallationMustBeAFullPath(string folder)
    {
        Assert.Contains("full local path", Problem(Install(("--install-dir", folder))), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--version", "0.3.0-beta")]
    [InlineData("--version", "0.3")]
    [InlineData("--from-version", "latest")]
    public void VersionsMustBeStable(string option, string value)
    {
        Assert.Contains("stable release versions", Problem(Install((option, value))), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--pid", "0")]
    [InlineData("--pid", "-5")]
    [InlineData("--pid", "12a")]
    [InlineData("--pid-start", "0")]
    [InlineData("--pid-start", "1e9")]
    public void TheProcessMustBeNumbers(string option, string value)
    {
        Assert.Contains("positive numbers", Problem(Install((option, value))), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0x1F")]
    [InlineData("pipe")]
    [InlineData("")]
    public void PipeHandlesMustBeNumbers(string value)
    {
        Assert.Contains("pipe handles", Problem(Install(("--pipe-in", value))), StringComparison.Ordinal);
    }
}
