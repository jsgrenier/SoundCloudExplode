﻿using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;
using SoundCloudExplode.Bridge;
using SoundCloudExplode.Common;
using SoundCloudExplode.Playlist;
using SoundCloudExplode.Exceptions;
using SoundCloudExplode.Utils.Extensions;

namespace SoundCloudExplode.Track;

/// <summary>
/// Operations related to Soundcloud playlist/album.
/// (Note: Everything for Playlists and Albums are handled the same.)
/// </summary>
public class PlaylistClient
{
    private readonly HttpClient _http;
    private readonly SoundcloudEndpoint _endpoint;

    private readonly Regex PlaylistRegex = new(@"soundcloud\..+?\/(.*?)\/sets\/[a-zA-Z]+");

    /// <summary>
    /// Initializes an instance of <see cref="PlaylistClient"/>.
    /// </summary>
    public PlaylistClient(
        HttpClient http,
        SoundcloudEndpoint endpoint)
    {
        _http = http;
        _endpoint = endpoint;
    }

    /// <summary>
    /// Checks for valid playlist url
    /// </summary>
    /// <param name="url"></param>
    /// <returns></returns>
    /// <exception cref="SoundcloudExplodeException"></exception>
    public bool IsUrlValid(string url)
    {
        url = url.ToLower();
        var isUrl = Uri.IsWellFormedUriString(url, UriKind.Absolute);
        return isUrl && PlaylistRegex.IsMatch(url);
    }

    /// <summary>
    /// Gets the metadata associated with the specified playlist.
    /// </summary>
    /// <param name="url"></param>
    /// <param name="autoPopulateAllTracks">Set to true if you want to get all tracks
    /// information at the same time. If false, only the tracks id and playlist info will return.
    /// </param>
    /// <param name="cancellationToken"></param>
    /// <exception cref="SoundcloudExplodeException"></exception>
    public async ValueTask<PlaylistInformation> GetAsync(
        string url,
        bool autoPopulateAllTracks = true,
        CancellationToken cancellationToken = default)
    {
        if (!IsUrlValid(url))
            throw new SoundcloudExplodeException("Invalid playlist url");

        var resolvedJson = await _endpoint.ResolveUrlAsync(url, cancellationToken);
        var playlist = JsonConvert.DeserializeObject<PlaylistInformation>(resolvedJson)!;

        if (autoPopulateAllTracks)
        {
            var tracks = await GetTracksAsync(url, cancellationToken: cancellationToken);
            playlist.Tracks = tracks.ToArray();
        }

        return playlist;
    }

    /// <summary>
    /// Enumerates batches of tracks included in the specified playlist.
    /// </summary>
    public async IAsyncEnumerable<Batch<TrackInformation>> GetTrackBatchesAsync(
        string url,
        int offset = 0,
        int limit = 0,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!IsUrlValid(url))
            throw new SoundcloudExplodeException("Invalid playlist url");

        var playlist = await GetAsync(url, false, cancellationToken);
        if (playlist is null || playlist.Tracks is null)
            yield break;

        if (limit > 0)
            playlist.Tracks = playlist.Tracks.Skip(offset).Take(limit).ToArray();
        else if (offset > 0)
            playlist.Tracks = playlist.Tracks.Skip(offset).ToArray();

        //Soundcloud single request limit is 50
        foreach (var chunk in playlist.Tracks.ChunkBy(50))
        {
            var ids = string.Join(",", chunk.Select(x => x.Id));

            var response = await _http.ExecuteGetAsync($"https://api-v2.soundcloud.com/tracks?ids={ids}&limit={limit}&offset={offset}&client_id={Constants.ClientId}", cancellationToken);
            yield return Batch.Create(JsonConvert.DeserializeObject<List<TrackInformation>>(response)!);
        }
    }

    /// <summary>
    /// Enumerates tracks included in the specified playlist url.
    /// </summary>
    public IAsyncEnumerable<TrackInformation> GetTracksAsync(
        string url,
        int offset = 0,
        int limit = 0,
        CancellationToken cancellationToken = default) =>
        GetTrackBatchesAsync(url, offset, limit, cancellationToken).FlattenAsync();
}