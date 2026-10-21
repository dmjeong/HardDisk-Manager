using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using ExplorerManager.Contracts;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ExplorerManager.Client
{
    public sealed class PipeClient : IExplorerClient
    {
        public async Task<T> SendAsync<T>(string command, object data = null, CancellationToken cancellation = default(CancellationToken))
        {
            var request = new Request { Command = command, Data = data == null ? new JObject() : JObject.FromObject(data) };
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                uint expectedPid = EndpointVerifier.ServicePid();
                bool impersonation = command == "PrepareUnlock" || command == "ValidateUnlock";
                using (var identityProbe = impersonation ? new NamedPipeClientStream(".", Protocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification) : null)
                using (var pipe = new NamedPipeClientStream(".", Protocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous,
                    impersonation ? TokenImpersonationLevel.Impersonation : TokenImpersonationLevel.Identification))
                {
                    if (identityProbe != null)
                    {
                        await identityProbe.ConnectAsync(3000, timeout.Token).ConfigureAwait(false);
                        EndpointVerifier.Verify(identityProbe, expectedPid);
                    }
                    await pipe.ConnectAsync(3000, timeout.Token).ConfigureAwait(false);
                    EndpointVerifier.Verify(pipe, expectedPid);
                    byte[] payload = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(request, Protocol.Json));
                    if (payload.Length > Protocol.MaxFrameBytes) throw new InvalidOperationException("요청 크기 제한을 초과했습니다.");
                    using (timeout.Token.Register(() => pipe.Dispose()))
                    {
                        await pipe.WriteAsync(BitConverter.GetBytes(payload.Length), 0, 4, timeout.Token).ConfigureAwait(false);
                        await pipe.WriteAsync(payload, 0, payload.Length, timeout.Token).ConfigureAwait(false);
                        await pipe.FlushAsync(timeout.Token).ConfigureAwait(false);
                        byte[] header = await ReadExact(pipe, 4, timeout.Token).ConfigureAwait(false);
                        int length = BitConverter.ToInt32(header, 0);
                        if (length < 1 || length > Protocol.MaxFrameBytes) throw new IOException("잘못된 응답 길이입니다.");
                        byte[] body = await ReadExact(pipe, length, timeout.Token).ConfigureAwait(false);
                        var response = JsonConvert.DeserializeObject<Response>(Encoding.UTF8.GetString(body), Protocol.Json);
                        if (response == null || response.Id != request.Id) throw new IOException("응답 ID가 일치하지 않습니다.");
                        if (!response.Success) throw new InvalidOperationException(response.Error);
                        return response.Data == null || response.Data.Type == JTokenType.Null ? default(T) : response.Data.ToObject<T>(JsonSerializer.Create(Protocol.Json));
                    }
                }
            }
        }
        private static async Task<byte[]> ReadExact(Stream stream, int count, CancellationToken ct)
        {
            var buffer = new byte[count]; int at = 0;
            while (at < count) { int read = await stream.ReadAsync(buffer, at, count - at, ct).ConfigureAwait(false); if (read == 0) throw new EndOfStreamException(); at += read; }
            return buffer;
        }
        public async Task CloseProcessesAsync(UnlockTicket ticket, bool force, CancellationToken cancellation)
        {
            // Revalidate server-side immediately before any local termination.
            var validated = await SendAsync<UnlockTicket>("ValidateUnlock", new { ticket.Id }, cancellation).ConfigureAwait(false);
            string result;
            try { result = await ProcessTermination.ExecuteAsync(validated, force, cancellation).ConfigureAwait(false); }
            catch (Exception ex)
            {
                await SendAsync<object>("CompleteUnlock", new { ticket.Id, Result = ex.Message, Retry = false, Force = force }, cancellation).ConfigureAwait(false);
                throw;
            }
            await SendAsync<object>("CompleteUnlock", new { ticket.Id, Result = result, Retry = true, Force = force }, cancellation).ConfigureAwait(false);
        }
    }
}
