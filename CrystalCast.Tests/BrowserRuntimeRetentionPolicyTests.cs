namespace CrystalCast.Tests;

public sealed class BrowserRuntimeRetentionPolicyTests
{
    [Fact]
    public void DisabledPluginRetainsNoBrowserRuntimes()
    {
        var screens = new[]
        {
            new BrowserScreenProfile { Enabled = true },
            new BrowserScreenProfile { Enabled = true },
        };

        var retained = BrowserRuntimeRetentionPolicy.GetRetainedScreenIds(pluginEnabled: false, screens);

        Assert.Empty(retained);
    }

    [Fact]
    public void DisabledScreensAreNotRetained()
    {
        var enabled = new BrowserScreenProfile { Enabled = true };
        var disabled = new BrowserScreenProfile { Enabled = false };

        var retained = BrowserRuntimeRetentionPolicy.GetRetainedScreenIds(
            pluginEnabled: true,
            new[] { enabled, disabled });

        Assert.Contains(enabled.ScreenId, retained);
        Assert.DoesNotContain(disabled.ScreenId, retained);
    }

    [Fact]
    public void ScreensDeferredByTheRuntimeBudgetAreNotRetained()
    {
        var screens = Enumerable.Range(0, Configuration.MaxActiveBrowserScreens + 1)
            .Select(_ => new BrowserScreenProfile { Enabled = true })
            .ToArray();

        var retained = BrowserRuntimeRetentionPolicy.GetRetainedScreenIds(pluginEnabled: true, screens);

        Assert.Equal(Configuration.MaxActiveBrowserScreens, retained.Count);
        Assert.DoesNotContain(screens[^1].ScreenId, retained);
    }
}
