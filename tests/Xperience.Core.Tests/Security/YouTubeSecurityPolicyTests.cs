using System.Net;
using System.Text.RegularExpressions;
using Xperience.Core.Services;
using Xperience.Core.Updates;
using Xunit;

namespace Xperience.Core.Tests.Security;

public sealed class YouTubeSecurityPolicyTests
{
    [Theory]
    [InlineData("https://accounts.google.com/ServiceLogin?continue=https%3A%2F%2Fmusic.youtube.com")]
    [InlineData("https://accounts.youtube.com/accounts/SetSID")]
    [InlineData("https://music.youtube.com/")]
    [InlineData("https://www.youtube.com/signin")]
    [InlineData("https://m.youtube.com/")]
    [InlineData("https://myaccount.google.com/")]
    [InlineData("https://consent.google.com/m")]
    [InlineData("https://consent.youtube.com/m")]
    [InlineData("https://gds.google.com/web/chip")]
    [InlineData("https://ssl.gstatic.com/accounts/ui/logo.png")]
    [InlineData("https://lh3.googleusercontent.com/a/avatar")]
    [InlineData("https://youtubei.googleapis.com/youtubei/v1/browse")]
    public void IsAllowedGoogleAuthUrl_AllowsTrustedGoogleAndYouTubeHttpsUrls(string url)
    {
        Assert.True(YouTubeSecurityPolicy.IsAllowedGoogleAuthUrl(url));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("http://accounts.google.com/ServiceLogin")]
    [InlineData("http://music.youtube.com/")]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("https://evil.com/accounts.google.com")]
    [InlineData("https://evilgoogle.com/login")]
    [InlineData("https://notyoutube.com/")]
    [InlineData("https://accounts.google.com.evil.com/login")]
    [InlineData("https://accounts.google.com@evil.com/login")]
    public void IsAllowedGoogleAuthUrl_RejectsHttpAndUntrustedHosts(string? url)
    {
        Assert.False(YouTubeSecurityPolicy.IsAllowedGoogleAuthUrl(url));
    }

    [Theory]
    [InlineData("music.youtube.com", true)]
    [InlineData("www.youtube.com", true)]
    [InlineData("m.youtube.com", true)]
    [InlineData("youtube.com", true)]
    [InlineData("youtubei.googleapis.com", true)]
    [InlineData("rr1---sn-abc.googlevideo.com", false)]
    [InlineData("evil.com", false)]
    [InlineData("music.youtube.com.evil.com", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsAllowedYouTubeApiHost_EnforcesStrictAllowlist(string? host, bool expected)
    {
        Assert.Equal(expected, YouTubeSecurityPolicy.IsAllowedYouTubeApiHost(host));
    }

    [Theory]
    [InlineData("rr3---sn-4g5e6nls.googlevideo.com", true)]
    [InlineData("r1---sn-a5mlrn7s.googlevideo.com", true)]
    [InlineData("music.youtube.com", true)]
    [InlineData("www.youtube.com", true)]
    [InlineData("youtube.com", true)]
    [InlineData("googlevideo.com", false)]
    [InlineData("evilgooglevideo.com", false)]
    [InlineData("evil.com", false)]
    [InlineData(null, false)]
    public void IsAllowedYouTubeStreamHost_EnforcesMediaCdnAllowlist(string? host, bool expected)
    {
        Assert.Equal(expected, YouTubeSecurityPolicy.IsAllowedYouTubeStreamHost(host));
    }

    [Theory]
    [InlineData("https://rr3---sn-4g5e6nls.googlevideo.com/videoplayback?expire=123", true)]
    [InlineData("https://music.youtube.com/watch?v=dQw4w9WgXcQ", true)]
    [InlineData("http://rr3---sn-4g5e6nls.googlevideo.com/videoplayback", false)]
    [InlineData("https://evil.example.com/videoplayback", false)]
    [InlineData("https://rr3---sn-abc.googlevideo.com@evil.com/videoplayback", false)]
    [InlineData("", false)]
    public void IsSafeHttpsYouTubeStreamUrl_ValidatesSchemeAndHost(string url, bool expected)
    {
        Assert.Equal(expected, YouTubeSecurityPolicy.IsSafeHttpsYouTubeStreamUrl(url));
    }

    [Theory]
    [InlineData("https://music-api.gdstudio.xyz/api.php", true)]
    [InlineData("https://api.tidal.com/v1/tracks/123", true)]
    [InlineData("https://1.1.1.1/api", true)]
    [InlineData("http://music-api.gdstudio.xyz/api.php", false)]
    [InlineData("https://localhost/api", false)]
    [InlineData("https://sub.localhost/api", false)]
    [InlineData("https://printer.local/api", false)]
    [InlineData("https://intranet/api", false)]
    [InlineData("https://127.0.0.1/api", false)]
    [InlineData("https://127.0.1.1/api", false)]
    [InlineData("https://10.0.0.1/api", false)]
    [InlineData("https://172.16.0.5/api", false)]
    [InlineData("https://192.168.1.1/api", false)]
    [InlineData("https://169.254.169.254/latest/meta-data/", false)]
    [InlineData("https://[::1]/api", false)]
    [InlineData("https://[fc00::1]/api", false)]
    [InlineData("https://[fe80::1]/api", false)]
    [InlineData("https://[::ffff:127.0.0.1]/api", false)]
    [InlineData("https://[::ffff:192.168.1.10]/api", false)]
    [InlineData("https://user:pass@music-api.gdstudio.xyz/api.php", false)]
    public void IsSafePublicHttpsUrl_RejectsNonHttpsAndPrivateTargets(string url, bool expected)
    {
        Assert.Equal(expected, YouTubeSecurityPolicy.IsSafePublicHttpsUrl(url));
    }

    [Fact]
    public void StripAuthCookiesForCdn_StripsGoogleAuthCookiesAndPreservesNonAuthCookies()
    {
        const string cookieHeader =
            "SID=secret1; HSID=secret2; SSID=secret3; APISID=secret4; SAPISID=secret5; " +
            "__Secure-1PSID=secret6; __Secure-3PSID=secret7; __Secure-1PAPISID=secret8; " +
            "__Secure-3PAPISID=secret9; LOGIN_INFO=secret10; OSID=secret11; __Secure-OSID=secret12; " +
            "LSID=secret13; SOCS=CAESEwgDEgk0ODE3Nzk3MjQaAmVuIAEaBgiA_LyaBg; PREF=tz=UTC";

        string? sanitized = YouTubeSecurityPolicy.StripAuthCookiesForCdn(cookieHeader);

        Assert.NotNull(sanitized);
        Assert.Contains("SOCS=CAESEwgDEgk0ODE3Nzk3MjQaAmVuIAEaBgiA_LyaBg", sanitized);
        Assert.Contains("PREF=tz=UTC", sanitized);

        string[] forbiddenNames =
        [
            "SID=", "HSID=", "SSID=", "APISID=", "SAPISID=",
            "__Secure-1PSID=", "__Secure-3PSID=", "__Secure-1PAPISID=", "__Secure-3PAPISID=",
            "LOGIN_INFO=", "OSID=", "__Secure-OSID=", "LSID="
        ];
        foreach (string forbidden in forbiddenNames)
        {
            Assert.DoesNotContain(forbidden, sanitized, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void StripAuthCookiesForCdn_ReturnsNullWhenOnlyAuthCookiesPresent()
    {
        string? sanitized = YouTubeSecurityPolicy.StripAuthCookiesForCdn("SID=abc; SAPISID=xyz; __Secure-3PSID=123");
        Assert.Null(sanitized);
    }

    [Theory]
    [InlineData("Neon_Pulse-01", "Neon_Pulse-01")]
    [InlineData("my_custom_viz", "my_custom_viz")]
    public void SanitizeVisualizerFileId_PreservesValidIds(string input, string expected)
    {
        Assert.Equal(expected, YouTubeSecurityPolicy.SanitizeVisualizerFileId(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("../../evil")]
    [InlineData("..\\Windows\\System32")]
    [InlineData("has space")]
    [InlineData("has.dot")]
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("PRN")]
    [InlineData("AUX")]
    [InlineData("NUL")]
    [InlineData("COM1")]
    [InlineData("COM9")]
    [InlineData("LPT1")]
    [InlineData("LPT9")]
    public void SanitizeVisualizerFileId_RejectsTraversalAndWindowsReservedNames(string? rawId)
    {
        string sanitized = YouTubeSecurityPolicy.SanitizeVisualizerFileId(rawId);

        Assert.StartsWith("viz_", sanitized, StringComparison.Ordinal);
        Assert.Matches(new Regex("^[A-Za-z0-9_-]{1,64}$"), sanitized);
    }

    [Fact]
    public async Task SafeRedirectDelegatingHandler_FollowsSameHostHttpsAndPreservesAuthHeaders()
    {
        var seenRequests = new List<HttpRequestMessage>();
        var inner = new StubHandler(req =>
        {
            seenRequests.Add(req);
            if (seenRequests.Count == 1)
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri("https://music.youtube.com/youtubei/v1/browse?hop=2");
                return redirect;
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        using var client = new HttpClient(new SafeRedirectDelegatingHandler(inner));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://music.youtube.com/youtubei/v1/browse");
        request.Headers.TryAddWithoutValidation("Cookie", "SAPISID=secret");
        request.Headers.TryAddWithoutValidation("Authorization", "SAPISIDHASH 123_abc");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, seenRequests.Count);
        Assert.True(seenRequests[1].Headers.Contains("Cookie"));
        Assert.True(seenRequests[1].Headers.Contains("Authorization"));
    }

    [Fact]
    public async Task SafeRedirectDelegatingHandler_StripsSensitiveHeadersOnCrossHostRedirect()
    {
        var seenRequests = new List<HttpRequestMessage>();
        var inner = new StubHandler(req =>
        {
            seenRequests.Add(req);
            if (seenRequests.Count == 1)
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
                redirect.Headers.Location = new Uri("https://evil-cdn.example.com/steal");
                return redirect;
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        using var client = new HttpClient(new SafeRedirectDelegatingHandler(inner));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://music.youtube.com/youtubei/v1/player");
        request.Headers.TryAddWithoutValidation("Cookie", "SAPISID=secret; SID=secret");
        request.Headers.TryAddWithoutValidation("Authorization", "SAPISIDHASH 123_abc");
        request.Headers.TryAddWithoutValidation("X-Goog-PageId", "12345");
        request.Headers.TryAddWithoutValidation("X-Goog-AuthUser", "0");
        request.Headers.TryAddWithoutValidation("X-Youtube-Bootstrap-Logged-In", "true");
        request.Headers.TryAddWithoutValidation("User-Agent", "TestAgent/1.0");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, seenRequests.Count);
        var redirected = seenRequests[1];
        Assert.False(redirected.Headers.Contains("Cookie"));
        Assert.False(redirected.Headers.Contains("Authorization"));
        Assert.False(redirected.Headers.Contains("X-Goog-PageId"));
        Assert.False(redirected.Headers.Contains("X-Goog-AuthUser"));
        Assert.False(redirected.Headers.Contains("X-Youtube-Bootstrap-Logged-In"));
        Assert.True(redirected.Headers.Contains("User-Agent"));
    }

    [Fact]
    public async Task SafeRedirectDelegatingHandler_BlocksHttpsToHttpDowngrade()
    {
        var inner = new StubHandler(_ =>
        {
            var redirect = new HttpResponseMessage(HttpStatusCode.Found);
            redirect.Headers.Location = new Uri("http://music.youtube.com/insecure");
            return redirect;
        });

        using var client = new HttpClient(new SafeRedirectDelegatingHandler(inner));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://music.youtube.com/youtubei/v1/browse");

        await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(request));
    }

    [Theory]
    [InlineData("https://github.com/AithonML/Xperience-Player-Windows-/releases/download/v1.1.0/setup.exe", true)]
    [InlineData("https://api.github.com/repos/AithonML/Xperience-Player-Windows-/releases/latest", true)]
    [InlineData("https://objects.githubusercontent.com/github-production-release-asset/123", true)]
    [InlineData("https://release-assets.githubusercontent.com/github-production-release-asset/456", true)]
    [InlineData("https://evil.com/setup.exe", false)]
    [InlineData("https://evilgithub.com/setup.exe", false)]
    [InlineData("http://github.com/AithonML/Xperience-Player-Windows-/releases/download/v1.1.0/setup.exe", false)]
    public void WindowsReleaseAssetSelector_IsAllowedGitHubUpdateUrl_EnforcesGitHubHosts(string url, bool expected)
    {
        Assert.Equal(expected, WindowsReleaseAssetSelector.IsAllowedGitHubUpdateUrl(url));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = responder(request);
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }
    }
}
