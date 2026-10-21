using System;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ExplorerManager.Contracts;
using Newtonsoft.Json;
#if NETFRAMEWORK
using System.Security.AccessControl;
using System.Runtime.InteropServices;
#endif

namespace ExplorerManager.Infrastructure
{
    public sealed class Caller
    {
        public string Sid { get; set; }
        public string Name { get; set; }
        public int Session { get; set; }
        public bool Operator { get; set; }
    }
    public sealed class PipeServer
    {
        private readonly Func<Request, Caller, Response> _execute;
        private readonly Action<Exception> _error;
        private readonly SemaphoreSlim _clients = new SemaphoreSlim(8);
        public PipeServer(Func<Request, Caller, Response> execute, Action<Exception> error) { _execute = execute; _error = error; }
        public async Task RunAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                await _clients.WaitAsync(ct).ConfigureAwait(false);
                NamedPipeServerStream pipe = null;
                try
                {
                    pipe = Create();
                    await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                    var connected = pipe; pipe = null;
                    _ = HandleAsync(connected, ct).ContinueWith(t => { if (t.Exception != null) _error(t.Exception); _clients.Release(); }, TaskScheduler.Default);
                }
                catch { pipe?.Dispose(); _clients.Release(); throw; }
            }
        }
        private static NamedPipeServerStream Create()
        {
#if NETFRAMEWORK
            var security = new PipeSecurity(); security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User, PipeAccessRights.FullControl, AccessControlType.Allow));
            return new NamedPipeServerStream(Protocol.PipeName, PipeDirection.InOut, 10, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 65536, 65536, security);
#else
            throw new PlatformNotSupportedException("서비스 IPC 호스트는 .NET Framework 4.8 Windows에서 실행합니다.");
#endif
        }
#if NETFRAMEWORK
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(IntPtr token, int type, out int value, int length, out int needed);
#endif
        private static Caller Authenticate(NamedPipeServerStream pipe)
        {
            Caller result = null;
#if NETFRAMEWORK
            pipe.RunAsClient(() =>
            {
                using (var identity = WindowsIdentity.GetCurrent(true))
                {
                    if (identity == null) throw new UnauthorizedAccessException("사용자 토큰이 없습니다.");
                    var principal = new WindowsPrincipal(identity);
                    if (principal.IsInRole(new SecurityIdentifier(WellKnownSidType.NetworkSid, null))) throw new UnauthorizedAccessException("원격 Pipe 연결은 허용하지 않습니다.");
                    int session, needed;
                    if (!GetTokenInformation(identity.Token, 12, out session, 4, out needed)) throw new UnauthorizedAccessException("사용자 세션을 확인할 수 없습니다.");
                    result = new Caller { Sid = identity.User.Value, Name = identity.Name, Session = session, Operator = principal.IsInRole(WindowsBuiltInRole.Administrator) };
                }
            });
#endif
            if (result == null) throw new UnauthorizedAccessException();
            return result;
        }
        private async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken stopping)
        {
            using (pipe)
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                try
                {
                    using (timeout.Token.Register(() => pipe.Dispose()))
                    {
                        byte[] header = await Read(pipe, 4, timeout.Token).ConfigureAwait(false);
                        int length = BitConverter.ToInt32(header, 0);
                        if (length < 1 || length > Protocol.MaxFrameBytes) throw new IOException("요청 프레임 크기 제한");
                        var body = await Read(pipe, length, timeout.Token).ConfigureAwait(false);
                        var request = JsonConvert.DeserializeObject<Request>(Encoding.UTF8.GetString(body), Protocol.Json);
                        if (request == null || request.Version != Protocol.Version || string.IsNullOrWhiteSpace(request.Id) || request.Id.Length > 64 || request.Data == null)
                            throw new IOException("지원하지 않는 프로토콜 또는 요청입니다.");
                        var caller = Authenticate(pipe);
                        Response response;
                        try { response = _execute(request, caller); }
                        catch (Exception ex) { response = new Response { Id = request.Id, Error = ex.Message }; _error(ex); }
                        byte[] encoded = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(response, Protocol.Json));
                        if (encoded.Length > Protocol.MaxFrameBytes) throw new IOException("응답 크기 제한을 초과했습니다. 조회 범위를 줄이세요.");
                        await pipe.WriteAsync(BitConverter.GetBytes(encoded.Length), 0, 4, timeout.Token).ConfigureAwait(false);
                        await pipe.WriteAsync(encoded, 0, encoded.Length, timeout.Token).ConfigureAwait(false);
                        await pipe.FlushAsync(timeout.Token).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is OperationCanceledException || ex is ObjectDisposedException || ex is UnauthorizedAccessException)
                { if (!stopping.IsCancellationRequested) _error(ex); }
            }
        }
        private static async Task<byte[]> Read(Stream stream, int size, CancellationToken ct)
        {
            var bytes = new byte[size]; int at = 0;
            while (at < size) { int n = await stream.ReadAsync(bytes, at, size - at, ct).ConfigureAwait(false); if (n == 0) throw new EndOfStreamException(); at += n; }
            return bytes;
        }
    }
}
