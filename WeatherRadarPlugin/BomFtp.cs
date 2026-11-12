using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace WeatherRadarPlugin;

/// <summary>
/// The only place the plugin talks to the Bureau of Meteorology.
///
/// Everything comes from BOM's anonymous FTP service, never from www.bom.gov.au. BOM's
/// copyright notice (https://www.bom.gov.au/copyright) forbids automated extraction of
/// material from the website and says BOM may block an IP address for it without
/// notice. The anonymous FTP service is the route BOM publishes for programs:
/// its README shows a scripted fetch, and its radar guide lists
/// ftp://ftp.bom.gov.au/anon/gen/radar/ as where the radar images are.
///
/// Terms that apply to what is downloaded here (BOM's "FTP Public products" page): personal
/// use or use inside an organisation is allowed; passing the material on, or commercial
/// use, is not. The plugin only ever shows the images to the person who downloaded them.
/// </summary>
internal static class BomFtp
{
    private const string RadarDirectory = "ftp://ftp.bom.gov.au/anon/gen/radar/";

    // Rain-only 256 km images, named IDRnnn2.T.yyyymmddhhmm.png with the scan time in UTC.
    // The server expands the wildcard itself, so the reply is the 1,600 names that matter
    // (about 45 KB) rather than all 10,000 files in the directory (275 KB).
    private const string ScanPattern = "IDR*2.T.*.png";
    private static readonly Regex ScanName = new(@"IDR\d{2,4}2\.T\.\d{12}\.png",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(10));
    private static readonly Regex WholeScanName = new(@"^IDR\d{2,4}2\.T\.\d{12}\.png$",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    // BOM's README asks for an e-mail address as the anonymous password, "for
    // identification only". The plugin has no business knowing its user's address, so it
    // identifies the software instead.
    private const string Identification = "WeatherRadarPlugin@localhost";

    private const int MaxListingBytes = 4 * 1024 * 1024;
    // A radar image is 2 to 10 KB. Anything near this size is not one.
    private const int MaxImageBytes = 1024 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The names of every 256 km scan currently on the server.</summary>
    internal static async Task<string[]> ListScansAsync(CancellationToken token)
    {
        byte[]? listing = null;
        try
        {
            listing = await ReadAsync(RadarDirectory + ScanPattern, WebRequestMethods.Ftp.ListDirectory,
                MaxListingBytes, token).ConfigureAwait(false);
        }
        catch (WebException ex) when (ex.Status == WebExceptionStatus.ProtocolError)
        {
            // A server that will not expand the pattern says "no such file". Fall through.
        }

        var names = listing is null ? Array.Empty<string>() : Names(listing);
        if (names.Length > 0) return names;

        // Either the pattern is not supported any more or nothing matched. One plain
        // listing of the directory settles which.
        listing = await ReadAsync(RadarDirectory, WebRequestMethods.Ftp.ListDirectory,
            MaxListingBytes, token).ConfigureAwait(false);
        return Names(listing);
    }

    internal static Task<byte[]> DownloadScanAsync(string fileName, CancellationToken token)
    {
        // The name goes straight into a server path, so it is never taken on trust.
        if (fileName is null || !WholeScanName.IsMatch(fileName))
            throw new ArgumentException($"'{fileName}' is not a BOM radar scan name.", nameof(fileName));
        return ReadAsync(RadarDirectory + fileName, WebRequestMethods.Ftp.DownloadFile, MaxImageBytes, token);
    }

    private static string[] Names(byte[] listing)
    {
        var text = System.Text.Encoding.ASCII.GetString(listing);
        return ScanName.Matches(text).Cast<Match>().Select(match => match.Value)
            .Distinct(StringComparer.Ordinal).ToArray();
    }

    private static async Task<byte[]> ReadAsync(string uri, string method, int maxBytes, CancellationToken token)
    {
#pragma warning disable SYSLIB0014
        var request = (FtpWebRequest)WebRequest.Create(uri);
#pragma warning restore SYSLIB0014
        request.Method = method;
        request.Credentials = new NetworkCredential("anonymous", Identification);
        request.UseBinary = true;
        request.UsePassive = true;
        // One control connection is kept and reused, rather than logging in for every file.
        request.KeepAlive = true;
        request.ConnectionGroupName = "WeatherRadarPlugin";
        // FTP through an HTTP proxy cannot list or resume; talk to the server directly.
        request.Proxy = null;
        request.Timeout = (int)RequestTimeout.TotalMilliseconds;
        request.ReadWriteTimeout = (int)RequestTimeout.TotalMilliseconds;

        // Timeout only governs the synchronous calls, so the deadline is enforced here.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(RequestTimeout);
        using var abort = deadline.Token.Register(() => { try { request.Abort(); } catch { } });
        try
        {
            using var response = await request.GetResponseAsync().ConfigureAwait(false);
            using var stream = response.GetResponseStream()
                ?? throw new InvalidDataException("BOM's FTP server sent an empty reply.");
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk, 0, chunk.Length).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > maxBytes)
                {
                    try { request.Abort(); } catch { }
                    throw new InvalidDataException($"BOM's FTP reply for {uri} was larger than {maxBytes} bytes.");
                }
                buffer.Write(chunk, 0, read);
            }
            return buffer.ToArray();
        }
        catch (Exception ex) when (ex is WebException or IOException or ObjectDisposedException)
        {
            if (token.IsCancellationRequested) throw new OperationCanceledException(token);
            if (deadline.IsCancellationRequested)
                throw new TimeoutException(
                    $"BOM's FTP server did not answer within {RequestTimeout.TotalSeconds:F0} seconds.", ex);
            throw;
        }
    }
}
