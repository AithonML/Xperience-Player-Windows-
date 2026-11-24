using System.Text.Json;
using Xperience.Core.Domain.Enums;
using Xperience.Core.Services;
using Xunit;

namespace Xperience.Core.Tests.Playlists;

public sealed class YouTubePlaylistImportParserTests
{
    [Theory]
    [InlineData("VLPL1234567890", "PL1234567890")]
    [InlineData("vlLM", "LM")]
    [InlineData("  VLPLabc_123  ", "PLabc_123")]
    [InlineData("PLalreadyClean", "PLalreadyClean")]
    [InlineData("LM", "LM")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData(null, "")]
    public void NormalizePlaylistId_StripsLeadingVlAndTrims(string? raw, string expected)
    {
        Assert.Equal(expected, YouTubePlaylistImportParser.NormalizePlaylistId(raw));
    }

    [Theory]
    [InlineData("VLPL123", null, "VLPL123")]
    [InlineData("vlPL123", null, "VLPL123")]
    [InlineData("PL123", null, "VLPL123")]
    [InlineData(null, "PL456", "VLPL456")]
    [InlineData("", "VLLM", "VLLM")]
    [InlineData("", "", "")]
    [InlineData(null, null, "")]
    public void NormalizeBrowseId_EnsuresVlPrefix(string? browseId, string? playlistId, string expected)
    {
        Assert.Equal(expected, YouTubePlaylistImportParser.NormalizeBrowseId(browseId, playlistId));
    }

    [Fact]
    public void ParseLibraryPlaylistsResponse_TwoRowGrid_FiltersActionTilesAndPodcasts_AcceptsLikedMusicAndPlaylists()
    {
        const string json = """
        {
          "contents": {
            "singleColumnBrowseResultsRenderer": {
              "tabs": [
                {
                  "tabRenderer": {
                    "content": {
                      "sectionListRenderer": {
                        "contents": [
                          {
                            "gridRenderer": {
                              "items": [
                                {
                                  "musicTwoRowItemRenderer": {
                                    "title": { "runs": [{ "text": "New playlist" }] },
                                    "navigationEndpoint": {
                                      "createPlaylistServiceEndpoint": { "params": "CAESAA==" }
                                    }
                                  }
                                },
                                {
                                  "musicTwoRowItemRenderer": {
                                    "title": { "runs": [{ "text": "Liked Music" }] },
                                    "subtitle": {
                                      "runs": [
                                        { "text": "Auto playlist" },
                                        { "text": " • " },
                                        { "text": "142 songs" }
                                      ]
                                    },
                                    "navigationEndpoint": {
                                      "browseEndpoint": {
                                        "browseId": "VLLM",
                                        "browseEndpointContextSupportedConfigs": {
                                          "browseEndpointContextMusicConfig": {
                                            "pageType": "MUSIC_PAGE_TYPE_PLAYLIST"
                                          }
                                        }
                                      }
                                    },
                                    "thumbnailRenderer": {
                                      "musicThumbnailRenderer": {
                                        "thumbnail": {
                                          "thumbnails": [
                                            { "url": "https://img.example/lm_120.jpg", "width": 120, "height": 120 },
                                            { "url": "https://img.example/lm_544.jpg", "width": 544, "height": 544 }
                                          ]
                                        }
                                      }
                                    }
                                  }
                                },
                                {
                                  "musicTwoRowItemRenderer": {
                                    "title": { "runs": [{ "text": "Episodes for Later" }] },
                                    "subtitle": { "runs": [{ "text": "Auto playlist" }] },
                                    "navigationEndpoint": {
                                      "browseEndpoint": {
                                        "browseId": "VLSE",
                                        "browseEndpointContextSupportedConfigs": {
                                          "browseEndpointContextMusicConfig": {
                                            "pageType": "MUSIC_PAGE_TYPE_PLAYLIST"
                                          }
                                        }
                                      }
                                    }
                                  }
                                },
                                {
                                  "musicTwoRowItemRenderer": {
                                    "title": { "runs": [{ "text": "Tech Talk Podcast" }] },
                                    "navigationEndpoint": {
                                      "browseEndpoint": {
                                        "browseId": "VLPLpodcast123",
                                        "browseEndpointContextSupportedConfigs": {
                                          "browseEndpointContextMusicConfig": {
                                            "pageType": "MUSIC_PAGE_TYPE_PODCAST_SHOW_DETAIL_PAGE"
                                          }
                                        }
                                      }
                                    }
                                  }
                                },
                                {
                                  "musicTwoRowItemRenderer": {
                                    "title": {
                                      "runs": [
                                        {
                                          "text": "Late Night Synthwave",
                                          "navigationEndpoint": {
                                            "browseEndpoint": {
                                              "browseId": "VLPLsynth001"
                                            }
                                          }
                                        }
                                      ]
                                    },
                                    "subtitle": {
                                      "runs": [
                                        { "text": "Playlist" },
                                        { "text": " • " },
                                        { "text": "Alex Rivera" },
                                        { "text": " • " },
                                        { "text": "38 songs" }
                                      ]
                                    },
                                    "thumbnailRenderer": {
                                      "musicThumbnailRenderer": {
                                        "thumbnail": {
                                          "thumbnails": [
                                            { "url": "//lh3.googleusercontent.com/synth_small", "width": 226, "height": 226 },
                                            { "url": "//lh3.googleusercontent.com/synth_large", "width": 600, "height": 600 }
                                          ]
                                        }
                                      }
                                    }
                                  }
                                },
                                {
                                  "musicTwoRowItemRenderer": {
                                    "title": { "runs": [{ "text": "Late Night Synthwave (Dup)" }] },
                                    "navigationEndpoint": {
                                      "browseEndpoint": {
                                        "browseId": "vlPLSYNTH001"
                                      }
                                    }
                                  }
                                },
                                {
                                  "musicTwoRowItemRenderer": {
                                    "title": { "runs": [{ "text": "Overlay Only Playlist" }] },
                                    "subtitle": { "runs": [{ "text": "12 tracks" }] },
                                    "thumbnailOverlay": {
                                      "musicItemThumbnailOverlayRenderer": {
                                        "content": {
                                          "musicPlayButtonRenderer": {
                                            "playNavigationEndpoint": {
                                              "watchPlaylistEndpoint": {
                                                "playlistId": "PLoverlay99"
                                              }
                                            }
                                          }
                                        }
                                      }
                                    }
                                  }
                                }
                              ],
                              "continuations": [
                                {
                                  "nextContinuationData": {
                                    "continuation": "GRID_TOKEN_PAGE_2"
                                  }
                                }
                              ]
                            }
                          }
                        ]
                      }
                    }
                  }
                }
              ]
            }
          }
        }
        """;

        using var doc = JsonDocument.Parse(json);
        var (playlists, continuationToken) = YouTubePlaylistImportParser.ParseLibraryPlaylistsResponse(doc.RootElement);

        Assert.Equal("GRID_TOKEN_PAGE_2", continuationToken);
        Assert.Equal(3, playlists.Count);

        Assert.Equal("LM", playlists[0].PlaylistId);
        Assert.Equal("VLLM", playlists[0].BrowseId);
        Assert.Equal("Liked Music", playlists[0].Title);
        Assert.Equal("142 songs", playlists[0].TrackCountText);
        Assert.Equal("https://img.example/lm_544.jpg", playlists[0].ArtworkUri);

        Assert.Equal("PLsynth001", playlists[1].PlaylistId);
        Assert.Equal("VLPLsynth001", playlists[1].BrowseId);
        Assert.Equal("Late Night Synthwave", playlists[1].Title);
        Assert.Equal("Alex Rivera", playlists[1].Author);
        Assert.Equal("38 songs", playlists[1].TrackCountText);
        Assert.Equal("https://lh3.googleusercontent.com/synth_large", playlists[1].ArtworkUri);

        Assert.Equal("PLoverlay99", playlists[2].PlaylistId);
        Assert.Equal("VLPLoverlay99", playlists[2].BrowseId);
        Assert.Equal("Overlay Only Playlist", playlists[2].Title);
        Assert.Equal("12 tracks", playlists[2].TrackCountText);
    }

    [Fact]
    public void ParseLibraryPlaylistsResponse_ResponsiveListItems_AndContinuationItemRenderer_ParsesCorrectly()
    {
        const string json = """
        {
          "continuationContents": {
            "musicShelfContinuation": {
              "contents": [
                {
                  "musicResponsiveListItemRenderer": {
                    "flexColumns": [
                      {
                        "musicResponsiveListItemFlexColumnRenderer": {
                          "text": {
                            "runs": [
                              {
                                "text": "Gym Phonk Mix",
                                "navigationEndpoint": {
                                  "browseEndpoint": {
                                    "browseId": "VLPLgym777",
                                    "browseEndpointContextSupportedConfigs": {
                                      "browseEndpointContextMusicConfig": {
                                        "pageType": "MUSIC_PAGE_TYPE_PLAYLIST"
                                      }
                                    }
                                  }
                                }
                              }
                            ]
                          }
                        }
                      },
                      {
                        "musicResponsiveListItemFlexColumnRenderer": {
                          "text": {
                            "runs": [
                              { "text": "Playlist" },
                              { "text": " • " },
                              { "text": "Sam" },
                              { "text": " • " },
                              { "text": "19 songs" }
                            ]
                          }
                        }
                      }
                    ],
                    "thumbnail": {
                      "musicThumbnailRenderer": {
                        "thumbnail": {
                          "thumbnails": [
                            { "url": "https://img.example/gym_60.jpg", "width": 60, "height": 60 },
                            { "url": "https://img.example/gym_544.jpg", "width": 544, "height": 544 }
                          ]
                        }
                      }
                    }
                  }
                },
                {
                  "continuationItemRenderer": {
                    "continuationEndpoint": {
                      "continuationCommand": {
                        "token": "CONT_ITEM_TOKEN_3"
                      }
                    }
                  }
                }
              ]
            }
          }
        }
        """;

        using var doc = JsonDocument.Parse(json);
        var (playlists, token) = YouTubePlaylistImportParser.ParseLibraryPlaylistsResponse(doc.RootElement);

        Assert.Single(playlists);
        Assert.Equal("PLgym777", playlists[0].PlaylistId);
        Assert.Equal("VLPLgym777", playlists[0].BrowseId);
        Assert.Equal("Gym Phonk Mix", playlists[0].Title);
        Assert.Equal("Sam", playlists[0].Author);
        Assert.Equal("19 songs", playlists[0].TrackCountText);
        Assert.Equal("https://img.example/gym_544.jpg", playlists[0].ArtworkUri);
        Assert.Equal("CONT_ITEM_TOKEN_3", token);
    }

    [Theory]
    [InlineData("""{"continuationContents":{"gridContinuation":{"continuations":[{"nextContinuationData":{"continuation":"TOK_GRID"}}]}}}""", "TOK_GRID")]
    [InlineData("""{"continuationContents":{"musicShelfContinuation":{"continuations":[{"nextContinuationData":{"continuation":"TOK_SHELF"}}]}}}""", "TOK_SHELF")]
    [InlineData("""{"continuationContents":{"sectionListContinuation":{"continuations":[{"nextContinuationData":{"continuation":"TOK_SECLIST"}}]}}}""", "TOK_SECLIST")]
    [InlineData("""{"contents":{"twoColumnBrowseResultsRenderer":{"secondaryContents":{"sectionListRenderer":{"contents":[{"musicPlaylistShelfRenderer":{"continuations":[{"nextContinuationData":{"continuation":"TOK_PL_SHELF"}}]}}]}}}}}""", "TOK_PL_SHELF")]
    [InlineData("""{"onResponseReceivedActions":[{"appendContinuationItemsAction":{"continuationItems":[{"continuationItemRenderer":{"continuationEndpoint":{"continuationCommand":{"token":"TOK_APPEND"}}}}]}}]}""", "TOK_APPEND")]
    public void ExtractContinuationToken_SupportsAllInnerTubeContainerShapes(string json, string expectedToken)
    {
        using var doc = JsonDocument.Parse(json);
        string? token = YouTubePlaylistImportParser.ExtractContinuationToken(doc.RootElement);

        Assert.Equal(expectedToken, token);
    }

    [Fact]
    public void ParsePlaylistTracksPage_PromotesAtvMptcId_RetainsOmvVideoTracks_ExtractsHeaderAndContinuation_IgnoresSuggestions()
    {
        const string json = """
        {
          "contents": {
            "twoColumnBrowseResultsRenderer": {
              "tabs": [
                {
                  "tabRenderer": {
                    "content": {
                      "sectionListRenderer": {
                        "contents": [
                          {
                            "musicEditablePlaylistDetailHeaderRenderer": {
                              "header": {
                                "musicResponsiveHeaderRenderer": {
                                  "thumbnail": {
                                    "musicThumbnailRenderer": {
                                      "thumbnail": {
                                        "thumbnails": [
                                          { "url": "https://img.example/header_226.jpg", "width": 226, "height": 226 },
                                          { "url": "https://img.example/header_1200.jpg", "width": 1200, "height": 1200 }
                                        ]
                                      }
                                    }
                                  },
                                  "description": {
                                    "musicDescriptionShelfRenderer": {
                                      "description": {
                                        "runs": [
                                          { "text": "Late night driving essentials" }
                                        ]
                                      }
                                    }
                                  }
                                }
                              }
                            }
                          }
                        ]
                      }
                    }
                  }
                }
              ],
              "secondaryContents": {
                "sectionListRenderer": {
                  "contents": [
                    {
                      "musicPlaylistShelfRenderer": {
                        "playlistId": "PLsynth001",
                        "contents": [
                          {
                            "musicResponsiveListItemRenderer": {
                              "playlistItemData": { "videoId": "OMVvideo111" },
                              "badges": [
                                {
                                  "musicInlineBadgeRenderer": {
                                    "icon": { "iconType": "MUSIC_EXPLICIT_BADGE" }
                                  }
                                }
                              ],
                              "menu": {
                                "menuRenderer": {
                                  "items": [
                                    {
                                      "menuNavigationItemRenderer": {
                                        "navigationEndpoint": {
                                          "browseEndpoint": {
                                            "browseId": "MPTCAtvAudio111"
                                          }
                                        }
                                      }
                                    }
                                  ]
                                }
                              },
                              "flexColumns": [
                                {
                                  "musicResponsiveListItemFlexColumnRenderer": {
                                    "text": { "runs": [{ "text": "Nightcall" }] }
                                  }
                                },
                                {
                                  "musicResponsiveListItemFlexColumnRenderer": {
                                    "text": {
                                      "runs": [
                                        {
                                          "text": "Kavinsky",
                                          "navigationEndpoint": {
                                            "browseEndpoint": {
                                              "browseId": "UCartist1",
                                              "browseEndpointContextSupportedConfigs": {
                                                "browseEndpointContextMusicConfig": { "pageType": "MUSIC_PAGE_TYPE_ARTIST" }
                                              }
                                            }
                                          }
                                        }
                                      ]
                                    }
                                  }
                                },
                                {
                                  "musicResponsiveListItemFlexColumnRenderer": {
                                    "text": {
                                      "runs": [
                                        {
                                          "text": "OutRun",
                                          "navigationEndpoint": {
                                            "browseEndpoint": {
                                              "browseId": "MPREb_outrun",
                                              "browseEndpointContextSupportedConfigs": {
                                                "browseEndpointContextMusicConfig": { "pageType": "MUSIC_PAGE_TYPE_ALBUM" }
                                              }
                                            }
                                          }
                                        }
                                      ]
                                    }
                                  }
                                }
                              ],
                              "fixedColumns": [
                                {
                                  "musicResponsiveListItemFixedColumnRenderer": {
                                    "text": { "runs": [{ "text": "4:18" }] }
                                  }
                                }
                              ],
                              "thumbnail": {
                                "musicThumbnailRenderer": {
                                  "thumbnail": {
                                    "thumbnails": [
                                      { "url": "https://img.example/nightcall_544.jpg", "width": 544, "height": 544 }
                                    ]
                                  }
                                }
                              }
                            }
                          },
                          {
                            "musicResponsiveListItemRenderer": {
                              "playlistItemData": { "videoId": "PureOmv2222" },
                              "overlay": {
                                "musicItemThumbnailOverlayRenderer": {
                                  "content": {
                                    "musicPlayButtonRenderer": {
                                      "playNavigationEndpoint": {
                                        "watchEndpoint": {
                                          "videoId": "PureOmv2222",
                                          "watchEndpointMusicSupportedConfigs": {
                                            "watchEndpointMusicConfig": {
                                              "musicVideoType": "MUSIC_VIDEO_TYPE_OMV"
                                            }
                                          }
                                        }
                                      }
                                    }
                                  }
                                }
                              },
                              "flexColumns": [
                                {
                                  "musicResponsiveListItemFlexColumnRenderer": {
                                    "text": { "runs": [{ "text": "Live Acoustic Session (Official Video)" }] }
                                  }
                                },
                                {
                                  "musicResponsiveListItemFlexColumnRenderer": {
                                    "text": {
                                      "runs": [
                                        { "text": "Indie Artist" },
                                        { "text": " • " },
                                        { "text": "2.4M views" }
                                      ]
                                    }
                                  }
                                }
                              ],
                              "fixedColumns": [
                                {
                                  "musicResponsiveListItemFixedColumnRenderer": {
                                    "text": { "runs": [{ "text": "5:02" }] }
                                  }
                                }
                              ],
                              "thumbnail": {
                                "musicThumbnailRenderer": {
                                  "thumbnail": {
                                    "thumbnails": [
                                      { "url": "https://img.example/live_480.jpg", "width": 480, "height": 360 }
                                    ]
                                  }
                                }
                              }
                            }
                          },
                          {
                            "musicResponsiveListItemRenderer": {
                              "playlistItemData": { "videoId": "AtvAudio111" },
                              "flexColumns": [
                                {
                                  "musicResponsiveListItemFlexColumnRenderer": {
                                    "text": { "runs": [{ "text": "Nightcall (Duplicate)" }] }
                                  }
                                }
                              ]
                            }
                          },
                          {
                            "continuationItemRenderer": {
                              "continuationEndpoint": {
                                "continuationCommand": {
                                  "token": "TRACKS_PAGE_2"
                                }
                              }
                            }
                          }
                        ]
                      }
                    },
                    {
                      "musicShelfRenderer": {
                        "title": { "runs": [{ "text": "Suggestions" }] },
                        "contents": [
                          {
                            "musicResponsiveListItemRenderer": {
                              "playlistItemData": { "videoId": "Suggest9999" },
                              "flexColumns": [
                                {
                                  "musicResponsiveListItemFlexColumnRenderer": {
                                    "text": { "runs": [{ "text": "Suggested Song Not In Playlist" }] }
                                  }
                                }
                              ]
                            }
                          }
                        ]
                      }
                    }
                  ]
                }
              }
            }
          }
        }
        """;

        using var doc = JsonDocument.Parse(json);
        var (tracks, continuationToken, headerCoverUri, headerDescription) =
            YouTubePlaylistImportParser.ParsePlaylistTracksPage(doc.RootElement);

        Assert.Equal("https://img.example/header_1200.jpg", headerCoverUri);
        Assert.Equal("Late night driving essentials", headerDescription);
        Assert.Equal("TRACKS_PAGE_2", continuationToken);
        Assert.Equal(2, tracks.Count);

        // Track 1: promoted from MPTC Track Credits ATV ID
        Assert.Equal("ytm_AtvAudio111", tracks[0].Id);
        Assert.Equal("AtvAudio111", tracks[0].YouTubeVideoId);
        Assert.Equal("ytmusic://AtvAudio111", tracks[0].AudioSourceUri);
        Assert.Equal(MediaSourceType.Online, tracks[0].SourceType);
        Assert.Equal("Nightcall", tracks[0].Title);
        Assert.Equal("Kavinsky", tracks[0].Artist);
        Assert.Equal("OutRun", tracks[0].Album);
        Assert.Equal(new TimeSpan(0, 4, 18), tracks[0].Duration);
        Assert.Equal("https://img.example/nightcall_544.jpg", tracks[0].CoverArtUri);
        Assert.True(tracks[0].IsExplicit);

        // Track 2: OMV video track retained (never silently dropped)
        Assert.Equal("ytm_PureOmv2222", tracks[1].Id);
        Assert.Equal("PureOmv2222", tracks[1].YouTubeVideoId);
        Assert.Equal("ytmusic://PureOmv2222", tracks[1].AudioSourceUri);
        Assert.Equal("Live Acoustic Session (Official Video)", tracks[1].Title);
        Assert.Equal("Indie Artist", tracks[1].Artist);
        Assert.Equal(new TimeSpan(0, 5, 2), tracks[1].Duration);
        Assert.False(tracks[1].IsExplicit);
    }

    [Fact]
    public void ParsePlaylistTracksPage_EmptyPlaylist_ReturnsZeroTracks()
    {
        const string json = """
        {
          "contents": {
            "twoColumnBrowseResultsRenderer": {
              "secondaryContents": {
                "sectionListRenderer": {
                  "contents": [
                    {
                      "musicPlaylistShelfRenderer": {
                        "playlistId": "PLempty",
                        "contents": []
                      }
                    }
                  ]
                }
              }
            }
          }
        }
        """;

        using var doc = JsonDocument.Parse(json);
        var (tracks, token, cover, desc) = YouTubePlaylistImportParser.ParsePlaylistTracksPage(doc.RootElement);

        Assert.Empty(tracks);
        Assert.Null(token);
        Assert.Null(cover);
        Assert.Null(desc);
    }
}
