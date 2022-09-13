﻿using System;
using System.IO;
using System.Net;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using HtmlAgilityPack;
using SoundCloudExplode.Http;
using SoundCloudExplode.Track;
using SoundCloudExplode.Utils;
using SoundCloudExplode.Playlist;
using SoundCloudExplode.Exceptions;
using SoundCloudExplode.Tracks;

namespace SoundCloudExplode;

/// <summary>
/// Initializes an instance of <see cref="SoundCloudClient"/>.
/// </summary>
public class SoundCloudClient
{
    internal string ClientId { get; private set; }

    //private readonly Regex PlaylistRegex = new (@"soundcloud\..+?/(.*?)\/sets\/.+");
    //private readonly Regex TrackRegex = new (@"soundcloud\..+?/(.*?)\/sets\/.+");
    private readonly Regex PlaylistRegex = new(@"soundcloud\..+?\/(.*?)\/sets\/[a-zA-Z]+");
    private readonly Regex TrackRegex = new(@"soundcloud\..+?\/(.*?)\/[a-zA-Z0-9~@#$^*()_+=[\]{}|\\,.?: -]+");

    private readonly NetHttpClient _http;

    private readonly string BaseUrl = "https://soundcloud.com";

    /// <summary>
    /// Operations related to Soundcloud tracks.
    /// </summary>
    public TrackClient Tracks { get; }

    /// <summary>
    /// Operations related to Soundcloud playlists.
    /// </summary>
    public PlaylistClient Playlists { get; }

    /// <summary>
    /// Initializes an instance of <see cref="SoundCloudClient"/>.
    /// </summary>
    public SoundCloudClient(string clientId, HttpClient http)
    {
        ClientId = clientId;
        _http = new NetHttpClient(http);
        Tracks = new TrackClient(this, http);
        Playlists = new PlaylistClient(this);
    }

    /// <summary>
    /// Initializes an instance of <see cref="SoundCloudClient"/>.
    /// </summary>
    public SoundCloudClient() : this(Constants.ClientId, Utils.Http.Client)
    {
    }

    /// <summary>
    /// Initializes an instance of <see cref="SoundCloudClient"/>.
    /// </summary>
    public SoundCloudClient(HttpClient http)
        : this(Constants.ClientId, http)
    {
    }

    /// <summary>
    /// Initializes an instance of <see cref="SoundCloudClient"/>.
    /// </summary>
    public SoundCloudClient(string clientId)
        : this(Utils.Http.Client)
    {
        ClientId = clientId;
    }

    /// <summary>
    /// Sets Default ClientId
    /// </summary>
    public async void SetClientId(CancellationToken cancellationToken = default)
    {
        var html = await _http.GetAsync(BaseUrl, cancellationToken);
        var document = new HtmlDocument();
        document.LoadHtml(html);

        var script = document.DocumentNode.Descendants()
            .Where(x => x.Name == "script").ToList();

        var script_url = script.Last().Attributes["src"].Value;

        html = await _http.GetAsync(script_url, cancellationToken);

        ClientId = html.Split(new string[] { ",client_id" }, StringSplitOptions.None)[1].Split('"')[1];
    }

    internal async ValueTask<string> ResolveSoundcloudUrlAsync(
        string soundcloudUrl,
        CancellationToken cancellationToken = default)
    {
        return await _http.GetAsync($"{Constants.ResolveEndpoint}?url={soundcloudUrl}&client_id={ClientId}", cancellationToken);
    }

    private async ValueTask<string?> QueryTrackMp3Async(
        string trackM3u8,
        CancellationToken cancellationToken = default)
    {
        var html = await _http.GetAsync(trackM3u8, cancellationToken);
        var m3u8 = html.Split(',');

        if (m3u8.Length <= 0)
            return null;

        var link = "";

        var last_stream = m3u8.Last().Split('/');
        for (int i = 0; i < last_stream.Length; i++)
        {
            if (last_stream[i] == "media")
            {
                last_stream[i + 1] = "0";
                link = string.Join("/", last_stream).Split('\n')[1];
            }
        }

        return link;
    }

    /// <summary>
    /// Gets the download url from a track
    /// </summary>
    public async ValueTask<string?> GetDownloadUrlAsync(
        TrackInformation track,
        CancellationToken cancellationToken = default)
    {
        if (track.Policy?.ToLower() == "block")
        {
            throw new TrackUnavailableException("This track is not available in your country");
        }

        if (track.Media is null
            || track.Media.Transcodings is null
            || track.Media.Transcodings.Length <= 0)
        {
            throw new TrackUnavailableException("No transcodings found");
        }

        var trackUrl = "";

        //progrssive/stream
        var transcoding = track.Media.Transcodings
            .Where(x => x.Quality == "sq"
                && x.Format is not null && x.Format.MimeType is not null
                && x.Format.MimeType.Contains("audio/mpeg") && x.Format.Protocol == "progressive")
            .FirstOrDefault();

        //hls
        transcoding ??= track.Media.Transcodings
            .Where(x => x.Quality == "sq"
                && x.Format is not null && x.Format.MimeType is not null
                && x.Format.MimeType.Contains("ogg") && x.Format.Protocol == "hls")
            .FirstOrDefault();

        if (transcoding is null || transcoding.Url is null)
            return null;

        trackUrl += transcoding.Url.ToString() + $"?client_id={ClientId}";

        var trackMedia = await _http.GetAsync(trackUrl, cancellationToken);
        var track2 = JsonConvert.DeserializeObject<TrackMediaInformation>(trackMedia);

        if (track2 is null)
            return null;

        var trackMediaUrl = track2.Url ?? "";
        if (trackMediaUrl.Contains(".m3u8"))
        {
            return await QueryTrackMp3Async(trackMediaUrl, cancellationToken);
        }

        return trackMediaUrl;
    }

    /// <summary>
    /// Downloads a track
    /// </summary>
    public async ValueTask DownloadAsync(
        Playlist.Track track,
        string filePath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var trackInfo = await Tracks.GetByIdAsync(track.Id, cancellationToken);
        if (trackInfo is null)
            return;

        await DownloadAsync(trackInfo, filePath, progress, cancellationToken);
    }

    /// <summary>
    /// Downloads a track
    /// </summary>
    public async ValueTask DownloadAsync(
        TrackInformation track,
        string filePath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var mp3TrackMediaUrl = await GetDownloadUrlAsync(track, cancellationToken);
        if (mp3TrackMediaUrl is null)
            return;

        var totalLength = await _http.GetFileSizeAsync(mp3TrackMediaUrl, cancellationToken);

        var downloadRequest = WebRequest.Create(mp3TrackMediaUrl);
        var downloadResponse = downloadRequest.GetResponse();
        var stream = downloadResponse.GetResponseStream();

        var dir = Path.GetDirectoryName(filePath);
        if (dir is null)
            return;

        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        //Create a stream for the file
        var file = File.Create(filePath);

        try
        {
            double totProgress = 0;

            //This controls how many bytes to read at a time and send to the client
            int bytesToRead = 10000;

            // Buffer to read bytes in chunk size specified above
            byte[] buffer = new byte[bytesToRead];

            int length;
            do
            {
                // Read data into the buffer.
                length = stream.Read(buffer, 0, bytesToRead);

                // and write it out to the response's output stream
                file.Write(buffer, 0, length);

                // Flush the data
                stream.Flush();

                //Clear the buffer
                buffer = new byte[bytesToRead];

                totProgress = (double)file.Length / (double)totalLength * 100;

                progress?.Report(totProgress / 100);
            } while (length > 0); //Repeat until no data is read
        }
        finally
        {
            file?.Close();
            stream?.Close();
        }
    }
}