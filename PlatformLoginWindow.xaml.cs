using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace VideoToMaterial
{
    public partial class PlatformLoginWindow : Window
    {
        private readonly VideoPlatform _platform;
        private readonly VideoPlatformDefinition _definition;
        private readonly string _profileFolder;
        private readonly string _cookiePath;
        private readonly bool _forceRelogin;
        private readonly DispatcherTimer _cookieTimer;
        private bool _checking;

        public PlatformLoginWindow(VideoPlatform platform, string profileFolder, string cookiePath, bool forceRelogin)
        {
            InitializeComponent();
            _platform = platform;
            _definition = VideoPlatformCatalog.Get(platform);
            _profileFolder = profileFolder;
            _cookiePath = cookiePath;
            _forceRelogin = forceRelogin;
            Title = _definition.DisplayName + "扫码登录";
            LoginTitle.Text = "请在下方" + _definition.DisplayName + "页面扫码登录";
            LoginHint.Text = "登录成功后会自动继续下载。每个平台使用独立登录环境，不读取或锁定 Edge/Chrome 数据库。";
            _cookieTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _cookieTimer.Tick += async (_, _) => await TryCompleteFromCookiesAsync(showMissingMessage: false);
            Loaded += async (_, _) => await InitializeBrowserAsync();
            Closed += (_, _) => _cookieTimer.Stop();
        }

        private async Task InitializeBrowserAsync()
        {
            try
            {
                CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(null, _profileFolder);
                await Browser.EnsureCoreWebView2Async(environment);
                Browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
                Browser.CoreWebView2.Settings.IsStatusBarEnabled = false;
                if (_forceRelogin) Browser.CoreWebView2.CookieManager.DeleteAllCookies();
                Browser.CoreWebView2.NavigationCompleted += async (_, _) =>
                    await TryCompleteFromCookiesAsync(showMissingMessage: false);
                Browser.Source = new Uri(_definition.HomeUrl);
                StatusText.Text = _forceRelogin
                    ? "旧登录态已清除，请重新扫码登录。"
                    : "请点击页面中的登录并扫码；检测成功后会自动继续。";
                _cookieTimer.Start();
            }
            catch (WebView2RuntimeNotFoundException)
            {
                StatusText.Text = "未检测到 Microsoft Edge WebView2 Runtime。请安装后重试，或使用系统浏览器 Cookie。";
            }
            catch (Exception error)
            {
                StatusText.Text = "无法打开内置登录页：" + error.Message;
            }
        }

        private async Task TryCompleteFromCookiesAsync(bool showMissingMessage)
        {
            if (_checking || Browser.CoreWebView2 == null) return;
            _checking = true;
            try
            {
                IReadOnlyList<CoreWebView2Cookie> cookies =
                    await Browser.CoreWebView2.CookieManager.GetCookiesAsync(_definition.HomeUrl);
                List<BrowserCookieRecord> records = cookies.Select(ToRecord).ToList();
                if (!PlatformLoginSession.HasAuthenticatedSession(_platform, records))
                {
                    if (showMissingMessage) StatusText.Text = "暂未检测到登录态，请完成扫码后再试。";
                    return;
                }
                PlatformCookieFile.Write(_cookiePath, _platform, records);
                StatusText.Text = "已检测到登录状态，正在继续下载…";
                _cookieTimer.Stop();
                DialogResult = true;
            }
            catch (Exception error)
            {
                StatusText.Text = "读取登录状态失败：" + error.Message;
            }
            finally
            {
                _checking = false;
            }
        }

        private static BrowserCookieRecord ToRecord(CoreWebView2Cookie cookie)
        {
            return new BrowserCookieRecord
            {
                Domain = cookie.Domain,
                Path = cookie.Path,
                Name = cookie.Name,
                Value = cookie.Value,
                IsSecure = cookie.IsSecure,
                IsHttpOnly = cookie.IsHttpOnly,
                ExpiresUnixSeconds = cookie.IsSession
                    ? 0
                    : Math.Max(0, new DateTimeOffset(cookie.Expires.ToUniversalTime()).ToUnixTimeSeconds())
            };
        }

        private async void Continue_Click(object sender, RoutedEventArgs e) =>
            await TryCompleteFromCookiesAsync(showMissingMessage: true);

        private void ClearLogin_Click(object sender, RoutedEventArgs e)
        {
            if (Browser.CoreWebView2 == null) return;
            Browser.CoreWebView2.CookieManager.DeleteAllCookies();
            Browser.CoreWebView2.Reload();
            StatusText.Text = "登录状态已清除，请重新扫码登录。";
        }

        private void OpenExternal_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo(_definition.HomeUrl) { UseShellExecute = true });
                StatusText.Text = "已在系统浏览器打开。系统浏览器与内置登录环境相互隔离；如需复用其登录态，请关闭浏览器后选择对应浏览器 Cookie。";
            }
            catch (Exception error)
            {
                StatusText.Text = "无法打开系统浏览器：" + error.Message;
            }
        }
    }
}
