using CrystalCast.Video;

namespace CrystalCast.Tests;

public sealed class LinuxDistributionTests
{
    [Theory]
    [InlineData("ID=arch\nPRETTY_NAME=Arch Linux", (int)LinuxInstallFamily.Arch, "pacman")]
    [InlineData("ID=ubuntu\nVERSION_ID=24.04\nID_LIKE=debian", (int)LinuxInstallFamily.Debian, "apt-get")]
    [InlineData("ID=linuxmint\nID_LIKE=\"ubuntu debian\"", (int)LinuxInstallFamily.Debian, "apt-get")]
    [InlineData("ID=fedora\nVARIANT_ID=workstation", (int)LinuxInstallFamily.Fedora, "dnf")]
    [InlineData("ID=endeavouros\nID_LIKE=arch", (int)LinuxInstallFamily.Arch, "pacman")]
    public void RecognizedSystemsReceiveTheMatchingPackageManager(string release, int family, string manager)
    {
        var guide = LinuxBrowserInstallationGuide.For(LinuxDistribution.Parse(release));
        Assert.Equal(family, (int)guide.Family);
        Assert.Contains(manager, guide.PackageCommand);
    }

    [Theory]
    [InlineData("ID=steamos\nID_LIKE=arch", (int)LinuxInstallFamily.SteamOs)]
    [InlineData("ID=fedora\nVARIANT_ID=silverblue", (int)LinuxInstallFamily.ImmutableFedora)]
    [InlineData("ID=fedora\nVARIANT_ID=kinoite", (int)LinuxInstallFamily.ImmutableFedora)]
    [InlineData("ID=fedora\nVARIANT_ID=cosmic-atomic", (int)LinuxInstallFamily.ImmutableFedora)]
    [InlineData("ID=bazzite\nID_LIKE=fedora", (int)LinuxInstallFamily.ImmutableFedora)]
    [InlineData("ID=steamrt\nID_LIKE=ubuntu", (int)LinuxInstallFamily.Unknown)]
    [InlineData("ID=org.freedesktop.platform\nID_LIKE=debian", (int)LinuxInstallFamily.Unknown)]
    [InlineData("ID=unrecognized", (int)LinuxInstallFamily.Unknown)]
    public void ImmutableSystemsAndRuntimeContainersDoNotGetMutableHostInstallCommands(string release, int family)
    {
        var guide = LinuxBrowserInstallationGuide.For(LinuxDistribution.Parse(release));
        Assert.Equal(family, (int)guide.Family);
        Assert.Null(guide.PackageCommand);
    }

    [Fact]
    public void ParsingUnquotesMetadataWithoutExecutingItOrInsertingItIntoCommands()
    {
        var distro = LinuxDistribution.Parse("""
            # a comment
            ID=ubuntu
            PRETTY_NAME="Ubuntu \"test\" \$HOME $(touch /tmp/no)"
            VERSION_ID='24.04'
            ID_LIKE="debian"
            """);
        Assert.Equal("Ubuntu \"test\" $HOME $(touch /tmp/no)", distro.Name);
        Assert.Equal("24.04", distro.Version);
        Assert.Equal(["debian"], distro.Like);
        Assert.DoesNotContain("touch", LinuxBrowserInstallationGuide.For(distro).PackageCommand);
    }

    [Fact]
    public void HostMetadataTakesPriorityOverSandboxMetadata()
    {
        var distro = LinuxDistribution.Detect(path => path switch
        {
            "/run/host/os-release" => "ID=ubuntu\nPRETTY_NAME=Ubuntu",
            "/etc/os-release" => "ID=org.freedesktop.platform",
            _ => null,
        });
        Assert.Equal("ubuntu", distro.Id);
        Assert.Equal("/run/host/os-release", distro.Source);
    }

    [Fact]
    public void UnavailableOrMalformedMetadataFallsBackToTheNextLocation()
    {
        var distro = LinuxDistribution.Detect(path => path switch
        {
            "/run/host/etc/os-release" => throw new UnauthorizedAccessException(),
            "/run/host/os-release" => "ID=$(uname)",
            "/etc/os-release" => "ID=arch",
            _ => null,
        });
        Assert.Equal("arch", distro.Id);
    }

    [Fact]
    public void OversizedAndMissingMetadataProduceNoGuessedInstallCommand()
    {
        Assert.Equal(LinuxDistribution.Unknown, LinuxDistribution.Parse("ID=arch\n" + new string('a', 16384)));
        Assert.Null(LinuxBrowserInstallationGuide.For(LinuxDistribution.Detect(_ => null)).PackageCommand);
    }

    [Theory]
    [InlineData("ubuntu")]
    [InlineData("fedora")]
    public void PrerequisitesAreNotPresentedAsCompleteWpeInstallers(string id)
    {
        var guide = LinuxBrowserInstallationGuide.For(LinuxDistribution.Parse("ID=" + id));
        Assert.DoesNotContain("wpewebkit", guide.PackageCommand);
        Assert.NotNull(guide.RepositoryCheck);
        Assert.Contains("2.54", guide.Explanation);
    }
}
