using Xperience.Core.Domain.Entities;
using Xperience.Core.Domain.Enums;
using Xperience.Core.Domain.Models;
using Xperience.Core.Playback;
using Xunit;

namespace Xperience.Core.Tests.Playback;

public sealed class PlaybackQueueControllerTests
{
    private static TrackItem MakeTrack(int index) => new()
    {
        Id = $"ytm_vid{index}",
        YouTubeVideoId = $"vid{index}",
        Title = $"Track {index}",
        Artist = $"Artist {index}",
        Album = "Album",
        SourceType = MediaSourceType.Online,
        AudioSourceUri = $"ytmusic://vid{index}",
        Duration = TimeSpan.FromMinutes(3)
    };

    [Fact]
    public void ShuffleThenUnshuffle_RestoresExactCanonicalOrder()
    {
        var controller = new PlaybackQueueController();
        var tracks = Enumerable.Range(1, 5).Select(MakeTrack).ToList();

        controller.SetQueue(tracks, startIndex: 1); // Active = Track 2, Upcoming = [3, 4, 5]
        bool shuffled = controller.ShuffleUpcoming(new Random(42));
        Assert.True(shuffled);

        bool unshuffled = controller.UnshuffleUpcoming();
        Assert.True(unshuffled || controller.GetUpcomingQueue().Select(t => t.Id).SequenceEqual(["ytm_vid3", "ytm_vid4", "ytm_vid5"]));

        Assert.Equal(["ytm_vid3", "ytm_vid4", "ytm_vid5"], controller.GetUpcomingQueue().Select(t => t.Id));
        Assert.Equal("ytm_vid2", controller.CurrentTrack?.Id);
    }

    [Fact]
    public void SetQueueShuffled_ThenUnshuffle_RestoresCanonicalWrapAround_EvenAfterReplaceCurrentTrack()
    {
        var controller = new PlaybackQueueController();
        var tracks = Enumerable.Range(1, 5).Select(MakeTrack).ToList();

        // Start shuffled directly from index 2 (Track 3)
        var first = controller.SetQueueShuffled(tracks, startCanonicalIndex: 2, new Random(17));
        Assert.Equal("ytm_vid3", first?.Id);

        // Simulate duration resolution replacing the current track instance
        var updatedCurrent = new TrackItem
        {
            Id = first!.Id,
            YouTubeVideoId = first.YouTubeVideoId,
            Title = first.Title,
            Artist = first.Artist,
            Album = first.Album,
            SourceType = first.SourceType,
            AudioSourceUri = first.AudioSourceUri,
            Duration = TimeSpan.FromSeconds(215)
        };
        Assert.True(controller.ReplaceCurrentTrack(updatedCurrent));

        controller.UnshuffleUpcoming();

        // Expected canonical wrap-around after Track 3: [Track 4, Track 5, Track 1, Track 2]
        Assert.Equal(
            ["ytm_vid4", "ytm_vid5", "ytm_vid1", "ytm_vid2"],
            controller.GetUpcomingQueue().Select(t => t.Id));
    }

    [Fact]
    public void UnshuffleUpcoming_PreservesPinnedPlayNextItemsFirst()
    {
        var controller = new PlaybackQueueController();
        var tracks = Enumerable.Range(1, 5).Select(MakeTrack).ToList();
        var pinned = MakeTrack(99);

        controller.SetQueue(tracks, startIndex: 0); // Current: 1
        controller.EnqueuePlayNext(pinned);
        controller.ShuffleUpcoming(new Random(99));
        controller.UnshuffleUpcoming();

        var upcomingIds = controller.GetUpcomingQueue().Select(t => t.Id).ToList();
        Assert.Equal("ytm_vid99", upcomingIds[0]);
        Assert.Equal(["ytm_vid2", "ytm_vid3", "ytm_vid4", "ytm_vid5"], upcomingIds.Skip(1));
    }

    [Fact]
    public void RestoreSession_RestoresQueueCurrentIndexAndCanonicalOrder()
    {
        var source = new PlaybackQueueController();
        var tracks = Enumerable.Range(1, 4).Select(MakeTrack).ToList();
        source.SetQueueShuffled(tracks, startCanonicalIndex: 1, new Random(7));
        var (items, currentIndex, canonical) = source.GetSessionSnapshot();

        var target = new PlaybackQueueController();
        var restored = target.RestoreSession(items, currentIndex, canonical);

        Assert.Equal("ytm_vid2", restored?.Id);
        Assert.Equal(currentIndex, target.CurrentIndex);

        target.UnshuffleUpcoming();
        Assert.Equal(["ytm_vid3", "ytm_vid4", "ytm_vid1"], target.GetUpcomingQueue().Select(t => t.Id));
    }

    [Fact]
    public void PlayerUiSettings_DefaultsVisualizerOff_WithBackdropOnly()
    {
        var defaults = new PlayerUiSettings();
        Assert.False(defaults.IsGpuVisualizerEnabled);
        Assert.Equal("BackdropOnly", defaults.SelectedVisualizerId);
        Assert.Equal("NeonTerrainPeaks", defaults.LastActiveVisualizerId);
    }

    [Fact]
    public void OfficialMusicVideoMetadata_MapsSongToVideoAndBackAcrossIntroAndInterlude()
    {
        // Video has 11s intro (0:00..0:11) before song begins at 0:00, and a 10s interlude at video 1:11..1:21
        var meta = new OfficialMusicVideoMetadata(
            SongVideoId: "song123",
            OfficialVideoId: "omv456",
            Segments:
            [
                new MusicVideoTimeSegment(SongStartMs: 0, VideoStartMs: 11_000, DurationMs: 60_000),
                new MusicVideoTimeSegment(SongStartMs: 60_000, VideoStartMs: 81_000, DurationMs: 60_000)
            ]);

        Assert.Equal(23, meta.MapSongToVideoPosition(TimeSpan.FromSeconds(12)).TotalSeconds, precision: 2);
        Assert.Equal(91, meta.MapSongToVideoPosition(TimeSpan.FromSeconds(70)).TotalSeconds, precision: 2);

        // Reverse mapping (video intro skit -> song 0:00, video interlude -> 1:00, post-interlude 1:31 -> song 1:10)
        Assert.Equal(0, meta.MapVideoToSongPosition(TimeSpan.FromSeconds(5)).TotalSeconds, precision: 2);
        Assert.Equal(60, meta.MapVideoToSongPosition(TimeSpan.FromSeconds(75)).TotalSeconds, precision: 2);
        Assert.Equal(70, meta.MapVideoToSongPosition(TimeSpan.FromSeconds(91)).TotalSeconds, precision: 2);
    }
}
