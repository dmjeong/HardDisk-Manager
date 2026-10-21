using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ExplorerManager.Application;
using ExplorerManager.Domain;
using Microsoft.Win32.SafeHandles;

namespace ExplorerManager.Infrastructure
{
    public static class PathGuard
    {
        public static string Normalize(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path.Length > 32000) throw new ArgumentException("경로가 비어 있거나 너무 깁니다.");
            path = NativeMethods.Plain(path);
            if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.Length < 3 || path[1] != ':' || path[2] != '\\' || path.IndexOf(':', 2) >= 0)
                throw new ArgumentException("로컬 절대 NTFS 경로만 지원합니다.");
            string full = Path.GetFullPath(path);
            return full.Length == 3 ? full : full.TrimEnd('\\');
        }
        public static bool Within(string path, string root) => string.Equals(path.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
        public static bool Protected(string path)
        {
            foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ExplorerManager") })
                if (!string.IsNullOrEmpty(root) && Within(path, root)) return true;
            var parts = path.Split('\\');
            return parts.Any(x => x.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase) ||
                x.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase) || x.Equals("Recovery", StringComparison.OrdinalIgnoreCase));
        }
        public static void ValidatePolicy(DrivePolicy p, IVolumeCatalog volumes)
        {
            var errors = PolicyRules.Validate(p); if (errors.Count != 0) throw new ArgumentException(string.Join("\n", errors));
            var volume = volumes.Get(p.VolumeId);
            if (volume.FileSystem != "NTFS") throw new ArgumentException("v1은 NTFS만 지원합니다.");
            if (p.ThresholdMode != ThresholdMode.UsedPercent && Math.Max(p.StartThreshold, p.StopThreshold) > volume.TotalBytes)
                throw new ArgumentException("용량 기준이 볼륨 전체 크기를 초과합니다.");
            var roots = p.Roots.Select(Normalize).ToList();
            foreach (string root in roots)
            {
                if (root.Length <= 3 || Protected(root) || !Directory.Exists(root)) throw new ArgumentException("등록할 수 없는 루트: " + root);
                if (!string.Equals(WindowsVolumeCatalog.Read(Path.GetPathRoot(root)).Id, p.VolumeId, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("정책과 다른 볼륨의 경로입니다.");
                using (var guards = new DirectoryGuards(root)) { }
            }
            for (int i = 0; i < roots.Count; i++)
                for (int j = i + 1; j < roots.Count; j++) if (Within(roots[i], roots[j]) || Within(roots[j], roots[i])) throw new ArgumentException("중복 또는 중첩 루트입니다.");
            p.Roots = roots;
            p.Exclusions = (p.Exclusions ?? new List<string>()).Select(Normalize).ToList();
        }
        public static bool Excluded(string path, DrivePolicy p)
        {
            if (Protected(path) || p.Exclusions.Any(x => Within(path, x))) return true;
            return p.ExcludedExtensions.Any(x => string.Equals(Path.GetExtension(path), x.StartsWith(".") ? x : "." + x, StringComparison.OrdinalIgnoreCase));
        }
    }
    internal sealed class DirectoryGuards : IDisposable
    {
        private readonly List<SafeFileHandle> _handles = new List<SafeFileHandle>();
        public DirectoryGuards(string directory)
        {
            try
            {
                string normalized = PathGuard.Normalize(directory);
                string current = Path.GetPathRoot(normalized);
                var paths = new List<string> { current };
                foreach (string part in normalized.Substring(current.Length).Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries))
                { current = Path.Combine(current, part); paths.Add(current); }
                foreach (string path in paths)
                {
                    var h = NativeMethods.CreateFileW(NativeMethods.LongPath(path), NativeMethods.ReadAttributes, path.Length <= 3 ? 3u : 1u,
                        IntPtr.Zero, 3, NativeMethods.BackupSemantics | NativeMethods.OpenReparsePoint, IntPtr.Zero);
                    if (h.IsInvalid) { h.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
                    _handles.Add(h);
                    NativeMethods.FileInfo info; NativeMethods.Check(NativeMethods.GetFileInformationByHandle(h, out info));
                    if ((info.Attributes & 0x400) != 0 || (info.Attributes & 0x10) == 0) throw new IOException("Reparse 경유 또는 디렉터리 교체를 거부했습니다.");
                    if (!string.Equals(NativeMethods.FinalPath(h), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) throw new IOException("최종 경로가 일치하지 않습니다.");
                    // Per-directory case sensitivity is a separate, unsupported NTFS mode.
                    IntPtr flags = Marshal.AllocHGlobal(4);
                    try
                    {
                        if (NativeMethods.GetFileInformationByHandleEx(h, 23, flags, 4) && (Marshal.ReadInt32(flags) & 1) != 0)
                            throw new IOException("대소문자 구분 디렉터리는 자동 관리에서 제외합니다.");
                    }
                    finally { Marshal.FreeHGlobal(flags); }
                }
            }
            catch { Dispose(); throw; }
        }
        public void Dispose() { for (int i = _handles.Count - 1; i >= 0; i--) _handles[i].Dispose(); _handles.Clear(); }
    }
    public sealed class WindowsFileSystem : IFileSystem
    {
        private readonly IVolumeCatalog _volumes;
        private readonly Dictionary<string, FileSnapshot> _directories = new Dictionary<string, FileSnapshot>(StringComparer.OrdinalIgnoreCase);
        public WindowsFileSystem(IVolumeCatalog volumes) { _volumes = volumes; }
        public Task ScanAsync(DrivePolicy p, DateTime cutoff, string runId, IJournal journal, CancellationToken cancellation) => Task.Run(() =>
        {
            PathGuard.ValidatePolicy(p, _volumes); _directories.Clear();
            var watch = Stopwatch.StartNew(); int visited = 0, skipped = 0;
            var stack = new Stack<Tuple<string, string>>();
            foreach (string root in p.Roots) stack.Push(Tuple.Create(root, root));
            while (stack.Count > 0)
            {
                cancellation.ThrowIfCancellationRequested();
                if (watch.Elapsed.TotalSeconds > p.MaxScanSeconds) throw new TimeoutException("전체 경로 스캔 제한 시간을 초과했습니다. 부분 목록은 삭제하지 않습니다.");
                var dir = stack.Pop();
                try
                {
                    using (var guards = new DirectoryGuards(dir.Item1))
                    {
                        var children = Directory.EnumerateFileSystemEntries(NativeMethods.LongPath(dir.Item1));
                        foreach (string raw in children)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            string path = PathGuard.Normalize(raw);
                            if (PathGuard.Excluded(path, p)) { skipped++; continue; }
                            FileSnapshot f;
                            try { f = Snapshot(path, dir.Item2, p, false); }
                            catch (Exception ex) when (ex is IOException || ex is Win32Exception || ex is UnauthorizedAccessException)
                            {
                                skipped++;
                                int error = ex is Win32Exception ? ((Win32Exception)ex).NativeErrorCode : ex.HResult & 0xffff;
                                journal.RecordItem(new ItemRecord { RunId = runId, PolicyId = p.Id, Utc = DateTime.UtcNow, Actor = "Scanner",
                                    File = new FileSnapshot { Path = path, Root = dir.Item2, VolumeId = p.VolumeId }, State = "Skipped", Reason = ex.Message, ErrorCode = error });
                                if (error == 32 || error == 33) journal.SaveAlert(new AlertRecord { Utc = DateTime.UtcNow, Category = "LockedFile",
                                    Severity = "Warning", PolicyId = p.Id, Path = path, Message = "사용 중인 항목의 메타데이터를 읽지 못해 건너뛰었습니다.",
                                    Processes = new RestartManagerInspector().Inspect(path).ToList() });
                                continue;
                            }
                            visited++;
                            if (f.UnsupportedReason != null) { skipped++; continue; }
                            if (f.IsDirectory)
                            {
                                _directories[path] = f; stack.Push(Tuple.Create(path, dir.Item2));
                                if (PolicyRules.IsExpired(f, p, cutoff) && !Directory.EnumerateFileSystemEntries(NativeMethods.LongPath(path)).Any()) journal.AddCandidate(runId, f);
                            }
                            else if (PolicyRules.IsExpired(f, p, cutoff) && !journal.IsProtected(f, DateTime.UtcNow)) journal.AddCandidate(runId, f);
                            if (visited % 500 == 0) { cancellation.WaitHandle.WaitOne(5); }
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is Win32Exception || ex is UnauthorizedAccessException)
                {
                    journal.SaveAlert(new AlertRecord { Utc = DateTime.UtcNow, Category = "ScanError", Severity = "Warning", Path = dir.Item1, PolicyId = p.Id, Message = ex.Message });
                    // An incomplete namespace cannot establish globally oldest order.
                    throw new IOException("스캔을 완료할 수 없어 이번 정리를 중단했습니다: " + dir.Item1, ex);
                }
            }
            journal.RecordEvent("ScanComplete", "Service", runId, "visited=" + visited + "; excluded/unsupported=" + skipped);
        }, cancellation);

        private FileSnapshot Snapshot(string path, string root, DrivePolicy policy, bool withDacl)
        {
            using (var h = NativeMethods.CreateFileW(NativeMethods.LongPath(path), NativeMethods.ReadAttributes | (withDacl ? NativeMethods.ReadControl : 0),
                7, IntPtr.Zero, 3, NativeMethods.BackupSemantics | NativeMethods.OpenReparsePoint, IntPtr.Zero))
            {
                if (h.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
                return ReadSnapshot(h, path, root, policy, withDacl);
            }
        }
        public FileSnapshot Inspect(FileSnapshot file, DrivePolicy policy)
        {
            if (!policy.Roots.Any(root => PathGuard.Within(file.Path, root)) || PathGuard.Excluded(file.Path, policy)) throw new IOException("관리 범위 밖입니다.");
            using (var guards = new DirectoryGuards(Path.GetDirectoryName(file.Path)))
                return Snapshot(file.Path, file.Root, policy, false);
        }
        private static FileSnapshot ReadSnapshot(SafeFileHandle h, string path, string root, DrivePolicy policy, bool withDacl)
        {
            NativeMethods.FileInfo info; NativeMethods.Check(NativeMethods.GetFileInformationByHandle(h, out info));
            var f = new FileSnapshot { Path = path, Root = root, ParentPath = Path.GetDirectoryName(path), VolumeId = policy.VolumeId,
                FileId = NativeMethods.Id(h), IsDirectory = (info.Attributes & 0x10) != 0, Attributes = (int)info.Attributes,
                CreationUtc = NativeMethods.Time(info.Creation), LastWriteUtc = NativeMethods.Time(info.Write), LinkCount = (int)info.Links };
            f.Length = f.IsDirectory ? 0 : ((long)info.SizeHigh << 32) | info.SizeLow;
            f.AgeUtc = policy.AgeBasis == AgeBasis.CreationTime ? f.CreationUtc : f.LastWriteUtc;
            const uint unsupported = 0x400 | 0x4000 | 0x200 | 0x800 | 0x1 | 0x2 | 0x4 | 0x1000 | 0x400000;
            if ((info.Attributes & unsupported) != 0 || (!f.IsDirectory && info.Links > 1)) f.UnsupportedReason = "특수 속성 또는 다중 hard link";
            if (withDacl) f.Dacl = NativeMethods.ReadDacl(h);
            return f;
        }
        public IFileLease OpenVerified(FileSnapshot expected, DrivePolicy policy)
        {
            string path = PathGuard.Normalize(expected.Path);
            if (!policy.Roots.Any(root => PathGuard.Within(path, root) && !string.Equals(path, root, StringComparison.OrdinalIgnoreCase)) ||
                PathGuard.Excluded(path, policy)) throw new IOException("관리 범위 밖 또는 보호 경로입니다.");
            _volumes.Get(policy.VolumeId);
            var guards = new DirectoryGuards(Path.GetDirectoryName(path));
            SafeFileHandle h = null;
            try
            {
                h = NativeMethods.CreateFileW(NativeMethods.LongPath(path), NativeMethods.ReadAttributes | NativeMethods.ReadControl | NativeMethods.Delete |
                    (expected.IsDirectory ? 1u : NativeMethods.GenericRead), 0, IntPtr.Zero, 3,
                    NativeMethods.BackupSemantics | NativeMethods.OpenReparsePoint, IntPtr.Zero);
                if (h.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (!string.Equals(NativeMethods.FinalPath(h), path, StringComparison.OrdinalIgnoreCase)) throw new FileChangedException("파일 경로가 변경되었습니다.");
                var current = ReadSnapshot(h, path, expected.Root, policy, true);
                if (current.UnsupportedReason != null) throw new IOException(current.UnsupportedReason);
                if (current.FileId != expected.FileId || current.Length != expected.Length || current.CreationUtc != expected.CreationUtc ||
                    (!current.IsDirectory && current.LastWriteUtc != expected.LastWriteUtc)) throw new FileChangedException("스캔 후 파일이 변경되었습니다.");
                if (current.IsDirectory)
                {
                    current.AgeUtc = expected.AgeUtc; current.LastWriteUtc = expected.LastWriteUtc;
                    if (!Empty(h)) throw new IOException("폴더가 비어 있지 않습니다.");
                }
                else EnsureNoAlternateStreams(h);
                return new FileLease(h, guards, current);
            }
            catch { h?.Dispose(); guards.Dispose(); throw; }
        }
        private static bool Empty(SafeFileHandle h)
        {
            IntPtr buffer = Marshal.AllocHGlobal(65536);
            try
            {
                bool restart = true;
                while (true)
                {
                    bool ok = NativeMethods.GetFileInformationByHandleEx(h, restart ? 11 : 10, buffer, 65536);
                    restart = false;
                    if (!ok) { int error = Marshal.GetLastWin32Error(); if (error == 18 || error == 2) return true; throw new Win32Exception(error); }
                    int offset = 0;
                    while (true)
                    {
                        int next = Marshal.ReadInt32(buffer, offset), length = Marshal.ReadInt32(buffer, offset + 60);
                        if (length < 0 || length > 64000 || offset + 104 + length > 65536) throw new IOException("폴더 응답 범위 오류");
                        string name = Marshal.PtrToStringUni(IntPtr.Add(buffer, offset + 104), length / 2);
                        if (name != "." && name != "..") return false;
                        if (next == 0) break;
                        if (next < 104 || offset + next >= 65536) throw new IOException("폴더 응답 구조 오류");
                        offset += next;
                    }
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        private static void EnsureNoAlternateStreams(SafeFileHandle h)
        {
            IntPtr buffer = Marshal.AllocHGlobal(65536);
            try
            {
                NativeMethods.Check(NativeMethods.GetFileInformationByHandleEx(h, 7, buffer, 65536));
                int offset = 0;
                while (true)
                {
                    int next = Marshal.ReadInt32(buffer, offset), length = Marshal.ReadInt32(buffer, offset + 4);
                    if (length < 0 || offset + 24 + length > 65536) throw new IOException("스트림 메타데이터 범위 초과");
                    if (Marshal.PtrToStringUni(IntPtr.Add(buffer, offset + 24), length / 2) != "::$DATA")
                        throw new IOException("대체 데이터 스트림이 있는 파일은 자동 삭제하지 않습니다.");
                    if (next == 0) break;
                    if (next < 24 || offset + next >= 65536) throw new IOException("스트림 구조 오류");
                    offset += next;
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        private sealed class FileLease : IFileLease
        {
            private readonly SafeFileHandle _handle;
            private readonly DirectoryGuards _guards;
            private readonly FileStream _stream;
            public FileSnapshot Snapshot { get; }
            public Stream Content => (Stream)_stream ?? Stream.Null;
            public FileLease(SafeFileHandle handle, DirectoryGuards guards, FileSnapshot snapshot)
            {
                _handle = handle; _guards = guards; Snapshot = snapshot;
                if (!snapshot.IsDirectory) _stream = new FileStream(handle, FileAccess.Read, 65536, false);
            }
            public void MarkForDeletion()
            {
                if (Snapshot.IsDirectory && !Empty(_handle)) throw new IOException("삭제 직전 폴더가 비어 있지 않습니다.");
                IntPtr info = Marshal.AllocHGlobal(1);
                try { Marshal.WriteByte(info, 1); NativeMethods.Check(NativeMethods.SetFileInformationByHandle(_handle, 4, info, 1)); }
                finally { Marshal.FreeHGlobal(info); }
            }
            public void Dispose() { _stream?.Dispose(); _handle.Dispose(); _guards.Dispose(); }
        }
        public bool IsAbsent(FileSnapshot file)
        {
            using (var h = NativeMethods.CreateFileW(NativeMethods.LongPath(file.Path), NativeMethods.ReadAttributes, 7, IntPtr.Zero, 3,
                NativeMethods.BackupSemantics | NativeMethods.OpenReparsePoint, IntPtr.Zero))
            {
                if (!h.IsInvalid) return false;
                int error = Marshal.GetLastWin32Error(); return error == 2 || error == 3;
            }
        }
        public FileSnapshot FindEmptyParent(FileSnapshot deleted, DrivePolicy policy, DateTime cutoff)
        {
            FileSnapshot parent;
            if (!_directories.TryGetValue(deleted.ParentPath ?? "", out parent) || parent.AgeUtc >= cutoff) return null;
            try
            {
                using (var lease = OpenVerified(parent, policy)) return parent;
            }
            catch (Exception ex) when (ex is IOException || ex is Win32Exception || ex is UnauthorizedAccessException) { return null; }
        }
        public async Task<FileSnapshot> RestoreAsync(FileSnapshot original, Stream content, string expectedHash, string targetDirectory, CancellationToken cancellation)
        {
            string parent = PathGuard.Normalize(targetDirectory ?? original.ParentPath);
            if (PathGuard.Protected(parent) || parent.Length <= 3) throw new IOException("보호 경로에는 복구할 수 없습니다.");
            if (WindowsVolumeCatalog.Read(Path.GetPathRoot(parent)).FileSystem != "NTFS") throw new IOException("NTFS 복구 대상이 필요합니다.");
            EnsureDirectories(parent);
            using (var guards = new DirectoryGuards(parent))
            {
                string target = Path.Combine(parent, Path.GetFileName(original.Path));
                if (File.Exists(NativeMethods.LongPath(target)) || Directory.Exists(NativeMethods.LongPath(target)))
                    target += ".restored-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                string temp = original.IsDirectory ? target : Path.Combine(parent, ".explorermanager-" + Guid.NewGuid().ToString("N") + ".tmp");
                SafeFileHandle handle = null;
                bool published = false;
                string createdId = null;
                try
                {
                    if (original.IsDirectory)
                    {
                        CreateDirectoryWithDacl(temp, original.Dacl);
                        handle = NativeMethods.CreateFileW(NativeMethods.LongPath(temp), NativeMethods.GenericRead | NativeMethods.GenericWrite | NativeMethods.Delete | NativeMethods.WriteDac,
                            0, IntPtr.Zero, 3, NativeMethods.BackupSemantics | NativeMethods.OpenReparsePoint, IntPtr.Zero);
                    }
                    else handle = CreatePrivateFile(temp);
                    if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
                    createdId = NativeMethods.Id(handle);
                    if (!original.IsDirectory)
                    {
                        using (var output = new FileStream(handle, FileAccess.ReadWrite, 65536, false))
                        using (var hash = SHA256.Create())
                        {
                            var buffer = new byte[65536]; int n; long total = 0;
                            while ((n = await content.ReadAsync(buffer, 0, buffer.Length, cancellation).ConfigureAwait(false)) != 0)
                            {
                                await output.WriteAsync(buffer, 0, n, cancellation).ConfigureAwait(false);
                                hash.TransformBlock(buffer, 0, n, buffer, 0); total += n;
                            }
                            hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                            if (total != original.Length || BitConverter.ToString(hash.Hash).Replace("-", "").ToLowerInvariant() != expectedHash)
                                throw new IOException("복구 본문 무결성 확인에 실패했습니다.");
                            output.Flush(true);
                            SetMetadataAndPublish(handle, original, target);
                            published = true;
                        }
                    }
                    else { SetMetadataAndPublish(handle, original, target, false); published = true; handle.Dispose(); }
                    var policy = new DrivePolicy { VolumeId = WindowsVolumeCatalog.Read(Path.GetPathRoot(target)).Id };
                    return Snapshot(target, targetDirectory ?? original.Root, policy, true);
                }
                finally
                {
                    handle?.Dispose();
                    if (!published && createdId != null)
                    {
                        // Delete only the output object this restore created, even after a path replacement.
                        try
                        {
                            using (var cleanup = NativeMethods.CreateFileW(NativeMethods.LongPath(temp), NativeMethods.Delete | NativeMethods.ReadAttributes, 0, IntPtr.Zero, 3,
                                NativeMethods.BackupSemantics | NativeMethods.OpenReparsePoint, IntPtr.Zero))
                                if (!cleanup.IsInvalid && NativeMethods.Id(cleanup) == createdId)
                                {
                                    IntPtr disposition = Marshal.AllocHGlobal(1);
                                    try { Marshal.WriteByte(disposition, 1); NativeMethods.SetFileInformationByHandle(cleanup, 4, disposition, 1); }
                                    finally { Marshal.FreeHGlobal(disposition); }
                                }
                        }
                        catch { }
                    }
                }
            }
        }
        private static void SetMetadataAndPublish(SafeFileHandle handle, FileSnapshot original, string target, bool rename = true)
        {
            long created = original.CreationUtc.ToFileTimeUtc(), modified = original.LastWriteUtc.ToFileTimeUtc();
            NativeMethods.Check(NativeMethods.SetFileTime(handle, ref created, IntPtr.Zero, ref modified));
            IntPtr basic = Marshal.AllocHGlobal(40);
            try
            {
                for (int i = 0; i < 40; i++) Marshal.WriteByte(basic, i, 0);
                int attributes = original.Attributes & 0x2120;
                Marshal.WriteInt32(basic, 32, attributes == 0 ? 0x80 : attributes);
                NativeMethods.Check(NativeMethods.SetFileInformationByHandle(handle, 0, basic, 40));
            }
            finally { Marshal.FreeHGlobal(basic); }
            NativeMethods.ApplyDacl(handle, original.Dacl);
            if (!rename) return;
            byte[] name = Encoding.Unicode.GetBytes(NativeMethods.LongPath(target));
            IntPtr info = Marshal.AllocHGlobal(20 + name.Length);
            try
            {
                for (int i = 0; i < 20; i++) Marshal.WriteByte(info, i, 0);
                Marshal.WriteIntPtr(info, 8, IntPtr.Zero); Marshal.WriteInt32(info, 16, name.Length);
                Marshal.Copy(name, 0, IntPtr.Add(info, 20), name.Length);
                NativeMethods.Check(NativeMethods.SetFileInformationByHandle(handle, 3, info, (uint)(20 + name.Length)));
            }
            finally { Marshal.FreeHGlobal(info); }
        }
        private static SafeFileHandle CreatePrivateFile(string path)
        {
            string sid = WindowsIdentity.GetCurrent().User.Value;
            IntPtr sd; uint size;
            NativeMethods.Check(NativeMethods.ConvertStringSecurityDescriptorToSecurityDescriptorW("D:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FA;;;" + sid + ")", 1, out sd, out size));
            IntPtr sa = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(NativeMethods.SecurityAttributes)));
            try
            {
                Marshal.StructureToPtr(new NativeMethods.SecurityAttributes { Length = Marshal.SizeOf(typeof(NativeMethods.SecurityAttributes)), Descriptor = sd }, sa, false);
                var h = NativeMethods.CreateFileW(NativeMethods.LongPath(path), NativeMethods.GenericRead | NativeMethods.GenericWrite | NativeMethods.Delete | NativeMethods.WriteDac,
                    0, sa, 1, 0x80, IntPtr.Zero);
                if (h.IsInvalid) { h.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
                return h;
            }
            finally { Marshal.FreeHGlobal(sa); NativeMethods.LocalFree(sd); }
        }
        private static void CreateDirectoryWithDacl(string path, string dacl)
        {
            IntPtr sd; uint length;
            NativeMethods.Check(NativeMethods.ConvertStringSecurityDescriptorToSecurityDescriptorW(dacl, 1, out sd, out length));
            IntPtr sa = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(NativeMethods.SecurityAttributes)));
            try
            {
                Marshal.StructureToPtr(new NativeMethods.SecurityAttributes { Length = Marshal.SizeOf(typeof(NativeMethods.SecurityAttributes)), Descriptor = sd }, sa, false);
                NativeMethods.Check(NativeMethods.CreateDirectoryW(NativeMethods.LongPath(path), sa));
            }
            finally { Marshal.FreeHGlobal(sa); NativeMethods.LocalFree(sd); }
        }
        private static void EnsureDirectories(string path)
        {
            string current = Path.GetPathRoot(path);
            foreach (string part in path.Substring(current.Length).Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries))
            {
                using (var guards = new DirectoryGuards(current))
                {
                    string child = Path.Combine(current, part);
                    if (!Directory.Exists(NativeMethods.LongPath(child)))
                        NativeMethods.Check(NativeMethods.CreateDirectoryW(NativeMethods.LongPath(child), IntPtr.Zero));
                    using (var verify = new DirectoryGuards(child)) { }
                    current = child;
                }
            }
        }
    }
}
