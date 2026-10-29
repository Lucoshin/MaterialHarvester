using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace VideoToMaterial
{
    public sealed class PlatformLoginService
    {
        private readonly string _profileRoot;
        private readonly string _temporaryFolder;

        public PlatformLoginService(string profileRoot = null, string temporaryFolder = null)
        {
            _profileRoot = profileRoot ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MaterialHarvester",
                "PlatformWebView2");
            _temporaryFolder = temporaryFolder ?? Path.Combine(Path.GetTempPath(), "MaterialHarvester", "PlatformCookies");
        }

        public Task<string> AcquireCookieFileAsync(
            Window owner,
            VideoPlatform platform,
            bool forceRelogin = false,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            VideoPlatformDefinition definition = VideoPlatformCatalog.Get(platform);
            string profileFolder = Path.Combine(_profileRoot, definition.ProfileKey);
            Directory.CreateDirectory(_temporaryFolder);
            string cookiePath = Path.Combine(
                _temporaryFolder,
                definition.ProfileKey + "-" + Guid.NewGuid().ToString("N") + ".txt");
            var window = new PlatformLoginWindow(platform, profileFolder, cookiePath, forceRelogin) { Owner = owner };
            using CancellationTokenRegistration registration = cancellationToken.Register(() =>
                window.Dispatcher.BeginInvoke(new Action(window.Close)));
            bool? accepted = window.ShowDialog();
            if (accepted == true && File.Exists(cookiePath)) return Task.FromResult(cookiePath);
            DeleteTemporaryCookieFile(cookiePath);
            return Task.FromResult<string>(null);
        }

        public static void DeleteTemporaryCookieFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                if (File.Exists(path)) File.Delete(path);
                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(directory)
                    && Directory.Exists(directory)
                    && !Directory.EnumerateFileSystemEntries(directory).Any())
                    Directory.Delete(directory);
            }
            catch
            {
                // Cookie cleanup is best effort and must not hide the download result.
            }
        }
    }
}
