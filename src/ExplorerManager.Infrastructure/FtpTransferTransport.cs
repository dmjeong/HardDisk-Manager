using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ExplorerManager.Application;
using ExplorerManager.Domain;
using FluentFTP;
using FluentFTP.Exceptions;

namespace ExplorerManager.Infrastructure
{
    public sealed class FtpTransferTransport : ITransferTransport
    {
        private readonly TransferConnection _connection;
        private readonly TransferFtpSession _client;
        private readonly AsyncByteLimiter _upload, _download;
        private readonly Func<TransferOptions> _options;
        private bool _connected;
        private int _disposed;
        public FtpTransferTransport(TransferConnection connection, string password, Func<TransferOptions> options = null,
            AsyncByteLimiter upload = null, AsyncByteLimiter download = null)
        {
            TransferRules.ValidateConnection(connection); _connection = connection;
            _options = options ?? (() => new TransferOptions()); _upload = upload ?? new AsyncByteLimiter(); _download = download ?? new AsyncByteLimiter();
            _client = new TransferFtpSession(connection, password);
            _client.Encoding = Encoding.GetEncoding(connection.EncodingName, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            _client.Config.EncryptionMode = connection.Encryption == TransferEncryption.Plain ? FtpEncryptionMode.None :
                connection.Encryption == TransferEncryption.ImplicitTls ? FtpEncryptionMode.Implicit : FtpEncryptionMode.Explicit;
            _client.Config.SslProtocols = SslProtocols.None; _client.Config.ValidateAnyCertificate = false;
            _client.Config.DataConnectionEncryption = connection.Encryption != TransferEncryption.Plain;
            _client.Config.DataConnectionType = connection.UseEpsv ? FtpDataConnectionType.EPSV : FtpDataConnectionType.PASVEX;
            _client.Config.ConnectTimeout = connection.ConnectTimeoutSeconds * 1000;
            _client.Config.ReadTimeout = connection.CommandTimeoutSeconds * 1000;
            _client.Config.WriteTimeout = connection.CommandTimeoutSeconds * 1000;
            _client.Config.DataConnectionConnectTimeout = connection.ConnectTimeoutSeconds * 1000;
            _client.Config.DataConnectionReadTimeout = connection.NoProgressTimeoutSeconds * 1000;
            _client.Config.DataConnectionWriteTimeout = connection.NoProgressTimeoutSeconds * 1000;
            _client.Config.DisconnectWithQuit = false; _client.Config.Noop = false; _client.Config.RetryAttempts = 0;
            _client.Config.LogPassword = false; _client.Config.LogUserName = false;
        }
        private async Task Connected(CancellationToken ct)
        {
            if (_connected) return;
            await _client.Connect(ct).ConfigureAwait(false);
            var binary = await _client.Execute("TYPE I", ct).ConfigureAwait(false);
            if (!binary.Success) throw new TransferFailure("BinaryModeUnavailable", "FTP binary 모드를 설정하지 못했습니다.");
            _connected = true;
        }
        private async Task<T> Guard<T>(Func<Task<T>> action, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            using (ct.Register(Dispose))
            {
                try { await Connected(ct).ConfigureAwait(false); return await action().ConfigureAwait(false); }
                catch (Exception) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
                catch (TransferFailure) { throw; }
                catch (AuthenticationException) { throw new TransferFailure("TlsAuthentication", "FTP TLS 인증서/보안 협상을 확인하세요."); }
                catch (FtpCommandException ex) { throw Failure(ex.CompletionCode); }
                catch (FtpException) { throw new TransferFailure("ConnectionUnavailable", "FTP 세션을 설정할 수 없습니다. 서버 연결/프로토콜 설정을 확인하세요.", true); }
                catch (EncoderFallbackException) { throw new TransferFailure("RemoteEncoding", "원격 파일명을 설정된 인코딩으로 표현할 수 없습니다."); }
                catch (DecoderFallbackException) { throw new TransferFailure("RemoteEncoding", "원격 응답의 문자 인코딩을 확인하세요."); }
                catch (SocketException) { throw new TransferFailure("ConnectionUnavailable", "FTP 서버 연결을 확인하세요.", true); }
                catch (TimeoutException) { throw new TransferFailure("ConnectionTimeout", "FTP 응답 시간이 초과되었습니다.", true); }
                catch (IOException) { throw new TransferFailure("ConnectionInterrupted", "FTP 데이터 연결이 중단되었습니다.", true); }
                catch (ObjectDisposedException) { throw new TransferFailure("ConnectionInterrupted", "FTP 세션이 종료되었습니다.", true); }
            }
        }
        private static TransferFailure Failure(string code)
        {
            if (code == "530") return new TransferFailure("AuthenticationFailed", "FTP 계정/비밀번호 또는 로그인 권한을 확인하세요.");
            if (code == "452" || code == "552") return new TransferFailure("DestinationFull", "상위 서버의 저장 공간/할당량을 확인하세요.", true);
            bool retry = code != null && code.StartsWith("4", StringComparison.Ordinal);
            return new TransferFailure("FtpReply" + (code ?? "Unknown"), "FTP 명령 실패 (" + (code ?? "응답 없음") + "). 경로/권한/서버 상태를 확인하세요.", retry);
        }
        public Task<TransferCapabilities> ProbeAsync(CancellationToken ct) => Guard(async () =>
        {
            string fingerprint = TransferRules.ConnectionFingerprint(_connection);
            var old = _connection.Capabilities;
            await Task.CompletedTask;
            return new TransferCapabilities { Connected = true, CheckedUtc = DateTime.UtcNow, TargetFingerprint = fingerprint,
                ServerSha256 = (_client.HashAlgorithms & FtpHashAlgorithm.SHA256) != 0, ResumeAdvertised = _client.Capabilities.Contains(FtpCapability.REST),
                UploadResumeVerified = old?.TargetFingerprint == fingerprint && old.UploadResumeVerified,
                ReadBackVerified = old?.TargetFingerprint == fingerprint && old.ReadBackVerified,
                RenameVerified = old?.TargetFingerprint == fingerprint && old.RenameVerified,
                Detail = "Login/binary/passive profile connected. Write/RETR/rename/resume require Test upload." };
        }, ct);

        public async Task PrepareAsync(TransferJob job, CancellationToken ct)
        {
            await Guard(async () => {
                if (!await _client.DirectoryExists(job.Policy.RemoteRoot, ct).ConfigureAwait(false)) throw new TransferFailure("RemoteRootUnavailable", "FTP에서 보이는 Remote root가 없거나 접근할 수 없습니다.");
                if (job.Policy.PublishMode == TransferPublishMode.StagingThenRename && !await _client.DirectoryExists(job.Policy.StagingRoot, ct).ConfigureAwait(false))
                    throw new TransferFailure("StagingUnavailable", "수신기 감시 밖의 Staging root가 없거나 접근할 수 없습니다.");
                string parent = Parent(job.RemoteFinal);
                if (job.Policy.AllowCreateDirectories) await _client.CreateDirectory(parent, true, ct).ConfigureAwait(false);
                else if (!await _client.DirectoryExists(parent, ct).ConfigureAwait(false)) throw new TransferFailure("RemoteDirectoryMissing", "원격 하위 폴더를 생성하거나 먼저 준비하세요.");
                return true;
            }, ct).ConfigureAwait(false);
        }
        private static string Parent(string path) { int n = path.LastIndexOf('/'); return n <= 0 ? "/" : path.Substring(0, n); }
        private static string Name(string path) => path.Substring(path.LastIndexOf('/') + 1);
        public Task<TransferRemoteState> InspectAsync(string path, CancellationToken ct) => Guard(async () =>
        {
            TransferRules.RemotePath(path);
            long size = await _client.GetFileSize(path, -1, ct).ConfigureAwait(false);
            if (size >= 0) return new TransferRemoteState { Known = true, Exists = true, Length = size };
            // 550 alone is ambiguous. A successfully completed, bounded parent NLST establishes absence.
            bool found = false; long total = 0; var line = new StringBuilder();
            FtpDataStream data;
            try { data = await _client.OpenNamesAsync(Parent(path), ct).ConfigureAwait(false); }
            catch (FtpCommandException) { return new TransferRemoteState(); }
            data.ControlConnection = null;
            try
            {
                byte[] buffer = new byte[8192]; var decoder = _client.Encoding.GetDecoder(); char[] chars = new char[16384];
                while (true)
                {
                    int n = await data.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false); if (n == 0) break;
                    total += n; if (total > 16 * 1048576L) throw new TransferFailure("RemoteListingLimit", "원격 폴더 목록이 너무 큽니다. 하위 폴더로 분리하세요.");
                    int count = decoder.GetChars(buffer, 0, n, chars, 0, false);
                    for (int i = 0; i < count; i++)
                    {
                        if (chars[i] == '\n') { found |= string.Equals(Name(line.ToString().TrimEnd('\r')), Name(path), StringComparison.OrdinalIgnoreCase); line.Clear(); }
                        else { line.Append(chars[i]); if (line.Length > 8192) throw new TransferFailure("RemoteListingInvalid", "원격 목록 항목이 너무 깁니다."); }
                    }
                }
                if (line.Length > 0) found |= string.Equals(Name(line.ToString().TrimEnd('\r')), Name(path), StringComparison.OrdinalIgnoreCase);
                await data.CloseAsync(ct).ConfigureAwait(false); RequireTransferReply(await _client.GetReply(ct).ConfigureAwait(false));
            }
            finally { data.Dispose(); }
            if (found) return new TransferRemoteState(); // entry exists, but its size/permissions are unknown
            return new TransferRemoteState { Known = true, Exists = false };
        }, ct);

        public Task<TransferUploadResult> UploadAsync(TransferJob job, Stream source, long offset, Action<TransferProgress> progress, CancellationToken ct) => Guard(async () =>
        {
            string path = TransferRules.RemotePath(job.RemoteTemp);
            if (offset < 0 || offset > job.Bytes || source.Position != offset) throw new TransferFailure("InvalidResume", "전송 offset이 원본과 일치하지 않습니다.");
            if (job.Policy.PublishMode != TransferPublishMode.DirectWrite) EnsureOwnedTemp(job);
            var data = (FtpDataStream)(offset == 0 ? await _client.OpenWrite(path, FtpDataType.Binary, job.Bytes, ct).ConfigureAwait(false) :
                await _client.OpenAppend(path, FtpDataType.Binary, offset, ct).ConfigureAwait(false));
            data.ControlConnection = null; long sent = offset; var watch = Stopwatch.StartNew(); byte[] buffer = new byte[TransferIO.BufferBytes];
            try
            {
                while (sent < job.Bytes)
                {
                    ct.ThrowIfCancellationRequested(); int want = (int)Math.Min(buffer.Length, job.Bytes - sent);
                    int n = await source.ReadAsync(buffer, 0, want, ct).ConfigureAwait(false);
                    if (n == 0) throw new TransferFailure("SourceLengthChanged", "원본 stream이 예상보다 짧습니다.");
                    await _upload.WaitAsync(n, _options().UploadBytesPerSecond, ct).ConfigureAwait(false);
                    await data.WriteAsync(buffer, 0, n, ct).ConfigureAwait(false); sent += n;
                    progress?.Invoke(new TransferProgress { JobId = job.Id, Stage = job.Policy.PublishMode == TransferPublishMode.DirectWrite ? "Publishing" : "UploadingTemp", Sent = sent, Total = job.Bytes, BytesPerSecond = (sent - offset) / Math.Max(0.001, watch.Elapsed.TotalSeconds) });
                }
                await data.FlushAsync(ct).ConfigureAwait(false); await data.CloseAsync(ct).ConfigureAwait(false);
                var reply = await _client.GetReply(ct).ConfigureAwait(false); RequireTransferReply(reply);
                return new TransferUploadResult { Sent = sent, Reply = reply.Code };
            }
            finally { data.Dispose(); }
        }, ct);

        public Task<TransferVerificationResult> VerifyAsync(string path, long length, TransferVerification mode, Action<long> progress, CancellationToken ct) => Guard(async () =>
        {
            long size = await _client.GetFileSize(path, -1, ct).ConfigureAwait(false);
            if (size != length) throw new TransferFailure("RemoteLengthMismatch", "원격 길이가 원본/예상 길이와 다릅니다.");
            if (mode == TransferVerification.SizeOnly) return new TransferVerificationResult { Method = "SIZE + transfer acknowledgement", Level = TransferEvidenceLevel.SizeChecked };
            if (mode == TransferVerification.AutoStrong && (_client.HashAlgorithms & FtpHashAlgorithm.SHA256) != 0)
            {
                try {
                    var hash = await _client.GetChecksum(path, FtpHashAlgorithm.SHA256, ct).ConfigureAwait(false);
                    string value = hash?.Value?.ToLowerInvariant();
                    if (hash != null && hash.IsValid && hash.Algorithm == FtpHashAlgorithm.SHA256 && TransferRules.ValidSha256(value))
                        return new TransferVerificationResult { Method = "Server SHA-256", Sha256 = value, Level = TransferEvidenceLevel.ContentVerified };
                }
                catch (FtpCommandException ex) when (ex.CompletionCode != "530") { }
            }
            FtpDataStream data;
            try { data = (FtpDataStream)await _client.OpenRead(path, FtpDataType.Binary, 0, length, ct).ConfigureAwait(false); }
            catch (FtpCommandException) { throw new TransferFailure("VerificationUnavailable", "원격 SHA-256과 RETR 읽기 권한을 사용할 수 없습니다."); }
            data.ControlConnection = null;
            try
            {
                using (var sha = SHA256.Create())
                {
                    byte[] buffer = new byte[TransferIO.BufferBytes]; long read = 0;
                    while (true)
                    {
                        int want = (int)Math.Min(buffer.Length, Math.Max(1, length - read + 1));
                        await _download.WaitAsync(want, _options().DownloadBytesPerSecond, ct).ConfigureAwait(false);
                        int n = await data.ReadAsync(buffer, 0, want, ct).ConfigureAwait(false); if (n == 0) break;
                        read += n; if (read > length) throw new TransferFailure("RemoteLengthMismatch", "원격 파일이 검증 도중 늘어났습니다.");
                        sha.TransformBlock(buffer, 0, n, buffer, 0); progress?.Invoke(read);
                    }
                    await data.CloseAsync(ct).ConfigureAwait(false); RequireTransferReply(await _client.GetReply(ct).ConfigureAwait(false));
                    if (read != length) throw new TransferFailure("RemoteLengthMismatch", "원격 파일이 검증 도중 짧아졌습니다.");
                    sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                    return new TransferVerificationResult { Method = "RETR read-back SHA-256", Sha256 = TransferRules.Hex(sha.Hash), BytesRead = read, Level = TransferEvidenceLevel.ContentVerified };
                }
            }
            finally { data.Dispose(); }
        }, ct);

        public Task<string> PublishAsync(TransferJob job, CancellationToken ct) => Guard(async () =>
        {
            EnsureOwnedTemp(job);
            var final = await InspectAsync(job.RemoteFinal, ct).ConfigureAwait(false);
            if (!final.Known || final.Exists) throw new TransferFailure("Conflict", "최종 경로가 비어 있음을 확인할 수 없어 게시를 보류했습니다.");
            bool rnToSent = false;
            try {
                var from = await _client.Execute("RNFR " + TransferRules.RemotePath(job.RemoteTemp), ct).ConfigureAwait(false);
                if (from.Code != "350") throw Failure(from.Code);
                rnToSent = true;
                var to = await _client.Execute("RNTO " + TransferRules.RemotePath(job.RemoteFinal), ct).ConfigureAwait(false);
                if (to.Code != "250") throw Failure(to.Code);
                return "RNTO " + to.Code;
            }
            catch (OperationCanceledException) { throw; }
            catch (TransferFailure) { throw; }
            catch (Exception) when (rnToSent) { throw new TransferFailure("PublicationUnknown", "게시 명령의 완료 응답을 확인하지 못했습니다. 원격 상태 확인이 필요합니다."); }
        }, ct);
        public async Task RemoveOwnedTempAsync(TransferJob job, CancellationToken ct)
        {
            EnsureOwnedTemp(job);
            await Guard(async () => { await _client.DeleteFile(job.RemoteTemp, ct).ConfigureAwait(false); return true; }, ct).ConfigureAwait(false);
        }
        private static void EnsureOwnedTemp(TransferJob job)
        {
            string expected = job.Policy.PublishMode == TransferPublishMode.SameDirectoryRename ? job.RemoteFinal + ".em-" + job.Id + ".part" :
                TransferRules.RemotePath(job.Policy.StagingRoot).TrimEnd('/') + "/em-" + job.Id + ".part";
            if (job.Policy.PublishMode == TransferPublishMode.DirectWrite || job.RemoteTemp != expected || job.RemoteTemp == job.RemoteFinal)
                throw new TransferFailure("TempOwnership", "이 작업 소유의 임시 경로가 아닙니다.");
        }
        private static void RequireTransferReply(FtpReply reply)
        {
            if (reply.Code != "226" && reply.Code != "250") throw new TransferFailure("TransferReplyUnconfirmed", "FTP 최종 전송 성공 응답을 확인하지 못했습니다.", true);
        }
        public async Task<TransferCapabilities> TestUploadAsync(string root, string probeId, CancellationToken ct)
        {
            root = TransferRules.RemotePath(root); Guid parsed;
            if (!Guid.TryParseExact(probeId, "N", out parsed)) throw new ArgumentException("잘못된 시험 ID");
            var caps = await ProbeAsync(ct).ConfigureAwait(false);
            byte[] bytes = new byte[32768]; using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            string final = root.TrimEnd('/') + "/em-probe-" + probeId + ".bin";
            var policy = new TransferPolicy { PublishMode = TransferPublishMode.SameDirectoryRename, RemoteRoot = root, StagingRoot = root };
            var job = new TransferJob { Id = probeId, Policy = policy, Connection = _connection, File = new FileSnapshot { Length = bytes.Length / 2 },
                RemoteFinal = final, RemoteTemp = final + ".em-" + probeId + ".part" };
            bool published = false;
            try
            {
                await PrepareAsync(job, ct).ConfigureAwait(false);
                var exists = await InspectAsync(final, ct).ConfigureAwait(false);
                if (!exists.Known || exists.Exists) throw new TransferFailure("ProbeConflict", "시험용 최종 경로를 안전하게 사용할 수 없습니다.");
                using (var input = new MemoryStream(bytes, 0, bytes.Length / 2, false)) await UploadAsync(job, input, 0, null, ct).ConfigureAwait(false);
                var prefix = await VerifyAsync(job.RemoteTemp, bytes.Length / 2, TransferVerification.ReadBackSha256, null, ct).ConfigureAwait(false);
                caps.ReadBackVerified = prefix.Level == TransferEvidenceLevel.ContentVerified;
                job.File.Length = bytes.Length;
                using (var input = new MemoryStream(bytes, false)) { input.Position = bytes.Length / 2; await UploadAsync(job, input, input.Position, null, ct).ConfigureAwait(false); }
                var full = await VerifyAsync(job.RemoteTemp, bytes.Length, TransferVerification.ReadBackSha256, null, ct).ConfigureAwait(false);
                using (var sha = SHA256.Create()) caps.UploadResumeVerified = full.Sha256 == TransferRules.Hex(sha.ComputeHash(bytes));
                if (!caps.UploadResumeVerified) throw new TransferFailure("ProbeMismatch", "FTP append/resume 시험의 내용이 일치하지 않습니다.");
                await PublishAsync(job, ct).ConfigureAwait(false); published = true; caps.RenameVerified = true;
                caps.Detail = "32 KiB probe: STOR, RETR SHA-256, verified APPE resume, RNFR/RNTO passed. Receiver watch behavior still requires operator confirmation.";
                return caps;
            }
            finally
            {
                if (!ct.IsCancellationRequested)
                    try {
                        var temp = await InspectAsync(job.RemoteTemp, ct).ConfigureAwait(false);
                        if (temp.Known && temp.Exists) await RemoveOwnedTempAsync(job, ct).ConfigureAwait(false);
                        if (published) await Guard(async () => { await _client.DeleteFile(final, ct).ConfigureAwait(false); return true; }, ct).ConfigureAwait(false);
                    } catch (Exception) { /* Probe names contain the durable operation ID; never remove unrelated paths. */ }
            }
        }
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) _client.Dispose(); }
        private sealed class TransferFtpSession : AsyncFtpClient
        {
            public TransferFtpSession(TransferConnection c, string password) : base(c.Host, c.UserName, password, c.Port) { }
            public Task<FtpDataStream> OpenNamesAsync(string parent, CancellationToken ct) => OpenDataStreamAsync("NLST " + parent, 0, ct);
        }
    }
}
