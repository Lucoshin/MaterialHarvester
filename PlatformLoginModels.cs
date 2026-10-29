using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace VideoToMaterial
{
    public enum VideoPlatform
    {
        Douyin,
        Xiaohongshu,
        Bilibili
    }

    public sealed class VideoPlatformDefinition
    {
        public VideoPlatform Platform { get; init; }
        public string DisplayName { get; init; } = "";
        public string ProfileKey { get; init; } = "";
        public string HomeUrl { get; init; } = "";
        public IReadOnlyList<string> LinkDomains { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> CookieDomains { get; init; } = Array.Empty<string>();
        public IReadOnlySet<string> SessionCookieNames { get; init; } = new HashSet<string>();
    }

    public static class VideoPlatformCatalog
    {
        private static readonly IReadOnlyDictionary<VideoPlatform, VideoPlatformDefinition> Definitions =
            new Dictionary<VideoPlatform, VideoPlatformDefinition>
            {
                [VideoPlatform.Douyin] = Create(
                    VideoPlatform.Douyin,
                    "抖音",
                    "douyin",
                    "https://www.douyin.com/",
                    new[] { "douyin.com" },
                    new[] { "douyin.com" },
                    new[] { "sessionid", "sessionid_ss", "sid_guard", "sid_tt" }),
                [VideoPlatform.Xiaohongshu] = Create(
                    VideoPlatform.Xiaohongshu,
                    "小红书",
                    "xiaohongshu",
                    "https://www.xiaohongshu.com/explore",
                    new[] { "xiaohongshu.com", "xhslink.com" },
                    new[] { "xiaohongshu.com" },
                    new[] { "web_session" }),
                [VideoPlatform.Bilibili] = Create(
                    VideoPlatform.Bilibili,
                    "哔哩哔哩",
                    "bilibili",
                    "https://www.bilibili.com/",
                    new[] { "bilibili.com", "b23.tv" },
                    new[] { "bilibili.com" },
                    new[] { "SESSDATA" })
            };

        public static VideoPlatform? Detect(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri parsed)) return null;
            string host = parsed.IdnHost.TrimEnd('.');
            foreach (VideoPlatformDefinition definition in Definitions.Values)
            {
                if (definition.LinkDomains.Any(domain => IsDomainOrSubdomain(host, domain)))
                    return definition.Platform;
            }
            return null;
        }

        public static VideoPlatformDefinition Get(VideoPlatform platform) => Definitions[platform];

        public static bool IsCookieDomain(VideoPlatform platform, string domain)
        {
            string normalized = (domain ?? "").TrimStart('.').TrimEnd('.');
            return Definitions[platform].CookieDomains.Any(allowed => IsDomainOrSubdomain(normalized, allowed));
        }

        private static bool IsDomainOrSubdomain(string host, string domain)
        {
            return host.Equals(domain, StringComparison.OrdinalIgnoreCase)
                || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
        }

        private static VideoPlatformDefinition Create(
            VideoPlatform platform,
            string displayName,
            string profileKey,
            string homeUrl,
            string[] linkDomains,
            string[] cookieDomains,
            string[] sessionCookieNames)
        {
            return new VideoPlatformDefinition
            {
                Platform = platform,
                DisplayName = displayName,
                ProfileKey = profileKey,
                HomeUrl = homeUrl,
                LinkDomains = linkDomains,
                CookieDomains = cookieDomains,
                SessionCookieNames = new HashSet<string>(sessionCookieNames, StringComparer.OrdinalIgnoreCase)
            };
        }
    }

    public sealed class BrowserCookieRecord
    {
        public string Domain { get; set; } = "";
        public string Path { get; set; } = "/";
        public string Name { get; set; } = "";
        public string Value { get; set; } = "";
        public bool IsSecure { get; set; }
        public bool IsHttpOnly { get; set; }
        public long ExpiresUnixSeconds { get; set; }
    }

    public static class PlatformCookieFile
    {
        public static void Write(string path, VideoPlatform platform, IEnumerable<BrowserCookieRecord> cookies)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Cookie 文件路径不能为空。", nameof(path));
            if (cookies == null) throw new ArgumentNullException(nameof(cookies));
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

            var lines = new List<string>
            {
                "# Netscape HTTP Cookie File",
                "# Generated temporarily by Material Harvester. Do not edit."
            };
            foreach (BrowserCookieRecord cookie in cookies.Where(cookie => IsSafeCookie(platform, cookie)))
            {
                string domain = cookie.Domain.Trim();
                bool includeSubdomains = domain.StartsWith(".", StringComparison.Ordinal);
                string serializedDomain = cookie.IsHttpOnly ? "#HttpOnly_" + domain : domain;
                string cookiePath = string.IsNullOrWhiteSpace(cookie.Path) ? "/" : cookie.Path;
                lines.Add(string.Join("\t",
                    serializedDomain,
                    includeSubdomains ? "TRUE" : "FALSE",
                    cookiePath,
                    cookie.IsSecure ? "TRUE" : "FALSE",
                    Math.Max(0, cookie.ExpiresUnixSeconds).ToString(CultureInfo.InvariantCulture),
                    cookie.Name,
                    cookie.Value));
            }
            File.WriteAllLines(path, lines, new UTF8Encoding(false));
        }

        private static bool IsSafeCookie(VideoPlatform platform, BrowserCookieRecord cookie)
        {
            return cookie != null
                && !ContainsSeparator(cookie.Domain)
                && !ContainsSeparator(cookie.Path)
                && !ContainsSeparator(cookie.Name)
                && !ContainsSeparator(cookie.Value)
                && VideoPlatformCatalog.IsCookieDomain(platform, cookie.Domain);
        }

        private static bool ContainsSeparator(string value)
        {
            return string.IsNullOrEmpty(value) || value.IndexOfAny(new[] { '\t', '\r', '\n' }) >= 0;
        }
    }

    public static class PlatformLoginSession
    {
        public static bool HasAuthenticatedSession(VideoPlatform platform, IEnumerable<BrowserCookieRecord> cookies)
        {
            VideoPlatformDefinition definition = VideoPlatformCatalog.Get(platform);
            return cookies != null && cookies.Any(cookie => cookie != null
                && !string.IsNullOrWhiteSpace(cookie.Value)
                && definition.SessionCookieNames.Contains(cookie.Name ?? "")
                && VideoPlatformCatalog.IsCookieDomain(platform, cookie.Domain));
        }
    }
}
