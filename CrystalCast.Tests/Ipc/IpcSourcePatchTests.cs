using System.Reflection;
using CrystalCast.Ipc;
using CrystalCast.Sources;

namespace CrystalCast.Tests;

public sealed class IpcSourcePatchTests
{
    [Fact]
    public void ASourcePatchWithoutAProviderAppliesToTheScreensCurrentProvider()
    {
        var screen = new BrowserScreenProfile { ProviderKind = BrowserSourceProviderKind.Twitch };
        var request = new ScreenIpcSourceUpdateRequest
        {
            Source = new ScreenSourcePatchDto { Url = "https://www.twitch.tv/somechannel", Volume = 0.25f },
        };

        Assert.True(ScreenPatchApplier.TryApplySourceMutation(screen, request, out var updated, out var error), error);

        Assert.Equal(BrowserSourceProviderKind.Twitch, updated.ProviderKind);
        Assert.Equal("https://www.twitch.tv/somechannel", updated.TwitchUrl);
        Assert.Equal(0.25f, updated.TwitchVolume);
        Assert.Equal(string.Empty, updated.YouTubeUrl);
    }

    [Fact]
    public void ALinkOfAnotherProviderIsRefusedUnlessTheProviderIsNamed()
    {
        var screen = new BrowserScreenProfile { ProviderKind = BrowserSourceProviderKind.YouTube };
        var patch = new ScreenSourcePatchDto { Url = "https://vimeo.com/123456789" };

        Assert.False(ScreenPatchApplier.TryApplySourceMutation(
            screen,
            new ScreenIpcSourceUpdateRequest { Source = patch },
            out _,
            out var error));
        Assert.Contains("YouTube", error, StringComparison.Ordinal);

        Assert.True(ScreenPatchApplier.TryApplySourceMutation(
            screen,
            new ScreenIpcSourceUpdateRequest { Provider = BrowserSourceProviderKind.Vimeo, Source = patch },
            out var updated,
            out error), error);
        Assert.Equal(BrowserSourceProviderKind.Vimeo, updated.ProviderKind);
        Assert.Equal("https://vimeo.com/123456789", updated.VimeoUrl);
    }

    [Fact]
    public void FieldsTheProviderDoesNotHaveAreIgnored()
    {
        var screen = new BrowserScreenProfile { ProviderKind = BrowserSourceProviderKind.Twitch };
        var request = new ScreenIpcSourceUpdateRequest
        {
            Source = new ScreenSourcePatchDto { Loop = true, PlaybackRate = 2.0f, PlaylistAutoplayNext = false, Autoplay = false },
        };

        Assert.True(ScreenPatchApplier.TryApplySourceMutation(screen, request, out var updated, out var error), error);

        Assert.False(updated.TwitchAutoplay);
        var playback = BrowserSourceIpcAdapters.Get(updated.ProviderKind).BuildPlaybackState(updated, telemetry: null);
        Assert.False(playback.Loop);
        Assert.True(playback.PlaylistAutoplayNext);
        Assert.Equal(1.0f, playback.Rate);
    }

    [Fact]
    public void AnEmptyLinkUnloadsTheSource()
    {
        var screen = new BrowserScreenProfile { YouTubeUrl = "https://youtu.be/dQw4w9WgXcQ" };
        var request = new ScreenIpcSourceUpdateRequest { Source = new ScreenSourcePatchDto { Url = "  " } };

        Assert.True(ScreenPatchApplier.TryApplySourceMutation(screen, request, out var updated, out var error), error);

        Assert.Equal(string.Empty, updated.YouTubeUrl);
    }

    [Fact]
    public void SourceDetailsCarryTheProvidersOwnFields()
    {
        var twitch = new BrowserScreenProfile
        {
            ProviderKind = BrowserSourceProviderKind.Twitch,
            TwitchUrl = "https://www.twitch.tv/somechannel",
        };
        var twitchDetails = BrowserSourceIpcAdapters.Get(twitch.ProviderKind).BuildSourceDetails(twitch, telemetry: null);
        Assert.Equal("somechannel", twitchDetails.ChannelName);
        Assert.Equal("https://www.twitch.tv/somechannel", twitchDetails.Url);
        Assert.Equal(string.Empty, twitchDetails.Hash);

        var vimeo = new BrowserScreenProfile
        {
            ProviderKind = BrowserSourceProviderKind.Vimeo,
            VimeoUrl = "https://vimeo.com/123456789?h=abcdef1234",
            PlaybackPaused = true,
        };
        var vimeoDetails = BrowserSourceIpcAdapters.Get(vimeo.ProviderKind).BuildSourceDetails(vimeo, telemetry: null);
        Assert.Equal("abcdef1234", vimeoDetails.Hash);
        Assert.Equal("123456789", vimeoDetails.VideoId);
        Assert.Equal("https://vimeo.com/123456789?h=abcdef1234", vimeoDetails.CanonicalUrl);
        Assert.Equal(ScreenPlaybackState.Paused, vimeoDetails.State);
        Assert.Equal(string.Empty, vimeoDetails.ChannelName);
    }

    [Fact]
    public void ChangeKindsFollowWhatThePatchTouches()
    {
        Assert.Equal(
            [ScreenIpcChangeKind.Playback],
            ScreenChangePublisher.GetSourceUpdateChangeKinds(new ScreenIpcSourceUpdateRequest
            {
                Source = new ScreenSourcePatchDto { PlaybackPaused = true, PositionMs = 1000 },
            }));
        Assert.Equal(
            [ScreenIpcChangeKind.Source, ScreenIpcChangeKind.Playback],
            ScreenChangePublisher.GetSourceUpdateChangeKinds(new ScreenIpcSourceUpdateRequest
            {
                Provider = BrowserSourceProviderKind.Vimeo,
                Source = new ScreenSourcePatchDto { Url = "https://vimeo.com/123456789", Restart = true },
            }));
        Assert.Equal(
            [ScreenIpcChangeKind.Source],
            ScreenChangePublisher.GetSourceUpdateChangeKinds(new ScreenIpcSourceUpdateRequest()));
    }

    [Fact]
    public void RequestsUseOneSourceObjectInCamelCase()
    {
        var request = IpcJsonService.Deserialize<ScreenIpcMutationRequest>(
            "{\"screenId\":\"s\",\"provider\":\"Dailymotion\",\"source\":{\"url\":\"x8abcde\",\"captureFps\":30}}");

        Assert.NotNull(request);
        Assert.Equal(BrowserSourceProviderKind.Dailymotion, request.Provider);
        Assert.Equal("x8abcde", request.Source?.Url);
        Assert.Equal(30.0f, request.Source?.CaptureFps);
    }

    [Fact]
    public void EveryProviderHasSettingsAndAnAdapter()
    {
        foreach (var provider in Enum.GetValues<BrowserSourceProviderKind>())
        {
            Assert.True(BrowserProviderSettings.IsSupported(provider));
            Assert.True(BrowserSourceIpcAdapters.IsSupported(provider));
            Assert.True(BrowserSourceProviderRegistry.IsSupported(provider));
            Assert.Equal(provider, BrowserProviderSettings.For(provider).ProviderKind);
            Assert.Equal(provider, BrowserSourceIpcAdapters.Get(provider).ProviderKind);
        }
    }

    [Fact]
    public void ProviderSettingsReadAndWriteTheirOwnFields()
    {
        foreach (var settings in BrowserProviderSettings.All)
        {
            var screen = new BrowserScreenProfile();
            var url = $"link-for-{settings.ProviderKind}";
            settings.SetUrl(screen, url);
            settings.SetVolume(screen, 0.11f);
            settings.SetBrowserWidth(screen, 1111);

            Assert.Equal(url, settings.GetUrl(screen));
            Assert.Equal(0.11f, settings.GetVolume(screen));
            Assert.Equal(1111, settings.GetBrowserWidth(screen));
            foreach (var other in BrowserProviderSettings.All.Where(other => other != settings))
            {
                Assert.NotEqual(url, other.GetUrl(screen));
                Assert.NotEqual(1111, other.GetBrowserWidth(screen));
            }
        }
    }

    [Fact]
    public void ChannelNamesAreStable()
    {
        var channels = typeof(IpcChannels)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(field => (string)field.GetRawConstantValue()!)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                "CrystalCast.ApiVersion",
                "CrystalCast.Disposed",
                "CrystalCast.GetCapabilities",
                "CrystalCast.Ready",
                "CrystalCast.Screen.ApplyState",
                "CrystalCast.Screen.Changed",
                "CrystalCast.Screen.Create",
                "CrystalCast.Screen.GetSnapshot",
                "CrystalCast.Screen.GetSourceState",
                "CrystalCast.Screen.Playlist.Add",
                "CrystalCast.Screen.Playlist.Clear",
                "CrystalCast.Screen.Playlist.Get",
                "CrystalCast.Screen.Playlist.Move",
                "CrystalCast.Screen.Playlist.Remove",
                "CrystalCast.Screen.Playlist.SetOptions",
                "CrystalCast.Screen.Playlist.Skip",
                "CrystalCast.Screen.Remove",
                "CrystalCast.Screen.SetSourceLock",
                "CrystalCast.Screen.Update",
                "CrystalCast.Screen.UpdateSource",
            ],
            channels);
    }
}
