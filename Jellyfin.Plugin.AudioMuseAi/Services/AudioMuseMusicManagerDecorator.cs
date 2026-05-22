using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AudioMuseAi.Services
{
    /// <summary>
    /// Decorates the default <see cref="IMusicManager"/> to inject AudioMuse AI similarity
    /// into instant mix generation for ALL callers — including DLNA casting, which goes through
    /// <c>SessionManager.TranslateItemForInstantMix()</c> and calls <see cref="IMusicManager"/>
    /// directly rather than the HTTP <c>GET /Items/{id}/InstantMix</c> endpoint.
    /// </summary>
    public sealed class AudioMuseMusicManagerDecorator : IMusicManager
    {
        private const int DefaultMixLimit = 200;

        private readonly IMusicManager _inner;
        private readonly ILibraryManager _libraryManager;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<AudioMuseMusicManagerDecorator> _logger;

        // Created on first use so a bad plugin config doesn't break IMusicManager globally.
        private IAudioMuseService? _audioMuseService;

        /// <summary>
        /// Initializes a new instance of the <see cref="AudioMuseMusicManagerDecorator"/> class.
        /// </summary>
        public AudioMuseMusicManagerDecorator(
            IMusicManager inner,
            ILibraryManager libraryManager,
            IHttpClientFactory httpClientFactory,
            ILogger<AudioMuseMusicManagerDecorator> logger)
        {
            _inner = inner;
            _libraryManager = libraryManager;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        private IAudioMuseService AudioMuseService
        {
            get
            {
                if (_audioMuseService is null)
                {
                    _audioMuseService = new AudioMuseService(_httpClientFactory);
                }

                return _audioMuseService;
            }
        }

        /// <inheritdoc />
        public IReadOnlyList<BaseItem> GetInstantMixFromItem(BaseItem item, User? user, DtoOptions dtoOptions)
        {
            _logger.LogInformation(
                "AudioMuseAI: GetInstantMixFromItem called for '{ItemName}' ({ItemType}).",
                item.Name,
                item.GetType().Name);

            try
            {
                // Task.Run ensures we run on a clean thread-pool thread with no
                // synchronization context, avoiding deadlocks when blocking on async.
                var result = Task.Run(() => BuildAudioMuseMixAsync(item, user, DefaultMixLimit))
                    .GetAwaiter().GetResult();

                if (result.Count > 0)
                {
                    // If AudioMuse returned fewer items than the limit, supplement with
                    // native Jellyfin genre-based results (mirrors the original controller fallback).
                    if (result.Count < DefaultMixLimit)
                    {
                        var needed = DefaultMixLimit - result.Count;
                        _logger.LogInformation(
                            "AudioMuseAI: Mix has {Count} items, supplementing with up to {Needed} native Jellyfin items.",
                            result.Count,
                            needed);
                        var existingIds = new HashSet<Guid>(result.Select(i => i.Id));
                        foreach (var nativeItem in _inner.GetInstantMixFromItem(item, user, dtoOptions)
                                     .Where(i => !existingIds.Contains(i.Id))
                                     .Take(needed))
                        {
                            result.Add(nativeItem);
                        }
                    }

                    _logger.LogInformation(
                        "AudioMuseAI: Returning {Count} items for '{ItemName}'.",
                        result.Count,
                        item.Name);
                    return result;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AudioMuseAI: AudioMuse mix failed for '{ItemName}'. Falling back to Jellyfin default.", item.Name);
            }

            _logger.LogInformation("AudioMuseAI: Falling back to native Jellyfin instant mix for '{ItemName}'.", item.Name);
            return _inner.GetInstantMixFromItem(item, user, dtoOptions);
        }

        /// <inheritdoc />
        public IReadOnlyList<BaseItem> GetInstantMixFromArtist(MusicArtist artist, User? user, DtoOptions dtoOptions)
            => _inner.GetInstantMixFromArtist(artist, user, dtoOptions);

        /// <inheritdoc />
        public IReadOnlyList<BaseItem> GetInstantMixFromGenres(IEnumerable<string> genres, User? user, DtoOptions dtoOptions)
            => _inner.GetInstantMixFromGenres(genres, user, dtoOptions);

        private async Task<List<BaseItem>> BuildAudioMuseMixAsync(BaseItem item, User? user, int limit)
        {
            var finalItems = new List<BaseItem>();
            var finalItemIds = new HashSet<Guid>();
            List<Audio> seedSongs;

            if (item is Audio song)
            {
                finalItems.Add(song);
                finalItemIds.Add(song.Id);
                seedSongs = new List<Audio> { song };
            }
            else if (item is MusicAlbum album)
            {
                seedSongs = _libraryManager
                    .GetItemList(new InternalItemsQuery(user)
                    {
                        ParentId = album.Id,
                        IncludeItemTypes = new[] { BaseItemKind.Audio }
                    })
                    .Cast<Audio>()
                    .OrderBy(_ => Guid.NewGuid())
                    .ToList();

                if (!seedSongs.Any())
                {
                    return finalItems;
                }

                var first = seedSongs.First();
                finalItems.Add(first);
                finalItemIds.Add(first.Id);
            }
            else if (item.GetType().Name == "Playlist" && item is Folder playlist)
            {
                var allSongs = playlist.GetChildren(user, true).OfType<Audio>().ToList();
                if (!allSongs.Any())
                {
                    return finalItems;
                }

                var rng = new Random();
                var randomSeed = allSongs[rng.Next(allSongs.Count)];
                finalItems.Add(randomSeed);
                finalItemIds.Add(randomSeed.Id);
                seedSongs = allSongs.OrderBy(_ => Guid.NewGuid()).Take(20).ToList();
            }
            else if (item is MusicArtist artist)
            {
                var allSongs = _libraryManager
                    .GetItemList(new InternalItemsQuery(user)
                    {
                        ArtistIds = new[] { artist.Id },
                        IncludeItemTypes = new[] { BaseItemKind.Audio }
                    })
                    .Cast<Audio>()
                    .ToList();

                if (!allSongs.Any())
                {
                    return finalItems;
                }

                var rng = new Random();
                var randomSeed = allSongs[rng.Next(allSongs.Count)];
                finalItems.Add(randomSeed);
                finalItemIds.Add(randomSeed.Id);
                seedSongs = allSongs.OrderBy(_ => Guid.NewGuid()).Take(20).ToList();
            }
            else
            {
                _logger.LogWarning(
                    "AudioMuseAI: Unsupported item type '{Type}' for AudioMuse mix.",
                    item.GetType().Name);
                return finalItems;
            }

            if (!seedSongs.Any())
            {
                return finalItems;
            }

            var remaining = limit - finalItems.Count;
            var perSeed = (int)Math.Ceiling((decimal)remaining / seedSongs.Count);
            if (seedSongs.Count > 1)
            {
                perSeed *= 2;
            }

            _logger.LogInformation(
                "AudioMuseAI: Requesting ~{PerSeed} similar tracks for each of {SeedCount} seed songs.",
                perSeed,
                seedSongs.Count);

            foreach (var seed in seedSongs)
            {
                if (finalItems.Count >= limit)
                {
                    break;
                }

                try
                {
                    var response = await AudioMuseService
                        .GetSimilarTracksAsync(seed.Id.ToString("N"), null, null, perSeed, null, CancellationToken.None)
                        .ConfigureAwait(false);

                    if (response?.IsSuccessStatusCode == true)
                    {
                        var json = await response.Content
                            .ReadAsStringAsync(CancellationToken.None)
                            .ConfigureAwait(false);

                        using var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.ValueKind == JsonValueKind.Array)
                        {
                            var ids = doc.RootElement.EnumerateArray()
                                .Select(t => t.TryGetProperty("item_id", out var el) ? el.GetString() : null)
                                .Where(id => !string.IsNullOrEmpty(id) && Guid.TryParse(id, out _))
                                .Select(id => Guid.Parse(id!))
                                .ToList();

                            var newItems = _libraryManager
                                .GetItemList(new InternalItemsQuery(user) { ItemIds = ids.ToArray() })
                                .Where(i => !finalItemIds.Contains(i.Id))
                                .OrderBy(i => ids.IndexOf(i.Id))
                                .ToList();

                            _logger.LogInformation(
                                "AudioMuseAI: Got {Count} new items from AudioMuse for seed {SeedId} ({AudioMuseIds} ids returned).",
                                newItems.Count,
                                seed.Id,
                                ids.Count);

                            foreach (var newItem in newItems)
                            {
                                if (finalItems.Count >= limit)
                                {
                                    break;
                                }

                                if (finalItemIds.Add(newItem.Id))
                                {
                                    finalItems.Add(newItem);
                                }
                            }
                        }
                        else
                        {
                            _logger.LogWarning(
                                "AudioMuseAI: Unexpected response shape for seed {SeedId} (expected array, got {Kind}): {Body}",
                                seed.Id,
                                doc.RootElement.ValueKind,
                                json.Length > 300 ? json[..300] : json);
                        }
                    }
                    else
                    {
                        var body = response is null ? "(null response)" :
                            await response.Content.ReadAsStringAsync(CancellationToken.None).ConfigureAwait(false);
                        _logger.LogWarning(
                            "AudioMuseAI: Non-success response for seed {SeedId}: HTTP {StatusCode} — {Body}",
                            seed.Id,
                            response?.StatusCode,
                            body.Length > 300 ? body[..300] : body);
                    }
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogWarning(ex, "AudioMuseAI: HTTP error for seed {SeedId}. Aborting similarity search.", seed.Id);
                    break;
                }
            }

            return finalItems;
        }
    }
}
