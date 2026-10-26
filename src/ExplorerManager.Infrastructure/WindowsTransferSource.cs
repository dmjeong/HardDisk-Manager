using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using ExplorerManager.Application;
using ExplorerManager.Domain;
using Microsoft.Win32.SafeHandles;

namespace ExplorerManager.Infrastructure
{
    public sealed class WindowsTransferSource : ITransferSource
    {
        private readonly IVolumeCatalog _volumes;
        public WindowsTransferSource(IVolumeCatalog volumes) { _volumes = volumes; }
        public IEnumerable<FileSnapshot> Enumerate(TransferPolicy policy, CancellationToken ct)
        {
            ValidatePolicy(policy); var stack = new Stack<string>(); stack.Push(policy.LocalRoot); var watch = Stopwatch.StartNew();
            var p = new DrivePolicy { VolumeId = policy.VolumeId };
            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested(); string directory = stack.Pop();
                using (var guards = new DirectoryGuards(directory))
                foreach (string raw in Directory.EnumerateFileSystemEntries(NativeMethods.LongPath(directory)))
                {
                    ct.ThrowIfCancellationRequested();
                    if (watch.Elapsed.TotalSeconds >= policy.MaxScanSeconds) throw new TransferFailure("ScanTimeout", "전송 전체 탐색 시간 한도를 초과했습니다. 완료로 기록하지 않습니다.", true);
                    string path = PathGuard.Normalize(raw);
                    if (PathGuard.Protected(path) || policy.Exclusions.Exists(x => PathGuard.Within(path, x))) continue;
                    FileSnapshot f;
                    using (var h = NativeMethods.CreateFileW(NativeMethods.LongPath(path), NativeMethods.ReadAttributes, 7, IntPtr.Zero, 3,
                        NativeMethods.BackupSemantics | NativeMethods.OpenReparsePoint, IntPtr.Zero))
                    {
                        if (h.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
                        f = WindowsFileSystem.ReadSnapshot(h, path, policy.LocalRoot, p, false);
                    }
                    if (f.IsDirectory) { if (policy.IncludeSubdirectories && f.UnsupportedReason == null) stack.Push(path); }
                    else if (TransferRules.Matches(policy, f)) yield return f;
                }
            }
        }
        public void ValidatePolicy(TransferPolicy policy)
        {
            policy.LocalRoot = PathGuard.Normalize(policy.LocalRoot);
            policy.Exclusions = policy.Exclusions.ConvertAll(PathGuard.Normalize);
            TransferRules.ValidatePolicy(policy);
            var p = new DrivePolicy { VolumeId = policy.VolumeId, Roots = new List<string> { policy.LocalRoot } };
            PathGuard.ValidatePolicy(p, _volumes);
        }
        public FileSnapshot Inspect(TransferPolicy policy, string path)
        {
            path = PathGuard.Normalize(path);
            if (!PathGuard.Within(path, policy.LocalRoot) || PathGuard.Protected(path)) return null;
            using (var guards = new DirectoryGuards(Path.GetDirectoryName(path)))
            using (var h = NativeMethods.CreateFileW(NativeMethods.LongPath(path), NativeMethods.ReadAttributes, 7, IntPtr.Zero, 3,
                NativeMethods.BackupSemantics | NativeMethods.OpenReparsePoint, IntPtr.Zero))
            {
                if (h.IsInvalid) { int e = Marshal.GetLastWin32Error(); if (e == 2 || e == 3) return null; throw new Win32Exception(e); }
                return WindowsFileSystem.ReadSnapshot(h, path, policy.LocalRoot, new DrivePolicy { VolumeId = policy.VolumeId }, false);
            }
        }
        public ITransferReadLease OpenVerified(FileSnapshot expected)
        {
            var volume = _volumes.Get(expected.VolumeId);
            if (volume.FileSystem != "NTFS" || !string.Equals(WindowsVolumeCatalog.Read(Path.GetPathRoot(expected.Path)).Id, expected.VolumeId, StringComparison.OrdinalIgnoreCase))
                throw new TransferFailure("VolumeChanged", "원본 NTFS 볼륨 식별자가 변경되었습니다.");
            string path = PathGuard.Normalize(expected.Path);
            if (PathGuard.Protected(path) || !PathGuard.Within(path, expected.Root)) throw new TransferFailure("SourceOutsideRoot", "원본 경로가 관리 범위를 벗어납니다.");
            var guards = new DirectoryGuards(Path.GetDirectoryName(path)); SafeFileHandle handle = null;
            try
            {
                handle = NativeMethods.CreateFileW(NativeMethods.LongPath(path), NativeMethods.GenericRead | NativeMethods.ReadAttributes, 1, IntPtr.Zero, 3,
                    NativeMethods.OpenReparsePoint, IntPtr.Zero);
                if (handle.IsInvalid)
                {
                    int error = Marshal.GetLastWin32Error();
                    throw new TransferFailure(error == 2 || error == 3 ? "SourceMissing" : error == 32 || error == 33 ? "SourceLocked" : "SourceAccess",
                        error == 32 || error == 33 ? "원본이 쓰기 중입니다. 저장 완료를 기다립니다." : "원본 파일의 존재/읽기 권한을 확인하세요.", error == 32 || error == 33);
                }
                if (!string.Equals(NativeMethods.FinalPath(handle), path, StringComparison.OrdinalIgnoreCase)) throw new TransferFailure("SourceChanged", "원본 경로가 교체되었습니다.", true);
                var file = WindowsFileSystem.ReadSnapshot(handle, path, expected.Root, new DrivePolicy { VolumeId = expected.VolumeId }, false);
                if (file.IsDirectory || file.UnsupportedReason != null) throw new TransferFailure("UnsupportedSource", file.UnsupportedReason ?? "원본이 파일이 아닙니다.");
                if (!TransferRules.SameFile(expected, file)) throw new TransferFailure("SourceChanged", "발견 후 원본이 변경되었습니다. 재검색을 기다립니다.", true);
                WindowsFileSystem.EnsureNoAlternateStreams(handle);
                return new ReadLease(handle, guards, file);
            }
            catch { handle?.Dispose(); guards.Dispose(); throw; }
        }
        private sealed class ReadLease : ITransferReadLease
        {
            private readonly SafeFileHandle _handle; private readonly DirectoryGuards _guards; private readonly FileStream _stream;
            public FileSnapshot Snapshot { get; }
            public Stream Content => _stream;
            public ReadLease(SafeFileHandle h, DirectoryGuards guards, FileSnapshot file)
            { _handle = h; _guards = guards; Snapshot = file; _stream = new FileStream(h, FileAccess.Read, 65536, false); }
            public void Dispose() { _stream.Dispose(); _handle.Dispose(); _guards.Dispose(); }
        }
    }
    public sealed class WindowsTransferSecrets
    {
        private readonly SqliteJournal _store;
        public WindowsTransferSecrets(SqliteJournal store) { _store = store; }
        public void Save(string id, string password, string actor)
        {
            if (password == null || password.Length > 4096 || password.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0) throw new ArgumentException("FTP 비밀번호 형식을 확인하세요.");
            byte[] plain = Encoding.UTF8.GetBytes(password);
            try { _store.SaveTransferSecret(id, ProtectedData.Protect(plain, Encoding.UTF8.GetBytes(id), DataProtectionScope.LocalMachine), actor); }
            finally { Array.Clear(plain, 0, plain.Length); }
        }
        public string Read(string id)
        {
            byte[] cipher = _store.TransferSecret(id) ?? throw new TransferFailure("CredentialMissing", "FTP 비밀번호를 서비스 설정에 등록하세요.");
            byte[] plain;
            try { plain = ProtectedData.Unprotect(cipher, Encoding.UTF8.GetBytes(id), DataProtectionScope.LocalMachine); }
            catch (CryptographicException) { throw new TransferFailure("CredentialUnreadable", "이 컴퓨터/서비스에서 FTP 비밀번호를 다시 등록하세요."); }
            try { return Encoding.UTF8.GetString(plain); } finally { Array.Clear(plain, 0, plain.Length); }
        }
    }
}
