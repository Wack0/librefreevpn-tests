using Renci.SshNet;
using ServiceLib.Common;
using ServiceLib.Enums;
using ServiceLib.Handler;
using ServiceLib.Manager;
using ServiceLib.Models;
using ServiceLib.ViewModels;
using System;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace LibFreeVPN.Tests
{
    [TestClass]
    public sealed class ProviderTests
    {
        private static readonly string[] NewLines = new string[] { "\r\n", "\n" };
        private static readonly HttpClient HttpClient = new HttpClient();
        private static MainWindowViewModel? RayViewModel = null;

        private static int FreeTcpPort()
        {
            TcpListener l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        private static async ValueTask<bool> SshTestAsync(IVPNServer server)
        {
            // Get the server credentials.
            var user = server.Registry[ServerRegistryKeys.Username];
            var pass = server.Registry[ServerRegistryKeys.Password];
            bool isPrivateKey = pass.StartsWith("-----BEGIN ") && pass.Contains("-----END ") && pass.Contains(" PRIVATE KEY-----") && NewLines.Any(pass.Contains);
            AuthenticationMethod? authMethod = null;
            if (isPrivateKey)
            {
                authMethod = new PrivateKeyAuthenticationMethod(user, new PrivateKeyFile(new MemoryStream(Encoding.UTF8.GetBytes(pass))));
            }
            else
            {
                authMethod = new PasswordAuthenticationMethod(user, pass);
            }
            var connInfo = new ConnectionInfo(server.Registry[ServerRegistryKeys.Hostname], int.Parse(server.Registry[ServerRegistryKeys.Port]), user, authMethod);
            using (var client = new SshClient(connInfo))
            {
                await client.ConnectAsync(CancellationToken.None);
                var localPort = FreeTcpPort();
                var port = new ForwardedPortLocal("127.0.0.1", (uint)localPort, "example.com", 80);
                client.AddForwardedPort(port);
                port.Start();
                using (var request = new HttpRequestMessage(HttpMethod.Get, string.Format("http://127.0.0.1:{0}", localPort)))
                {
                    request.Headers.Host = "example.com";
                    var response = await HttpClient.SendAsync(request);
                    await response.Content.ReadAsStringAsync();
                }
            }
            return true;
        }

        public static void CopyDirectory(string source, string target)
        {
            var stack = new Stack<(string Source, string Target)>();
            stack.Push((source, target));

            while (stack.Count > 0)
            {
                var folders = stack.Pop();
                Directory.CreateDirectory(folders.Target);
                foreach (var file in Directory.GetFiles(folders.Source, "*.*"))
                {
                    File.Copy(file, Path.Combine(folders.Target, Path.GetFileName(file)));
                }

                foreach (var folder in Directory.GetDirectories(folders.Source))
                {
                    stack.Push((folder, Path.Combine(folders.Target, Path.GetFileName(folder))));
                }
            }
        }

        private static async ValueTask<bool> RayOrSocksTestAsync(string url)
        {
            if (RayViewModel == null)
            {
                // make sure binaries are downloaded.
                string os = string.Empty;
                string arch = string.Empty;
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) os = "windows";
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) os = "macos";
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) os = "linux";
                else return true; // no binaries for this OS
                if (RuntimeInformation.OSArchitecture == Architecture.X64) arch = "64";
                else if (RuntimeInformation.OSArchitecture == Architecture.Arm64) arch = "arm64";
                else return false; // no binaries for this architecture

                // ensure binary dir is created
                var binPath = Path.Combine(Utils.StartupPath(), "bin");
                if (!Directory.Exists(binPath) || !File.Exists(Path.Combine(binPath, "geoip.dat")))
                {
                    // get and extract the zip
                    var data = await HttpClient.GetByteArrayAsync(string.Format("https://raw.githubusercontent.com/2dust/v2rayN-core-bin/refs/heads/master/v2rayN-{0}-{1}.zip", os, arch));
                    // create a temp dir
                    string tempDirectory = string.Empty;
                    for (tempDirectory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()); File.Exists(tempDirectory); tempDirectory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())) { }
                    Directory.CreateDirectory(tempDirectory);
                    using (var zip = new ZipArchive(new MemoryStream(data)))
                    {
                        zip.ExtractToDirectory(tempDirectory, true);
                    }
                    var extractedDir = Directory.EnumerateDirectories(tempDirectory).First();
                    var tempBin = Path.Combine(extractedDir, "bin");
                    if (!Directory.Exists(tempBin))
                    {
                        Directory.Delete(tempDirectory, true);
                        return false; // can't find the files extracted???
                    }
                    if (Directory.Exists(binPath)) Directory.Delete(binPath, true);
                    CopyDirectory(tempBin, binPath);
                }

                AppManager.Instance.InitApp();
                RayViewModel = new MainWindowViewModel(null);
            }
            // Remove all servers.
            var items = await AppManager.Instance.ProfileItems(string.Empty);
            int ret = 0;
            if (items != null)
            {
                ret = await ConfigHandler.RemoveServers(AppManager.Instance.Config, items);
                if (ret != 0) return false;
            }
            await RayViewModel.AddServerViaClipboardAsync(url);
            items = await AppManager.Instance.ProfileItems(string.Empty);
            if (items == null) return false;
            if (items.Count == 0) return false;
            ret = await ConfigHandler.SetDefaultServer(AppManager.Instance.Config, new List<ProfileItemModel>());
            if (ret != 0) return false;

            if (!await ConfigHandler.ApplyRegionalPreset(AppManager.Instance.Config, EPresetType.Default)) return false;
            ret = await ConfigHandler.InitRouting(AppManager.Instance.Config);
            if (ret != 0) return false;
            ret = await ConfigHandler.SaveConfig(AppManager.Instance.Config);
            if (ret != 0) return false;


            var freePort = FreeTcpPort();
            var firstSocks = AppManager.Instance.Config.Inbound.FirstOrDefault(t => t.Protocol == nameof(EInboundProtocol.socks));
            if (firstSocks != null) firstSocks.LocalPort = freePort;

            var node = await ConfigHandler.GetDefaultServer(AppManager.Instance.Config);
            await CoreManager.Instance.LoadCore(node);
            try
            {
                await Task.Delay(1000);
                var handler = new HttpClientHandler()
                {
                    Proxy = new WebProxy()
                    {
                        Address = new Uri(string.Format("http://127.0.0.1:{0}", freePort))
                    }
                };
                var client = new HttpClient(handler, true);
                await client.GetStringAsync("http://example.com");
            }
            catch
            {
                return false;
            }
            finally
            {
                await CoreManager.Instance.CoreStop();
            }
            return true;
        }

        private static ValueTask<bool> RayTestAsync(IVPNServer server)
        {
            return RayOrSocksTestAsync(server.Config);
        }

        private static readonly char[] s_CredsSplit = new char[] { ':' };

        private static async ValueTask<bool> ProxyHttpTestAsync(string server)
        {
            try
            {
                var serverUri = new Uri(server);
                ICredentials? creds = null;
                if (!string.IsNullOrEmpty(serverUri.UserInfo))
                {
                    var userInfo = serverUri.UserInfo.Split(s_CredsSplit, 2);
                    if (userInfo.Length == 2) creds = new NetworkCredential(userInfo[0], userInfo[1]);
                }
                var handler = new HttpClientHandler()
                {
                    Proxy = new WebProxy()
                    {
                        Address = new Uri(server),
                        UseDefaultCredentials = false,
                        Credentials = creds
                    }
                };
                var client = new HttpClient(handler, true);
                await client.GetStringAsync("http://example.com");
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string ProxyGetUriFromConfig(IVPNServer server)
        {
            var config = server.Config.Split("\r\n");
            for (int i = 0; i < config.Length; i++)
            {
                if (config[i].StartsWith("http://") || config[i].StartsWith("https://") || config[i].StartsWith("socks://")) return config[i];
            }
            return string.Empty;
        }

        private static ValueTask<bool> ProxyTestAsync(IVPNServer server)
        {
            var uri = ProxyGetUriFromConfig(server);
            if (string.IsNullOrEmpty(uri)) return new ValueTask<bool>(false);
            if (uri.StartsWith("socks://")) return RayOrSocksTestAsync(uri);
            return ProxyHttpTestAsync(uri);
        }

        private static async ValueTask<bool> OvpnTestAsync(IVPNServer server)
        {
            if (server.Registry[ServerRegistryKeys.Hostname].EndsWith(".cloudfront.net")) return false;
            // can't check UDP, just assume it's valid
            if (!server.Config.Contains("proto tcp-") && !server.Config.Contains("proto tcp\r\n") && !server.Config.Contains("proto tcp\n")) return true;
            // TODO: actually implement an actual connection if possible.
            // for now, just ensure the TCP port is open
            using (var tcp = new TcpClient())
            {
                await tcp.ConnectAsync(server.Registry[ServerRegistryKeys.Hostname], int.Parse(server.Registry[ServerRegistryKeys.Port]));
            }
            return true;
        }

        public static async ValueTask<bool> ServerTestAsync(IVPNProvider provider, IVPNServer server)
        {
            try
            {
                switch (server.Protocol)
                {
                    case ServerProtocol.SSH:
                        return await SshTestAsync(server);
                    case ServerProtocol.V2Ray:
                        return await RayTestAsync(server);
                    case ServerProtocol.WebProxy:
                        return await ProxyTestAsync(server);
                    case ServerProtocol.OpenVPN:
                        if (server.Registry[ServerRegistryKeys.Hostname].EndsWith(".cloudfront.net")) return false;
                        return await OvpnTestAsync(server);
                    case ServerProtocol.WireGuard:
                        // TODO: actually implement something.
                        if (server.Registry[ServerRegistryKeys.Hostname].EndsWith(".cloudfront.net")) return false;
                        return true;
                    case ServerProtocol.Unknown: // should never happen
                    default: // unimplemented
                        return true; // assume it works.
                }
            } catch
            {
                return false;
            }
        }

        public static bool ServerCanBeTestedAsync(IVPNServer server)
        {
            switch (server.Protocol)
            {
                case ServerProtocol.SSH:
                    return true;
                case ServerProtocol.V2Ray:
                    return true;
                case ServerProtocol.WebProxy:
                    return true;
                case ServerProtocol.OpenVPN:
                    if (server.Registry[ServerRegistryKeys.Hostname].EndsWith(".cloudfront.net")) return true;
                    if (!server.Config.Contains("proto tcp-") && !server.Config.Contains("proto tcp\r\n") && !server.Config.Contains("proto tcp\n")) return false;
                    return true;
                case ServerProtocol.WireGuard:
                    // TODO: actually implement something.
                    if (server.Registry[ServerRegistryKeys.Hostname].EndsWith(".cloudfront.net")) return true;
                    return false;
                case ServerProtocol.Unknown: // should never happen
                default: // unimplemented
                    return false;
            }
        }
        private async Task ProviderTestAsyncImpl(IVPNProvider provider)
        {
            provider.Reset();
            var servers = await provider.GetServersAsync();
            Assert.IsNotEmpty(servers, "An empty list of servers was obtained from this provider.");
            bool oneServerGood = false;
            var serversToTest = servers.Where(ServerCanBeTestedAsync);
            if (!serversToTest.Any()) return;
            foreach (var server in serversToTest)
            {
                oneServerGood |= await ServerTestAsync(provider, server);
                if (oneServerGood) break;
            }
            Assert.IsTrue(oneServerGood, "None of the servers obtained from this provider succeeded a connection test.");
        }

        [TestMethod]
        [DynamicData(nameof(GetProviders), DynamicDataSourceType.Method)]
        [DoNotParallelize]
        public Task ProviderTestAsync(IVPNProvider provider) => ProviderTestAsyncImpl(provider);

        [TestMethod]
        [DynamicData(nameof(GetProvidersAbandoned), DynamicDataSourceType.Method)]
        [DoNotParallelize]
        public Task ProviderTestAbandonedAsync(IVPNProvider provider) => ProviderTestAsyncImpl(provider);

        public static IEnumerable<TestDataRow<IVPNProvider>> GetProviders() => GetProvidersImpl(false);
        public static IEnumerable<TestDataRow<IVPNProvider>> GetProvidersAbandoned() => GetProvidersImpl(true);

        private static IEnumerable<TestDataRow<IVPNProvider>> GetProvidersImpl(bool abandoned) => VPNProviders.Providers.Where((p) => p.PossiblyAbandoned.HasValue == abandoned).Select((p) =>
        {
            string prefix = "";
            if (p.RiskyRequests) prefix = "[Risky] ";
            return new TestDataRow<IVPNProvider>(p)
            {
                DisplayName = prefix + p.Name
            };
        });
    }
}
