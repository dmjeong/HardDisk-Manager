using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ExplorerManager.Application;
using ExplorerManager.Domain;
using ExplorerManager.Infrastructure;
using Xunit;

namespace ExplorerManager.Tests
{
    // Real FTP control/data sockets. Faults are injected after receiving data or applying RNTO.
    internal sealed class TinyFtpServer : IDisposable
    {
        private readonly TcpListener _listener = new TcpListener(IPAddress.Loopback, 0);
        private readonly ConcurrentBag<TcpClient> _clients = new ConcurrentBag<TcpClient>();
        private readonly ConcurrentBag<TcpListener> _dataListeners = new ConcurrentBag<TcpListener>();
        private readonly ConcurrentDictionary<string, bool> _directories = new ConcurrentDictionary<string, bool>();
        public ConcurrentDictionary<string, byte[]> Files { get; } = new ConcurrentDictionary<string, byte[]>();
        public volatile bool DenyRead, DenyListing, DenySize, RejectFinalReply, ConsumeOnRenameAndLoseReply, CorruptUpload, AdvertiseHash;
        public int Uploads, Appends, Renames;
        public X509Certificate2 Certificate { get; set; }
        private bool _disposed;
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public TinyFtpServer()
        {
            foreach (string path in new[] { "/", "/incoming", "/staging", "/test" }) _directories[path] = true;
            _listener.Start(); _ = Task.Run(AcceptLoop);
        }
        private async Task AcceptLoop()
        {
            while (!_disposed)
                try { var client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false); _clients.Add(client); _ = Task.Run(() => Session(client)); }
                catch (Exception) when (_disposed) { return; }
        }
        private static StreamReader Reader(Stream s) => new StreamReader(s, new UTF8Encoding(false), false, 4096, true);
        private static StreamWriter Writer(Stream s) => new StreamWriter(s, new UTF8Encoding(false), 4096, true) { NewLine = "\r\n", AutoFlush = true };
        private static string Canonical(string path, string cwd)
        {
            if (string.IsNullOrEmpty(path)) return cwd;
            string value = path.StartsWith("/", StringComparison.Ordinal) ? path : cwd.TrimEnd('/') + "/" + path;
            var segments = new List<string>(); foreach (string p in value.Split('/')) { if (p.Length == 0 || p == ".") continue; if (p == "..") { if (segments.Count > 0) segments.RemoveAt(segments.Count - 1); } else segments.Add(p); }
            return "/" + string.Join("/", segments);
        }
        private static string Parent(string p) { int n = p.LastIndexOf('/'); return n <= 0 ? "/" : p.Substring(0, n); }
        private async Task Session(TcpClient client)
        {
            Stream control = client.GetStream(); var reader = Reader(control); var writer = Writer(control);
            TcpListener passive = null; string cwd = "/", rename = null; bool protect = false;
            try
            {
                await writer.WriteLineAsync("220 Explorer FTP test fixture").ConfigureAwait(false);
                while (!_disposed)
                {
                    string line = await reader.ReadLineAsync().ConfigureAwait(false); if (line == null) break;
                    int separator = line.IndexOf(' '); string command = (separator < 0 ? line : line.Substring(0, separator)).ToUpperInvariant();
                    string arg = separator < 0 ? "" : line.Substring(separator + 1); string path = Canonical(arg, cwd);
                    switch (command)
                    {
                        case "USER": await writer.WriteLineAsync("331 Password required").ConfigureAwait(false); break;
                        case "PASS": await writer.WriteLineAsync(arg == "test-password" ? "230 Logged in" : "530 Invalid credentials").ConfigureAwait(false); break;
                        case "AUTH":
                            if (Certificate == null) { await writer.WriteLineAsync("502 TLS unavailable").ConfigureAwait(false); break; }
                            await writer.WriteLineAsync("234 Start TLS").ConfigureAwait(false);
                            var ssl = new SslStream(control, false); await ssl.AuthenticateAsServerAsync(Certificate, false, SslProtocols.Tls12, false).ConfigureAwait(false);
                            control = ssl; reader = Reader(control); writer = Writer(control); break;
                        case "PBSZ": await writer.WriteLineAsync("200 PBSZ=0").ConfigureAwait(false); break;
                        case "PROT": protect = arg == "P"; await writer.WriteLineAsync("200 Protection set").ConfigureAwait(false); break;
                        case "FEAT":
                            await writer.WriteLineAsync("211-Features").ConfigureAwait(false);
                            foreach (string feat in new[] { " UTF8", " SIZE", " MDTM", " REST STREAM", " MLST type*;size*;modify*;" }) await writer.WriteLineAsync(feat).ConfigureAwait(false);
                            if (AdvertiseHash) await writer.WriteLineAsync(" HASH SHA-256*").ConfigureAwait(false);
                            await writer.WriteLineAsync("211 End").ConfigureAwait(false); break;
                        case "OPTS": case "TYPE": case "NOOP": await writer.WriteLineAsync("200 OK").ConfigureAwait(false); break;
                        case "SYST": await writer.WriteLineAsync("215 UNIX Type: L8").ConfigureAwait(false); break;
                        case "PWD": await writer.WriteLineAsync("257 \"" + cwd + "\" is current directory").ConfigureAwait(false); break;
                        case "CWD":
                            if (_directories.ContainsKey(path)) { cwd = path; await writer.WriteLineAsync("250 Directory changed").ConfigureAwait(false); }
                            else await writer.WriteLineAsync("550 Directory unavailable").ConfigureAwait(false); break;
                        case "MKD": _directories[path] = true; await writer.WriteLineAsync("257 Directory created").ConfigureAwait(false); break;
                        case "SIZE":
                            byte[] sizeData; await writer.WriteLineAsync(!DenySize && Files.TryGetValue(path, out sizeData) ? "213 " + sizeData.Length : "550 Unavailable").ConfigureAwait(false); break;
                        case "MDTM": await writer.WriteLineAsync(Files.ContainsKey(path) ? "213 20261001000000" : "550 Unavailable").ConfigureAwait(false); break;
                        case "MLST":
                            byte[] mlstData;
                            if (_directories.ContainsKey(path) || Files.TryGetValue(path, out mlstData))
                            {
                                long length = Files.TryGetValue(path, out mlstData) ? mlstData.Length : 0;
                                await writer.WriteLineAsync("250-Listing").ConfigureAwait(false);
                                await writer.WriteLineAsync(" type=" + (_directories.ContainsKey(path) ? "dir" : "file") + ";size=" + length + ";modify=20261001000000; " + path).ConfigureAwait(false);
                                await writer.WriteLineAsync("250 End").ConfigureAwait(false);
                            }
                            else await writer.WriteLineAsync("550 Unavailable").ConfigureAwait(false); break;
                        case "EPSV": case "PASV":
                            passive?.Stop(); passive = new TcpListener(IPAddress.Loopback, 0); passive.Start(); _dataListeners.Add(passive);
                            int port = ((IPEndPoint)passive.LocalEndpoint).Port;
                            await writer.WriteLineAsync(command == "EPSV" ? "229 Entering Extended Passive Mode (|||" + port + "|)" : "227 Entering Passive Mode (127,0,0,1," + (port / 256) + "," + (port % 256) + ")").ConfigureAwait(false); break;
                        case "STOR": case "APPE": case "RETR": case "NLST": case "LIST": case "MLSD":
                            if (passive == null) { await writer.WriteLineAsync("425 Use passive mode").ConfigureAwait(false); break; }
                            byte[] existing = null;
                            bool listing = command == "NLST" || command == "LIST" || command == "MLSD";
                            if ((command == "RETR" && (DenyRead || !Files.TryGetValue(path, out existing))) || (listing && (DenyListing || !_directories.ContainsKey(path))))
                            { await writer.WriteLineAsync("550 Access denied or missing").ConfigureAwait(false); passive.Stop(); passive = null; break; }
                            await writer.WriteLineAsync("150 Data channel open").ConfigureAwait(false);
                            using (var dataClient = await passive.AcceptTcpClientAsync().ConfigureAwait(false))
                            {
                                _clients.Add(dataClient); Stream data = dataClient.GetStream();
                                if (protect) { var dataSsl = new SslStream(data, false); await dataSsl.AuthenticateAsServerAsync(Certificate, false, SslProtocols.Tls12, false).ConfigureAwait(false); data = dataSsl; }
                                using (data)
                                {
                                    if (command == "STOR" || command == "APPE")
                                    {
                                        using (var received = new MemoryStream())
                                        {
                                            if (command == "APPE" && Files.TryGetValue(path, out existing)) received.Write(existing, 0, existing.Length);
                                            await data.CopyToAsync(received).ConfigureAwait(false); byte[] bytes = received.ToArray();
                                            if (CorruptUpload && bytes.Length > 0) bytes[bytes.Length - 1] ^= 1;
                                            Files[path] = bytes; if (command == "STOR") Interlocked.Increment(ref Uploads); else Interlocked.Increment(ref Appends);
                                        }
                                    }
                                    else if (command == "RETR") await data.WriteAsync(existing, 0, existing.Length).ConfigureAwait(false);
                                    else
                                    {
                                        var names = Files.Keys.Concat(_directories.Keys.Where(x => x != "/")).Where(x => Parent(x) == path).OrderBy(x => x).ToArray();
                                        string text = string.Join("\r\n", names.Select(x => command == "NLST" ? x : command == "MLSD" ? "type=file;size=" + (Files.ContainsKey(x) ? Files[x].Length : 0) + "; " + x.Substring(x.LastIndexOf('/') + 1) :
                                            "-rw-r--r-- 1 user group " + (Files.ContainsKey(x) ? Files[x].Length : 0) + " Oct 01 00:00 " + x.Substring(x.LastIndexOf('/') + 1)));
                                        if (text.Length > 0) text += "\r\n"; byte[] bytes = Encoding.UTF8.GetBytes(text); await data.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
                                    }
                                    await data.FlushAsync().ConfigureAwait(false);
                                }
                            }
                            passive.Stop(); passive = null;
                            await writer.WriteLineAsync(RejectFinalReply && (command == "STOR" || command == "APPE") ? "426 Transfer rejected" : "226 Transfer complete").ConfigureAwait(false); break;
                        case "HASH":
                            byte[] hashData;
                            if (!AdvertiseHash || !Files.TryGetValue(path, out hashData)) await writer.WriteLineAsync("550 Hash unavailable").ConfigureAwait(false);
                            else await writer.WriteLineAsync("213 SHA-256 0-" + hashData.Length + " " + TransferTestContext.Hash(hashData) + " " + path).ConfigureAwait(false); break;
                        case "RNFR": rename = Files.ContainsKey(path) ? path : null; await writer.WriteLineAsync(rename == null ? "550 Missing" : "350 Ready for RNTO").ConfigureAwait(false); break;
                        case "RNTO":
                            byte[] moved;
                            if (rename == null || Files.ContainsKey(path) || !Files.TryRemove(rename, out moved)) await writer.WriteLineAsync("550 Rename denied").ConfigureAwait(false);
                            else
                            {
                                Files[path] = moved; Interlocked.Increment(ref Renames); rename = null;
                                if (ConsumeOnRenameAndLoseReply) { Files.TryRemove(path, out moved); client.Close(); return; }
                                await writer.WriteLineAsync("250 Rename complete").ConfigureAwait(false);
                            }
                            break;
                        case "DELE": byte[] removed; await writer.WriteLineAsync(Files.TryRemove(path, out removed) ? "250 Deleted" : "550 Missing").ConfigureAwait(false); break;
                        case "QUIT": await writer.WriteLineAsync("221 Goodbye").ConfigureAwait(false); return;
                        default: await writer.WriteLineAsync("502 Unsupported").ConfigureAwait(false); break;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is SocketException || ex is ObjectDisposedException || ex is AuthenticationException) { }
            finally { passive?.Stop(); control.Dispose(); client.Close(); }
        }
        public TransferConnection Connection() => new TransferConnection { Host = "localhost", Port = Port, UserName = "images", Encryption = TransferEncryption.Plain,
            ConnectTimeoutSeconds = 3, CommandTimeoutSeconds = 2, NoProgressTimeoutSeconds = 5 };
        public void Dispose() { _disposed = true; _listener.Stop(); foreach (var c in _clients) c.Close(); foreach (var l in _dataListeners) l.Stop(); }
    }
    public sealed class FtpTransferTests
    {
        private static TransferJob Job(TransferConnection connection, byte[] bytes)
        {
            var j = new TransferJob { Connection = connection, Policy = new TransferPolicy { RemoteRoot = "/incoming", StagingRoot = "/staging", PublishMode = TransferPublishMode.StagingThenRename },
                File = new FileSnapshot { Length = bytes.Length }, RemoteFinal = "/incoming/capture.png", SourceSha256 = TransferTestContext.Hash(bytes) };
            j.RemoteTemp = "/staging/em-" + j.Id + ".part"; return j;
        }
        private static TransferOptions Unlimited() => new TransferOptions { UploadBytesPerSecond = 0, DownloadBytesPerSecond = 0, DiskReadBytesPerSecond = 0 };
        [Fact] public async Task ActualFtpSocketsUploadReadBackAndRenameWithoutExposingFinalEarly()
        {
            using (var server = new TinyFtpServer()) using (var ftp = new FtpTransferTransport(server.Connection(), "test-password", Unlimited))
            {
                byte[] bytes = Enumerable.Range(0, 100000).Select(x => (byte)(x % 251)).ToArray(); var j = Job(server.Connection(), bytes);
                await ftp.PrepareAsync(j, CancellationToken.None);
                Assert.False((await ftp.InspectAsync(j.RemoteFinal, CancellationToken.None)).Exists);
                using (var input = new MemoryStream(bytes)) Assert.Equal(bytes.Length, (await ftp.UploadAsync(j, input, 0, null, CancellationToken.None)).Sent);
                Assert.False(server.Files.ContainsKey(j.RemoteFinal)); Assert.Equal(bytes, server.Files[j.RemoteTemp]);
                var verified = await ftp.VerifyAsync(j.RemoteTemp, bytes.Length, TransferVerification.AutoStrong, null, CancellationToken.None);
                Assert.Equal(j.SourceSha256, verified.Sha256); Assert.Equal(TransferEvidenceLevel.ContentVerified, verified.Level);
                await ftp.PublishAsync(j, CancellationToken.None); Assert.Equal(bytes, server.Files[j.RemoteFinal]); Assert.False(server.Files.ContainsKey(j.RemoteTemp));
            }
        }
        [Fact] public async Task TestUploadVerifiesAppendResumeAndCleansOnlyProbeFiles()
        {
            using (var server = new TinyFtpServer()) using (var ftp = new FtpTransferTransport(server.Connection(), "test-password", Unlimited))
            {
                byte[] unrelated = { 1, 2, 3 }; server.Files["/test/unrelated.bin"] = unrelated;
                var caps = await ftp.TestUploadAsync("/test", Guid.NewGuid().ToString("N"), CancellationToken.None);
                Assert.True(caps.UploadResumeVerified); Assert.True(caps.RenameVerified); Assert.True(caps.ReadBackVerified);
                Assert.Equal(1, server.Appends); Assert.Single(server.Files); Assert.Same(unrelated, server.Files["/test/unrelated.bin"]);
            }
        }
        [Fact] public async Task NegativeFinalReplyIsRejectedDespiteCompleteRemoteBytes()
        {
            using (var server = new TinyFtpServer { RejectFinalReply = true }) using (var ftp = new FtpTransferTransport(server.Connection(), "test-password", Unlimited))
            {
                var bytes = new byte[4096]; var j = Job(server.Connection(), bytes);
                using (var input = new MemoryStream(bytes)) Assert.Equal("TransferReplyUnconfirmed", (await Assert.ThrowsAsync<TransferFailure>(() => ftp.UploadAsync(j, input, 0, null, CancellationToken.None))).Code);
                Assert.True(server.Files.ContainsKey(j.RemoteTemp)); Assert.False(server.Files.ContainsKey(j.RemoteFinal));
            }
        }
        [Fact] public async Task PermissionDeniedListingIsUnknownRatherThanMissing()
        {
            using (var server = new TinyFtpServer { DenySize = true, DenyListing = true }) using (var ftp = new FtpTransferTransport(server.Connection(), "test-password", Unlimited))
            { var remote = await ftp.InspectAsync("/incoming/hidden.png", CancellationToken.None); Assert.False(remote.Known); }
        }
        [Fact] public async Task NoReadPermissionCannotClaimStrongVerification()
        {
            using (var server = new TinyFtpServer { DenyRead = true }) using (var ftp = new FtpTransferTransport(server.Connection(), "test-password", Unlimited))
            { server.Files["/incoming/a.png"] = new byte[100]; Assert.Equal("VerificationUnavailable", (await Assert.ThrowsAsync<TransferFailure>(() => ftp.VerifyAsync("/incoming/a.png", 100, TransferVerification.AutoStrong, null, CancellationToken.None))).Code); }
        }
        [Fact] public async Task ForeignFinalIsNeverOverwrittenAndForeignTempCannotBeDeleted()
        {
            using (var server = new TinyFtpServer()) using (var ftp = new FtpTransferTransport(server.Connection(), "test-password", Unlimited))
            {
                byte[] bytes = { 9, 8, 7 }; var j = Job(server.Connection(), bytes); server.Files[j.RemoteFinal] = new byte[] { 1 }; server.Files[j.RemoteTemp] = bytes;
                Assert.Equal("Conflict", (await Assert.ThrowsAsync<TransferFailure>(() => ftp.PublishAsync(j, CancellationToken.None))).Code);
                Assert.Equal(new byte[] { 1 }, server.Files[j.RemoteFinal]); j.RemoteTemp = "/staging/foreign.part";
                Assert.Equal("TempOwnership", (await Assert.ThrowsAsync<TransferFailure>(() => ftp.RemoveOwnedTempAsync(j, CancellationToken.None))).Code);
            }
        }
        [Fact] public async Task LostRenameReplyIsNotReportedAsSuccessfulPublication()
        {
            using (var server = new TinyFtpServer { ConsumeOnRenameAndLoseReply = true }) using (var ftp = new FtpTransferTransport(server.Connection(), "test-password", Unlimited))
            {
                var j = Job(server.Connection(), new byte[100]); server.Files[j.RemoteTemp] = new byte[100];
                await Assert.ThrowsAsync<TransferFailure>(() => ftp.PublishAsync(j, CancellationToken.None)); Assert.Equal(1, server.Renames);
            }
        }
        [Fact] public async Task ServerSha256CapabilityIsUsedWhenAvailable()
        {
            using (var server = new TinyFtpServer { AdvertiseHash = true }) using (var ftp = new FtpTransferTransport(server.Connection(), "test-password", Unlimited))
            {
                var bytes = new byte[] { 3, 1, 4 }; server.Files["/incoming/hash.bin"] = bytes;
                var caps = await ftp.ProbeAsync(CancellationToken.None); Assert.True(caps.ServerSha256);
                var verified = await ftp.VerifyAsync("/incoming/hash.bin", bytes.Length, TransferVerification.AutoStrong, null, CancellationToken.None);
                Assert.Equal(TransferTestContext.Hash(bytes), verified.Sha256); Assert.Equal("Server SHA-256", verified.Method);
            }
        }
        [Fact] public async Task FtpAuthenticationFailureHasNoCredentialInError()
        {
            using (var server = new TinyFtpServer()) using (var ftp = new FtpTransferTransport(server.Connection(), "do-not-log-me", Unlimited))
            { var error = await Assert.ThrowsAsync<TransferFailure>(() => ftp.ProbeAsync(CancellationToken.None)); Assert.Equal("AuthenticationFailed", error.Code); Assert.DoesNotContain("do-not-log-me", error.Message); }
        }
        [Fact] public async Task ExplicitFtpsDoesNotSilentlyDowngradeOnServerWithoutTls()
        {
            using (var server = new TinyFtpServer())
            {
                var c = server.Connection(); c.Encryption = TransferEncryption.ExplicitTls;
                using (var ftp = new FtpTransferTransport(c, "test-password", Unlimited)) await Assert.ThrowsAsync<TransferFailure>(() => ftp.ProbeAsync(CancellationToken.None));
                Assert.Equal(0, server.Uploads);
            }
        }
    }
}
