using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using ExplorerManager.Contracts;
using ExplorerManager.Domain;
using Newtonsoft.Json;

namespace ExplorerManager.Infrastructure
{
    internal static class PrivilegedSensors
    {
        public static async Task<List<Metric>> ReadAsync(CancellationToken ct)
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(3000);
                using (var pipe = new NamedPipeClientStream(".", "ExplorerManager.Sensors.v1", PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation))
                {
                    await pipe.ConnectAsync(1000, timeout.Token).ConfigureAwait(false);
                    using (timeout.Token.Register(() => pipe.Dispose()))
                    {
                        int length = BitConverter.ToInt32(await Read(pipe, 4, timeout.Token).ConfigureAwait(false), 0);
                        if (length < 1 || length > 1024 * 1024) throw new IOException("센서 표본 크기 제한");
                        var bytes = await Read(pipe, length, timeout.Token).ConfigureAwait(false);
                        var metrics = JsonConvert.DeserializeObject<List<Metric>>(Encoding.UTF8.GetString(bytes), Protocol.Json) ?? new List<Metric>();
                        foreach (var m in metrics)
                            if (DateTime.UtcNow - m.Utc > TimeSpan.FromSeconds(20)) { m.Quality = "Stale"; m.Value = null; }
                        return metrics;
                    }
                }
            }
        }
        private static async Task<byte[]> Read(Stream stream, int count, CancellationToken ct)
        {
            var bytes = new byte[count]; int at = 0;
            while (at < count) { int n = await stream.ReadAsync(bytes, at, count - at, ct).ConfigureAwait(false); if (n == 0) throw new EndOfStreamException(); at += n; }
            return bytes;
        }
    }
}
