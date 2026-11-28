using System.Text.Json;
using Xperience.Core.Domain.Entities;
using Xperience.Core.Domain.Models;
using Xperience.Core.Widgets;
using Xunit;

namespace Xperience.Core.Tests.Widgets;

public sealed class WidgetAdaptiveCardBuilderTests
{
    [Theory]
    [InlineData(WidgetDefinitions.MiniPlayer)]
    [InlineData(WidgetDefinitions.RecommendedPlaylists)]
    [InlineData(WidgetDefinitions.MusicHub)]
    public void GetTemplateJson_ReturnsValidAdaptiveCardV15(string definitionId)
    {
        string templateJson = WidgetAdaptiveCardBuilder.GetTemplateJson(definitionId);
        Assert.False(string.IsNullOrWhiteSpace(templateJson));

        using var doc = JsonDocument.Parse(templateJson);
        var root = doc.RootElement;

        Assert.Equal("AdaptiveCard", root.GetProperty("type").GetString());
        Assert.Equal("1.5", root.GetProperty("version").GetString());
        Assert.Equal(WidgetAdaptiveCardBuilder.AdaptiveCardSchemaUrl, root.GetProperty("$schema").GetString());
        Assert.True(root.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.Array && body.GetArrayLength() > 0);
    }

    [Fact]
    public void MiniPlayerTemplate_ContainsPlaybackAndLikeVerbs()
    {
        string templateJson = WidgetAdaptiveCardBuilder.GetTemplateJson(WidgetDefinitions.MiniPlayer);

        Assert.Contains($"\"verb\":\"{WidgetActionVerbs.Previous}\"", templateJson);
        Assert.Contains($"\"verb\":\"{WidgetActionVerbs.PlayPause}\"", templateJson);
        Assert.Contains($"\"verb\":\"{WidgetActionVerbs.Next}\"", templateJson);
        Assert.Contains($"\"verb\":\"{WidgetActionVerbs.ToggleLike}\"", templateJson);
        Assert.Contains("${elapsedText}", templateJson);
        Assert.Contains("${durationText}", templateJson);
    }

    [Fact]
    public void RecommendedPlaylistsTemplate_ContainsRefreshAndFourPlaylistSlots()
    {
        string templateJson = WidgetAdaptiveCardBuilder.GetTemplateJson(WidgetDefinitions.RecommendedPlaylists);

        Assert.Contains($"\"verb\":\"{WidgetActionVerbs.RefreshRecommendations}\"", templateJson);
        Assert.Contains($"\"verb\":\"{WidgetActionVerbs.PlayPlaylist}\"", templateJson);
        Assert.Contains("${card0.title}", templateJson);
        Assert.Contains("${card1.title}", templateJson);
        Assert.Contains("${card2.title}", templateJson);
        Assert.Contains("${card3.title}", templateJson);
    }

    [Fact]
    public void MusicHubTemplate_CombinesNowPlayingAmbientAndRecommendedGrid()
    {
        string templateJson = WidgetAdaptiveCardBuilder.GetTemplateJson(WidgetDefinitions.MusicHub);

        Assert.Contains($"\"verb\":\"{WidgetActionVerbs.OpenAmbient}\"", templateJson);
        Assert.Contains($"\"verb\":\"{WidgetActionVerbs.PlayPause}\"", templateJson);
        Assert.Contains($"\"verb\":\"{WidgetActionVerbs.ToggleLike}\"", templateJson);
        Assert.Contains($"\"verb\":\"{WidgetActionVerbs.RefreshRecommendations}\"", templateJson);
        Assert.Contains($"\"verb\":\"{WidgetActionVerbs.PlayPlaylist}\"", templateJson);
        Assert.Contains("${card0.title}", templateJson);
        Assert.Contains("${card3.title}", templateJson);
    }

    [Theory]
    [InlineData(WidgetDefinitions.MiniPlayer)]
    [InlineData(WidgetDefinitions.RecommendedPlaylists)]
    [InlineData(WidgetDefinitions.MusicHub)]
    public void BuildDataJson_IdleState_ProducesValidJsonWithFallbacks(string definitionId)
    {
        var idleSnapshot = new WidgetStateSnapshot
        {
            Playback = new WidgetPlaybackSnapshot(),
            Recommendations = [],
            LikedSongsCount = 24
        };

        string dataJson = WidgetAdaptiveCardBuilder.BuildDataJson(definitionId, idleSnapshot);
        using var doc = JsonDocument.Parse(dataJson);
        var root = doc.RootElement;

        Assert.False(root.GetProperty("hasTrack").GetBoolean());
        Assert.False(root.GetProperty("isPlaying").GetBoolean());
        Assert.Equal("Xperience Music", root.GetProperty("trackTitle").GetString());
        Assert.Equal("Tap to play music", root.GetProperty("trackArtist").GetString());
        Assert.Equal("▶", root.GetProperty("playPauseLabel").GetString());
        Assert.Equal("♡", root.GetProperty("likeLabel").GetString());
        Assert.Equal("0:00", root.GetProperty("elapsedText").GetString());
        Assert.Equal("0:00", root.GetProperty("durationText").GetString());
        Assert.Equal(WidgetAdaptiveCardBuilder.DefaultBrandArtworkUrl, root.GetProperty("artworkUrl").GetString());

        // All 4 playlist cards must be populated with sensible fallbacks
        Assert.Equal("Daily Mix", root.GetProperty("card0").GetProperty("title").GetString());
        Assert.Equal("Liked Songs", root.GetProperty("card1").GetProperty("title").GetString());
        Assert.Equal("24 songs", root.GetProperty("card1").GetProperty("subtitle").GetString());
        Assert.Equal("Late Night Vibes", root.GetProperty("card2").GetProperty("title").GetString());
        Assert.Equal("Quick Radio", root.GetProperty("card3").GetProperty("title").GetString());
    }

    [Theory]
    [InlineData(WidgetDefinitions.MiniPlayer)]
    [InlineData(WidgetDefinitions.RecommendedPlaylists)]
    [InlineData(WidgetDefinitions.MusicHub)]
    public void BuildDataJson_PlayingState_ProducesLiveTrackProgressAndPlaylists(string definitionId)
    {
        var playingSnapshot = new WidgetStateSnapshot
        {
            Playback = new WidgetPlaybackSnapshot
            {
                TrackId = "track-42",
                Title = "Midnight City",
                Artist = "M83",
                Album = "Hurry Up, We're Dreaming",
                ArtworkUri = "https://example.com/art.jpg",
                IsPlaying = true,
                IsLiked = true,
                PositionMs = 90_000,
                DurationMs = 180_000,
                QueueSize = 12
            },
            Recommendations =
            [
                new WidgetPlaylistItem { Id = "p1", Title = "Synthwave Drive", Subtitle = "18 songs", TrackCount = 18, SourceType = WidgetPlaylistSourceType.UserPlaylist },
                new WidgetPlaylistItem { Id = "p2", Title = "Liked Songs", Subtitle = "42 songs", TrackCount = 42, SourceType = WidgetPlaylistSourceType.LikedSongs },
                new WidgetPlaylistItem { Id = "p3", Title = "Focus Beats", Subtitle = "Smart Mix", SourceType = WidgetPlaylistSourceType.SmartPlaylist },
                new WidgetPlaylistItem { Id = "p4", Title = "M83 Radio", Subtitle = "Quick Mix", SourceType = WidgetPlaylistSourceType.RadioStation }
            ],
            LikedSongsCount = 42
        };

        string dataJson = WidgetAdaptiveCardBuilder.BuildDataJson(definitionId, playingSnapshot);
        using var doc = JsonDocument.Parse(dataJson);
        var root = doc.RootElement;

        Assert.True(root.GetProperty("hasTrack").GetBoolean());
        Assert.True(root.GetProperty("isPlaying").GetBoolean());
        Assert.True(root.GetProperty("isLiked").GetBoolean());
        Assert.Equal("Midnight City", root.GetProperty("trackTitle").GetString());
        Assert.Equal("M83", root.GetProperty("trackArtist").GetString());
        Assert.Equal("https://example.com/art.jpg", root.GetProperty("artworkUrl").GetString());
        Assert.Equal("⏸", root.GetProperty("playPauseLabel").GetString());
        Assert.Equal("♥", root.GetProperty("likeLabel").GetString());
        Assert.Equal("1:30", root.GetProperty("elapsedText").GetString());
        Assert.Equal("3:00", root.GetProperty("durationText").GetString());
        Assert.Equal(50, root.GetProperty("progressPercent").GetInt32());
        Assert.Equal(50, root.GetProperty("progressFilledWeight").GetInt32());
        Assert.Equal(50, root.GetProperty("progressRemainingWeight").GetInt32());
        Assert.Equal("Synthwave Drive", root.GetProperty("card0").GetProperty("title").GetString());
        Assert.Equal("M83 Radio", root.GetProperty("card3").GetProperty("title").GetString());
    }

    [Fact]
    public void Curator_InterleavesSourcesDeduplicatesAndRotatesWindow()
    {
        var userPlaylists = new List<UserPlaylist>
        {
            new() { Id = "u1", Title = "Road Trip", Tracks = [new TrackItem { Id = "t1", Title = "Song 1" }] },
            new() { Id = "u2", Title = "Workout", Tracks = [new TrackItem { Id = "t2", Title = "Song 2" }, new TrackItem { Id = "t3", Title = "Song 3" }] }
        };
        var madeForYou = new List<RecommendedPlaylistItem>
        {
            new("m1", "Discover Mix", "For You", "https://example.com/m1.jpg", "PL_M1")
        };
        var radios = new List<RadioStationItem>
        {
            new("r1", "Indie Radio", "Quick Mix", "https://example.com/r1.jpg", "RD_1")
        };

        var curated = WidgetRecommendationCurator.Curate(
            userPlaylists: userPlaylists,
            madeForYou: madeForYou,
            radioStations: radios,
            likedCount: 15);

        Assert.True(curated.Count >= 5);
        Assert.Contains(curated, c => c.Title == "Discover Mix");
        Assert.Contains(curated, c => c.Title == "Workout");
        Assert.Contains(curated, c => c.Title == "Liked Songs" && c.TrackCount == 15);

        var window0 = WidgetRecommendationCurator.SelectWindow(curated, offset: 0, count: 4);
        var window1 = WidgetRecommendationCurator.SelectWindow(curated, offset: 4, count: 4);
        Assert.Equal(4, window0.Count);
        Assert.Equal(4, window1.Count);
        Assert.NotEqual(window0[0].Id, window1[0].Id);
    }

    [Fact]
    public void StateFileStore_AndCommandMessage_RoundTripCleanly()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"xperience_widget_test_{Guid.NewGuid():N}.json");
        try
        {
            var store = new WidgetStateFileStore(tempFile);
            var original = new WidgetStateSnapshot
            {
                Playback = new WidgetPlaybackSnapshot
                {
                    TrackId = "t-99",
                    Title = "Starlight",
                    Artist = "Muse",
                    IsPlaying = true,
                    PositionMs = 45_000,
                    DurationMs = 240_000
                },
                LikedSongsCount = 7,
                RotationOffset = 4
            };

            store.Save(original);
            var loaded = store.Load();

            Assert.Equal("t-99", loaded.Playback.TrackId);
            Assert.Equal("Starlight", loaded.Playback.Title);
            Assert.True(loaded.Playback.IsPlaying);
            Assert.Equal(7, loaded.LikedSongsCount);
            Assert.Equal(4, loaded.RotationOffset);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }

        var cmd = new WidgetCommandMessage
        {
            Verb = WidgetActionVerbs.PlayPlaylist,
            PlaylistId = "pl-123",
            PlaylistTitle = "Late Night Vibes",
            SourceType = WidgetPlaylistSourceType.SmartPlaylist
        };

        string encoded = cmd.ToLaunchArgumentValue();
        var decoded = WidgetCommandMessage.FromLaunchArgumentValue(encoded);
        Assert.NotNull(decoded);
        Assert.Equal(WidgetActionVerbs.PlayPlaylist, decoded!.Verb);
        Assert.Equal("pl-123", decoded.PlaylistId);
        Assert.Equal(WidgetPlaylistSourceType.SmartPlaylist, decoded.SourceType);
    }
}
