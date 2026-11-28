using System.Text.Json;
using System.Text.Json.Nodes;

namespace Xperience.Core.Widgets;

/// <summary>
/// Builds Windows 11 Widgets Board Adaptive Cards v1.5 templates and dynamic data JSON payloads
/// for all 3 Xperience widgets (<see cref="WidgetDefinitions.MiniPlayer"/>,
/// <see cref="WidgetDefinitions.RecommendedPlaylists"/>, and <see cref="WidgetDefinitions.MusicHub"/>).
/// </summary>
public static class WidgetAdaptiveCardBuilder
{
    public const string AdaptiveCardSchemaUrl = "http://adaptivecards.io/schemas/adaptive-card.json";
    public const string AdaptiveCardVersion = "1.5";
    public const string DefaultBrandArtworkUrl = "ms-appx:///Assets/Branding/xperience_icon_round_512.png";

    private static readonly JsonSerializerOptions CompactJsonOptions = new()
    {
        WriteIndented = false
    };

    /// <summary>
    /// Returns the Adaptive Card v1.5 template JSON for the specified widget definition ID.
    /// </summary>
    public static string GetTemplateJson(string definitionId)
    {
        return definitionId switch
        {
            WidgetDefinitions.MiniPlayer => BuildMiniPlayerTemplate(),
            WidgetDefinitions.RecommendedPlaylists => BuildRecommendedPlaylistsTemplate(),
            WidgetDefinitions.MusicHub => BuildMusicHubTemplate(),
            _ => BuildMiniPlayerTemplate()
        };
    }

    /// <summary>
    /// Builds the dynamic data JSON payload bound to the Adaptive Card template for the specified widget.
    /// </summary>
    public static string BuildDataJson(string definitionId, WidgetStateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var playback = snapshot.Playback ?? new WidgetPlaybackSnapshot();
        bool hasTrack = playback.HasTrack;
        int progressPercent = playback.ProgressPercent;
        // Adaptive Card Column widths must be >= 1 to avoid zero-weight layout collapse.
        int filledWeight = hasTrack ? Math.Clamp(progressPercent, 1, 100) : 1;
        int remainingWeight = Math.Max(1, 100 - filledWeight);

        string artworkUrl = ResolveCardImageUrl(playback.ArtworkUri);
        string trackTitle = hasTrack ? playback.Title.Trim() : "Xperience Music";
        string trackArtist = hasTrack
            ? (!string.IsNullOrWhiteSpace(playback.Artist) ? playback.Artist.Trim() : "Xperience Music")
            : "Tap to play music";
        string playPauseLabel = playback.IsPlaying ? "⏸" : "▶";
        string playPauseTooltip = playback.IsPlaying ? "Pause" : "Play";
        string likeLabel = playback.IsLiked ? "♥" : "♡";
        string likeTooltip = playback.IsLiked ? "Remove from Liked Songs" : "Add to Liked Songs";
        string likeStyle = playback.IsLiked ? "positive" : "default";

        var data = new JsonObject
        {
            ["widgetId"] = definitionId,
            ["hasTrack"] = hasTrack,
            ["isPlaying"] = playback.IsPlaying,
            ["isLiked"] = playback.IsLiked,
            ["headerLabel"] = "NOW PLAYING",
            ["brandMark"] = "✦",
            ["trackTitle"] = trackTitle,
            ["trackArtist"] = trackArtist,
            ["artworkUrl"] = artworkUrl,
            ["elapsedText"] = hasTrack ? playback.FormattedPosition : "0:00",
            ["durationText"] = hasTrack ? playback.FormattedDuration : "0:00",
            ["progressPercent"] = progressPercent,
            ["progressFilledWeight"] = filledWeight,
            ["progressRemainingWeight"] = remainingWeight,
            ["playPauseLabel"] = playPauseLabel,
            ["playPauseTooltip"] = playPauseTooltip,
            ["likeLabel"] = likeLabel,
            ["likeTooltip"] = likeTooltip,
            ["likeStyle"] = likeStyle,
            ["ambientLabel"] = "✦ Ambient",
            ["recommendedHeader"] = "Recommended for you",
            ["refreshLabel"] = "↻"
        };

        var cards = WidgetRecommendationCurator.SelectWindow(
            snapshot.Recommendations,
            snapshot.RotationOffset,
            count: 4,
            likedCount: snapshot.LikedSongsCount);

        for (int i = 0; i < 4; i++)
        {
            var card = cards[i];
            data[$"card{i}"] = new JsonObject
            {
                ["id"] = card.Id,
                ["title"] = card.Title,
                ["subtitle"] = card.DisplaySubtitle,
                ["artworkUrl"] = ResolveCardImageUrl(card.ArtworkUri),
                ["sourceType"] = card.SourceType.ToString(),
                ["playlistId"] = card.PlaylistId ?? string.Empty,
                ["browseId"] = card.BrowseId ?? string.Empty,
                ["seedVideoId"] = card.SeedVideoId ?? string.Empty,
                ["params"] = card.Params ?? string.Empty
            };
        }

        return data.ToJsonString(CompactJsonOptions);
    }

    private static string ResolveCardImageUrl(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
        {
            return DefaultBrandArtworkUrl;
        }

        string trimmed = uri.Trim();
        if (trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("ms-appx:///", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        return DefaultBrandArtworkUrl;
    }

    private static string BuildMiniPlayerTemplate()
    {
        var card = CreateBaseCard();
        var body = new JsonArray
        {
            BuildNowPlayingHeaderAndTrackRow(includeAmbientPill: false),
            BuildProgressBarRow(showTimestamps: true),
            BuildTransportControlsRow()
        };
        card["body"] = body;
        return card.ToJsonString(CompactJsonOptions);
    }

    private static string BuildRecommendedPlaylistsTemplate()
    {
        var card = CreateBaseCard();
        var body = new JsonArray
        {
            BuildRecommendedHeaderRow(showBrandMark: true),
            BuildPlaylistGridRow(0, 1),
            BuildPlaylistGridRow(2, 3)
        };
        card["body"] = body;
        return card.ToJsonString(CompactJsonOptions);
    }

    private static string BuildMusicHubTemplate()
    {
        var card = CreateBaseCard();
        var body = new JsonArray
        {
            BuildMusicHubTopHeaderRow(),
            BuildTrackMetadataRow(),
            BuildProgressAndCompactControlsRow(),
            BuildRecommendedHeaderRow(showBrandMark: false, separator: true),
            BuildPlaylistGridRow(0, 1),
            BuildPlaylistGridRow(2, 3)
        };
        card["body"] = body;
        return card.ToJsonString(CompactJsonOptions);
    }

    private static JsonObject CreateBaseCard()
    {
        return new JsonObject
        {
            ["type"] = "AdaptiveCard",
            ["$schema"] = AdaptiveCardSchemaUrl,
            ["version"] = AdaptiveCardVersion
        };
    }

    private static JsonObject BuildMusicHubTopHeaderRow()
    {
        return new JsonObject
        {
            ["type"] = "ColumnSet",
            ["spacing"] = "None",
            ["columns"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "Column",
                    ["width"] = "stretch",
                    ["verticalContentAlignment"] = "Center",
                    ["items"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "TextBlock",
                            ["text"] = "${brandMark} ${headerLabel}",
                            ["size"] = "Small",
                            ["weight"] = "Bolder",
                            ["color"] = "Accent",
                            ["maxLines"] = 1
                        }
                    }
                },
                new JsonObject
                {
                    ["type"] = "Column",
                    ["width"] = "auto",
                    ["verticalContentAlignment"] = "Center",
                    ["items"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "ActionSet",
                            ["actions"] = new JsonArray
                            {
                                new JsonObject
                                {
                                    ["type"] = "Action.Execute",
                                    ["title"] = "${ambientLabel}",
                                    ["tooltip"] = "Open Ambient Mode",
                                    ["verb"] = WidgetActionVerbs.OpenAmbient
                                }
                            }
                        }
                    }
                }
            }
        };
    }

    private static JsonObject BuildNowPlayingHeaderAndTrackRow(bool includeAmbientPill)
    {
        var middleItems = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "TextBlock",
                ["text"] = "${brandMark} ${headerLabel}",
                ["size"] = "Small",
                ["weight"] = "Bolder",
                ["color"] = "Accent",
                ["spacing"] = "None",
                ["maxLines"] = 1
            },
            new JsonObject
            {
                ["type"] = "TextBlock",
                ["text"] = "${trackTitle}",
                ["size"] = "Medium",
                ["weight"] = "Bolder",
                ["spacing"] = "None",
                ["wrap"] = false,
                ["maxLines"] = 1
            },
            new JsonObject
            {
                ["type"] = "TextBlock",
                ["text"] = "${trackArtist}",
                ["size"] = "Small",
                ["isSubtle"] = true,
                ["spacing"] = "None",
                ["wrap"] = false,
                ["maxLines"] = 1
            }
        };

        var columns = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "Column",
                ["width"] = "auto",
                ["verticalContentAlignment"] = "Center",
                ["selectAction"] = new JsonObject
                {
                    ["type"] = "Action.Execute",
                    ["title"] = "Open Xperience",
                    ["verb"] = WidgetActionVerbs.OpenApp
                },
                ["items"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "Image",
                        ["url"] = "${artworkUrl}",
                        ["size"] = "Medium",
                        ["style"] = "Default",
                        ["width"] = "56px",
                        ["height"] = "56px",
                        ["altText"] = "${trackTitle}"
                    }
                }
            },
            new JsonObject
            {
                ["type"] = "Column",
                ["width"] = "stretch",
                ["verticalContentAlignment"] = "Center",
                ["selectAction"] = new JsonObject
                {
                    ["type"] = "Action.Execute",
                    ["title"] = "Open Xperience",
                    ["verb"] = WidgetActionVerbs.OpenApp
                },
                ["items"] = middleItems
            },
            new JsonObject
            {
                ["type"] = "Column",
                ["width"] = "auto",
                ["verticalContentAlignment"] = "Center",
                ["items"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "ActionSet",
                        ["actions"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["type"] = "Action.Execute",
                                ["title"] = "${likeLabel}",
                                ["tooltip"] = "${likeTooltip}",
                                ["style"] = "${likeStyle}",
                                ["verb"] = WidgetActionVerbs.ToggleLike
                            }
                        }
                    }
                }
            }
        };

        if (includeAmbientPill)
        {
            columns.Add(new JsonObject
            {
                ["type"] = "Column",
                ["width"] = "auto",
                ["verticalContentAlignment"] = "Center",
                ["items"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "ActionSet",
                        ["actions"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["type"] = "Action.Execute",
                                ["title"] = "${ambientLabel}",
                                ["verb"] = WidgetActionVerbs.OpenAmbient
                            }
                        }
                    }
                }
            });
        }

        return new JsonObject
        {
            ["type"] = "ColumnSet",
            ["spacing"] = "Small",
            ["columns"] = columns
        };
    }

    private static JsonObject BuildTrackMetadataRow()
    {
        return new JsonObject
        {
            ["type"] = "ColumnSet",
            ["spacing"] = "Small",
            ["columns"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "Column",
                    ["width"] = "auto",
                    ["verticalContentAlignment"] = "Center",
                    ["selectAction"] = new JsonObject
                    {
                        ["type"] = "Action.Execute",
                        ["title"] = "Open Xperience",
                        ["verb"] = WidgetActionVerbs.OpenApp
                    },
                    ["items"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "Image",
                            ["url"] = "${artworkUrl}",
                            ["width"] = "52px",
                            ["height"] = "52px",
                            ["altText"] = "${trackTitle}"
                        }
                    }
                },
                new JsonObject
                {
                    ["type"] = "Column",
                    ["width"] = "stretch",
                    ["verticalContentAlignment"] = "Center",
                    ["selectAction"] = new JsonObject
                    {
                        ["type"] = "Action.Execute",
                        ["title"] = "Open Xperience",
                        ["verb"] = WidgetActionVerbs.OpenApp
                    },
                    ["items"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "TextBlock",
                            ["text"] = "${trackTitle}",
                            ["size"] = "Medium",
                            ["weight"] = "Bolder",
                            ["spacing"] = "None",
                            ["maxLines"] = 1
                        },
                        new JsonObject
                        {
                            ["type"] = "TextBlock",
                            ["text"] = "${trackArtist}",
                            ["size"] = "Small",
                            ["isSubtle"] = true,
                            ["spacing"] = "None",
                            ["maxLines"] = 1
                        }
                    }
                },
                new JsonObject
                {
                    ["type"] = "Column",
                    ["width"] = "auto",
                    ["verticalContentAlignment"] = "Center",
                    ["items"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "ActionSet",
                            ["actions"] = new JsonArray
                            {
                                new JsonObject
                                {
                                    ["type"] = "Action.Execute",
                                    ["title"] = "${likeLabel}",
                                    ["tooltip"] = "${likeTooltip}",
                                    ["style"] = "${likeStyle}",
                                    ["verb"] = WidgetActionVerbs.ToggleLike
                                }
                            }
                        }
                    }
                }
            }
        };
    }

    private static JsonObject BuildProgressBarRow(bool showTimestamps)
    {
        var bar = new JsonObject
        {
            ["type"] = "ColumnSet",
            ["spacing"] = "None",
            ["columns"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "Column",
                    ["width"] = "${progressFilledWeight}",
                    ["style"] = "accent",
                    ["minHeight"] = "4px",
                    ["items"] = new JsonArray()
                },
                new JsonObject
                {
                    ["type"] = "Column",
                    ["width"] = "${progressRemainingWeight}",
                    ["style"] = "emphasis",
                    ["minHeight"] = "4px",
                    ["items"] = new JsonArray()
                }
            }
        };

        if (!showTimestamps)
        {
            return bar;
        }

        return new JsonObject
        {
            ["type"] = "ColumnSet",
            ["spacing"] = "Small",
            ["columns"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "Column",
                    ["width"] = "auto",
                    ["verticalContentAlignment"] = "Center",
                    ["items"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "TextBlock",
                            ["text"] = "${elapsedText}",
                            ["size"] = "Small",
                            ["isSubtle"] = true
                        }
                    }
                },
                new JsonObject
                {
                    ["type"] = "Column",
                    ["width"] = "stretch",
                    ["verticalContentAlignment"] = "Center",
                    ["items"] = new JsonArray { bar }
                },
                new JsonObject
                {
                    ["type"] = "Column",
                    ["width"] = "auto",
                    ["verticalContentAlignment"] = "Center",
                    ["items"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "TextBlock",
                            ["text"] = "${durationText}",
                            ["size"] = "Small",
                            ["isSubtle"] = true
                        }
                    }
                }
            }
        };
    }

    private static JsonObject BuildTransportControlsRow()
    {
        return new JsonObject
        {
            ["type"] = "ActionSet",
            ["spacing"] = "Small",
            ["horizontalAlignment"] = "Center",
            ["actions"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "Action.Execute",
                    ["title"] = "⏮",
                    ["tooltip"] = "Previous Track",
                    ["verb"] = WidgetActionVerbs.Previous
                },
                new JsonObject
                {
                    ["type"] = "Action.Execute",
                    ["title"] = "${playPauseLabel}",
                    ["tooltip"] = "${playPauseTooltip}",
                    ["style"] = "positive",
                    ["verb"] = WidgetActionVerbs.PlayPause
                },
                new JsonObject
                {
                    ["type"] = "Action.Execute",
                    ["title"] = "⏭",
                    ["tooltip"] = "Next Track",
                    ["verb"] = WidgetActionVerbs.Next
                }
            }
        };
    }

    private static JsonObject BuildProgressAndCompactControlsRow()
    {
        return new JsonObject
        {
            ["type"] = "ColumnSet",
            ["spacing"] = "Small",
            ["columns"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "Column",
                    ["width"] = "stretch",
                    ["verticalContentAlignment"] = "Center",
                    ["items"] = new JsonArray
                    {
                        BuildProgressBarRow(showTimestamps: false),
                        new JsonObject
                        {
                            ["type"] = "TextBlock",
                            ["text"] = "${elapsedText} / ${durationText}",
                            ["size"] = "Small",
                            ["isSubtle"] = true,
                            ["spacing"] = "Small"
                        }
                    }
                },
                new JsonObject
                {
                    ["type"] = "Column",
                    ["width"] = "auto",
                    ["verticalContentAlignment"] = "Center",
                    ["items"] = new JsonArray
                    {
                        BuildTransportControlsRow()
                    }
                }
            }
        };
    }

    private static JsonObject BuildRecommendedHeaderRow(bool showBrandMark, bool separator = false)
    {
        string titleBinding = showBrandMark
            ? "${brandMark} ${recommendedHeader}"
            : "${recommendedHeader}";

        var header = new JsonObject
        {
            ["type"] = "ColumnSet",
            ["spacing"] = separator ? "Medium" : "None",
            ["separator"] = separator,
            ["columns"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "Column",
                    ["width"] = "stretch",
                    ["verticalContentAlignment"] = "Center",
                    ["items"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "TextBlock",
                            ["text"] = titleBinding,
                            ["size"] = "Medium",
                            ["weight"] = "Bolder",
                            ["maxLines"] = 1
                        }
                    }
                },
                new JsonObject
                {
                    ["type"] = "Column",
                    ["width"] = "auto",
                    ["verticalContentAlignment"] = "Center",
                    ["items"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "ActionSet",
                            ["actions"] = new JsonArray
                            {
                                new JsonObject
                                {
                                    ["type"] = "Action.Execute",
                                    ["title"] = "${refreshLabel}",
                                    ["tooltip"] = "Refresh Recommendations",
                                    ["verb"] = WidgetActionVerbs.RefreshRecommendations
                                }
                            }
                        }
                    }
                }
            }
        };

        return header;
    }

    private static JsonObject BuildPlaylistGridRow(int leftIndex, int rightIndex)
    {
        return new JsonObject
        {
            ["type"] = "ColumnSet",
            ["spacing"] = "Small",
            ["columns"] = new JsonArray
            {
                BuildPlaylistCardColumn(leftIndex),
                BuildPlaylistCardColumn(rightIndex)
            }
        };
    }

    private static JsonObject BuildPlaylistCardColumn(int index)
    {
        string prefix = $"card{index}";
        return new JsonObject
        {
            ["type"] = "Column",
            ["width"] = "stretch",
            ["style"] = "emphasis",
            ["bleed"] = false,
            ["selectAction"] = new JsonObject
            {
                ["type"] = "Action.Execute",
                ["title"] = $"Play ${{{prefix}.title}}",
                ["verb"] = WidgetActionVerbs.PlayPlaylist,
                ["data"] = new JsonObject
                {
                    ["id"] = $"${{{prefix}.id}}",
                    ["title"] = $"${{{prefix}.title}}",
                    ["sourceType"] = $"${{{prefix}.sourceType}}",
                    ["playlistId"] = $"${{{prefix}.playlistId}}",
                    ["browseId"] = $"${{{prefix}.browseId}}",
                    ["seedVideoId"] = $"${{{prefix}.seedVideoId}}",
                    ["params"] = $"${{{prefix}.params}}"
                }
            },
            ["items"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "ColumnSet",
                    ["spacing"] = "None",
                    ["columns"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "Column",
                            ["width"] = "auto",
                            ["verticalContentAlignment"] = "Center",
                            ["items"] = new JsonArray
                            {
                                new JsonObject
                                {
                                    ["type"] = "Image",
                                    ["url"] = $"${{{prefix}.artworkUrl}}",
                                    ["width"] = "36px",
                                    ["height"] = "36px",
                                    ["altText"] = $"${{{prefix}.title}}"
                                }
                            }
                        },
                        new JsonObject
                        {
                            ["type"] = "Column",
                            ["width"] = "stretch",
                            ["verticalContentAlignment"] = "Center",
                            ["items"] = new JsonArray
                            {
                                new JsonObject
                                {
                                    ["type"] = "TextBlock",
                                    ["text"] = $"${{{prefix}.title}}",
                                    ["weight"] = "Bolder",
                                    ["size"] = "Small",
                                    ["wrap"] = true,
                                    ["maxLines"] = 2,
                                    ["spacing"] = "None"
                                },
                                new JsonObject
                                {
                                    ["type"] = "TextBlock",
                                    ["text"] = $"${{{prefix}.subtitle}}",
                                    ["size"] = "Small",
                                    ["isSubtle"] = true,
                                    ["wrap"] = false,
                                    ["maxLines"] = 1,
                                    ["spacing"] = "None"
                                }
                            }
                        }
                    }
                }
            }
        };
    }
}
