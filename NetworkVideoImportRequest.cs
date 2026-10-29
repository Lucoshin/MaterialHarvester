using System.Collections.Generic;
using System.Linq;

namespace VideoToMaterial
{
    public sealed class NetworkVideoImportRequest
    {
        public string UrlText { get; set; }
        public bool AddDownloadsToQueue { get; set; } = true;
        public DownloadCookieOptions CookieOptions { get; set; } = new DownloadCookieOptions();

        public IEnumerable<string> FilesForQueue(IEnumerable<string> downloadedFiles)
        {
            return AddDownloadsToQueue
                ? downloadedFiles ?? Enumerable.Empty<string>()
                : Enumerable.Empty<string>();
        }
    }
}
