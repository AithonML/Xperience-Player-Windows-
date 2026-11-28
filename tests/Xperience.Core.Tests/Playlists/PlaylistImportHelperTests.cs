using System.Text.Json;
using Xperience.Core.Domain.Entities;
using Xperience.Core.Domain.Enums;
using Xperience.Core.Domain.Models;
using Xperience.Core.Services;
using Xunit;

namespace Xperience.Core.Tests.Playlists;

public sealed class PlaylistImportHelperTests
{
    [Fact]
    public void UserPlaylist_JsonRoundTrip_PreservesYouTubeMusicFields_AndSupportsLegacyPayload()
    {
        var imported = new UserPlaylist
        {
            Id = "pl_1234",
            Title = "Workout Beats",
            Description = "High tempo mix",
            CustomCoverImagePath = "https://img.example/workout.jpg",
            Source = UserPlaylist.YouTubeMusicSource,
            ExternalPlaylistId = "PLworkout99",
            ExternalBrowseId = "VLPLworkout99",
            SourceAccountLabel = "user@example.com",
            ImportedAt = "2026-11-24T08:00:00.0000000+00:00",
            Tracks = [CreateTrack("vidA", "Track A")]
        };

        string json = JsonSerializer.Serialize(imported);
        var deserialized = JsonSerializer.Deserialize<UserPlaylist>(json);

        Assert.NotNull(deserialized);
        Assert.Equal("pl_1234", deserialized!.Id);
        Assert.Equal(UserPlaylist.YouTubeMusicSource, deserialized.Source);
        Assert.Equal("PLworkout99", deserialized.ExternalPlaylistId);
        Assert.Equal("VLPLworkout99", deserialized.ExternalBrowseId);
        Assert.Equal("user@example.com", deserialized.SourceAccountLabel);
        Assert.Equal("2026-11-24T08:00:00.0000000+00:00", deserialized.ImportedAt);
        Assert.True(deserialized.IsFromYouTubeMusic);

        // Legacy UserPlaylists_v1 JSON without any new fields must deserialize cleanly as local playlist.
        const string legacyJson = """
        {
          "Id": "pl_legacy",
          "Title": "Local Mix",
          "Description": "Offline favorites",
          "CustomCoverImagePath": "",
          "Tracks": []
        }
        """;

        var legacy = JsonSerializer.Deserialize<UserPlaylist>(legacyJson);
        Assert.NotNull(legacy);
        Assert.Equal("pl_legacy", legacy!.Id);
        Assert.Equal(string.Empty, legacy.Source);
        Assert.Equal(string.Empty, legacy.ExternalPlaylistId);
        Assert.Equal(string.Empty, legacy.ExternalBrowseId);
        Assert.False(legacy.IsFromYouTubeMusic);
    }

    [Fact]
    public void UpsertYouTubeMusicPlaylist_CreatesNewImportedPlaylistAtIndexZero_AndDeduplicatesTracks()
    {
        var existingLocal = new UserPlaylist
        {
            Id = "pl_local_1",
            Title = "My Local Playlist"
        };
        var list = new List<UserPlaylist> { existingLocal };

        var source = new YouTubeMusicLibraryPlaylist(
            PlaylistId: "VLPLroadtrip1",
            BrowseId: "VLPLroadtrip1",
            Title: "Road Trip",
            Subtitle: "Playlist • 2 songs",
            ArtworkUri: "https://img.example/roadtrip.jpg");

        var timestamp = new DateTimeOffset(2026, 11, 24, 9, 30, 0, TimeSpan.Zero);
        var tracks = new[]
        {
            CreateTrack("abc11111111", "Song 1"),
            CreateTrack("abc11111111", "Song 1 Duplicate"),
            CreateTrack("def22222222", "Song 2")
        };

        var created = PlaylistImportHelper.UpsertYouTubeMusicPlaylist(
            list,
            source,
            tracks,
            accountLabel: "Alex",
            description: "Highway classics",
            importedAt: timestamp);

        Assert.Equal(2, list.Count);
        Assert.Same(created, list[0]);
        Assert.StartsWith("pl_", created.Id, StringComparison.Ordinal);
        Assert.Equal("Road Trip", created.Title);
        Assert.Equal("Highway classics", created.Description);
        Assert.Equal("https://img.example/roadtrip.jpg", created.CustomCoverImagePath);
        Assert.Equal(UserPlaylist.YouTubeMusicSource, created.Source);
        Assert.Equal("PLroadtrip1", created.ExternalPlaylistId);
        Assert.Equal("VLPLroadtrip1", created.ExternalBrowseId);
        Assert.Equal("Alex", created.SourceAccountLabel);
        Assert.Equal(timestamp.ToString("O"), created.ImportedAt);
        Assert.True(created.IsFromYouTubeMusic);
        Assert.Equal(2, created.Tracks.Count);
        Assert.Equal("ytm_abc11111111", created.Tracks[0].Id);
        Assert.Equal("ytm_def22222222", created.Tracks[1].Id);
    }

    [Fact]
    public void UpsertYouTubeMusicPlaylist_ReimportUpdatesMetadataAndTracksWithoutDuplicating_AndPreservesLocalCover()
    {
        var originalCreatedAt = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        var existingImported = new UserPlaylist
        {
            Id = "pl_keep_this_id",
            Title = "Old Title",
            Description = "Old Description",
            CustomCoverImagePath = @"C:\Users\Alex\Pictures\custom_cover.png",
            CreatedAt = originalCreatedAt,
            Source = UserPlaylist.YouTubeMusicSource,
            ExternalPlaylistId = "PLsync01",
            ExternalBrowseId = "VLPLsync01",
            Tracks = [CreateTrack("old11111111", "Old Song")]
        };

        var list = new List<UserPlaylist> { existingImported };

        var refreshedSource = new YouTubeMusicLibraryPlaylist(
            PlaylistId: "plsync01",
            BrowseId: "VLplsync01",
            Title: "Updated Title",
            Subtitle: "Updated Subtitle",
            ArtworkUri: "https://img.example/new_remote_cover.jpg");

        var refreshedAt = new DateTimeOffset(2026, 11, 24, 10, 0, 0, TimeSpan.Zero);
        var updated = PlaylistImportHelper.UpsertYouTubeMusicPlaylist(
            list,
            refreshedSource,
            [CreateTrack("new22222222", "New Song 1"), CreateTrack("new33333333", "New Song 2")],
            accountLabel: "Alex@YT",
            description: "Updated Description",
            fallbackCoverUri: "https://img.example/header_cover.jpg",
            importedAt: refreshedAt);

        Assert.Single(list);
        Assert.Same(existingImported, updated);
        Assert.Equal("pl_keep_this_id", updated.Id);
        Assert.Equal(originalCreatedAt, updated.CreatedAt);
        Assert.Equal("Updated Title", updated.Title);
        Assert.Equal("Updated Description", updated.Description);
        // Preserves user's local disk cover path!
        Assert.Equal(@"C:\Users\Alex\Pictures\custom_cover.png", updated.CustomCoverImagePath);
        Assert.Equal("Alex@YT", updated.SourceAccountLabel);
        Assert.Equal(refreshedAt.ToString("O"), updated.ImportedAt);
        Assert.Equal(2, updated.Tracks.Count);
        Assert.Equal("ytm_new22222222", updated.Tracks[0].Id);
    }

    [Fact]
    public void UpsertYouTubeMusicPlaylist_ReimportUpdatesRemoteOrCachedCoverWhenNoUserLocalCoverSet()
    {
        var existingImported = new UserPlaylist
        {
            Id = "pl_remote_cover",
            Title = "Chill",
            CustomCoverImagePath = "https://img.example/old_cover.jpg",
            Source = UserPlaylist.YouTubeMusicSource,
            ExternalPlaylistId = "PLchill",
            ExternalBrowseId = "VLPLchill"
        };

        var list = new List<UserPlaylist> { existingImported };
        var source = new YouTubeMusicLibraryPlaylist(
            PlaylistId: "PLchill",
            BrowseId: "VLPLchill",
            Title: "Chill",
            ArtworkUri: "https://img.example/new_cover.jpg");

        var updated = PlaylistImportHelper.UpsertYouTubeMusicPlaylist(list, source, []);

        Assert.Equal("https://img.example/new_cover.jpg", updated.CustomCoverImagePath);
    }

    [Fact]
    public void IsAlreadyImported_MatchesNormalizedPlaylistIdCaseInsensitively()
    {
        var playlists = new List<UserPlaylist>
        {
            new()
            {
                Id = "pl_1",
                Title = "Local Only"
            },
            new()
            {
                Id = "pl_2",
                Title = "Imported",
                Source = UserPlaylist.YouTubeMusicSource,
                ExternalPlaylistId = "PLAbCdEf123"
            }
        };

        Assert.True(PlaylistImportHelper.IsAlreadyImported(playlists, "PLAbCdEf123"));
        Assert.True(PlaylistImportHelper.IsAlreadyImported(playlists, "VLplabcdef123"));
        Assert.False(PlaylistImportHelper.IsAlreadyImported(playlists, "PLother999"));
        Assert.False(PlaylistImportHelper.IsAlreadyImported(playlists, ""));
        Assert.False(PlaylistImportHelper.IsAlreadyImported(playlists, null));
    }

    private static TrackItem CreateTrack(string videoId, string title) => new()
    {
        Id = $"ytm_{videoId}",
        Title = title,
        Artist = "Test Artist",
        Album = "Test Album",
        Duration = TimeSpan.FromMinutes(3),
        AudioSourceUri = $"ytmusic://{videoId}",
        YouTubeVideoId = videoId,
        SourceType = MediaSourceType.Online
    };
}
