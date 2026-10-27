#Requires -RunAsAdministrator
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$Path)
$ErrorActionPreference='Stop'
$full=(Resolve-Path -LiteralPath $Path).Path.TrimEnd('\')
if ($full.Length -le 3 -or $full.StartsWith('\\') -or -not (Test-Path $full -PathType Container)) { throw 'A local managed subdirectory is required.' }
foreach ($blocked in @($env:windir,$env:ProgramFiles,[Environment]::GetFolderPath('ProgramFilesX86'),"$env:ProgramData\ExplorerManager")) {
    if ($blocked -and ($full -eq $blocked -or $full.StartsWith($blocked.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase))) { throw 'Protected path.' }
}
if ((Get-Item -LiteralPath $full -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse roots are not allowed.' }
if (-not ('ExplorerFolderAcl' -as [type])) {
Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
public static class ExplorerFolderAcl {
 [StructLayout(LayoutKind.Sequential)] struct Basic { public long Created,Accessed,Written,Changed; public uint Attributes; }
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern SafeFileHandle CreateFileW(string path,uint access,uint share,IntPtr security,uint mode,uint flags,IntPtr template);
 [DllImport("kernel32.dll",SetLastError=true)] static extern bool GetFileInformationByHandleEx(SafeFileHandle handle,int kind,out Basic basic,uint length);
 [DllImport("advapi32.dll")] static extern uint GetSecurityInfo(SafeFileHandle handle,int kind,uint info,out IntPtr owner,out IntPtr group,out IntPtr dacl,out IntPtr sacl,out IntPtr descriptor);
 [DllImport("advapi32.dll")] static extern uint GetSecurityDescriptorLength(IntPtr descriptor);
 [DllImport("advapi32.dll",SetLastError=true)] static extern bool GetSecurityDescriptorDacl(IntPtr descriptor,out bool present,out IntPtr dacl,out bool defaulted);
 [DllImport("advapi32.dll")] static extern uint SetSecurityInfo(SafeFileHandle handle,int kind,uint info,IntPtr owner,IntPtr group,IntPtr dacl,IntPtr sacl);
 [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr value);
 public static void GrantAncestor(string path) {
  // MAXIMUM_ALLOWED explicitly prevents SetSecurityInfo propagating the unchanged
  // inheritable ACL through the ancestor's entire subtree. The added ACE has no inheritance.
  using(var handle=CreateFileW(path,0x02000000,3,IntPtr.Zero,3,0x02200000,IntPtr.Zero)) {
   if(handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
   Basic basic; if(!GetFileInformationByHandleEx(handle,0,out basic,(uint)Marshal.SizeOf(typeof(Basic)))) throw new Win32Exception(Marshal.GetLastWin32Error());
   if((basic.Attributes & 0x400)!=0 || (basic.Attributes & 0x10)==0) throw new InvalidOperationException("Reparse ancestor rejected: "+path);
   IntPtr owner,group,dacl,sacl,descriptor; uint code=GetSecurityInfo(handle,1,4,out owner,out group,out dacl,out sacl,out descriptor);
   if(code!=0) throw new Win32Exception((int)code);
   try {
    byte[] bytes=new byte[GetSecurityDescriptorLength(descriptor)]; Marshal.Copy(descriptor,bytes,0,bytes.Length);
    var security=new DirectorySecurity(); security.SetSecurityDescriptorBinaryForm(bytes,AccessControlSections.Access);
    var sid=(SecurityIdentifier)new NTAccount("NT SERVICE", "ExplorerManager").Translate(typeof(SecurityIdentifier));
    var rights=FileSystemRights.ReadAttributes | FileSystemRights.Traverse;
    foreach(FileSystemAccessRule existing in security.GetAccessRules(true,true,typeof(SecurityIdentifier)))
     if(existing.IdentityReference.Equals(sid) && existing.AccessControlType==AccessControlType.Allow && (existing.FileSystemRights & rights)==rights) return;
    security.AddAccessRule(new FileSystemAccessRule(sid,rights,InheritanceFlags.None,PropagationFlags.None,AccessControlType.Allow));
    bytes=security.GetSecurityDescriptorBinaryForm(); var pinned=GCHandle.Alloc(bytes,GCHandleType.Pinned);
    try {
     bool present,defaulted; if(!GetSecurityDescriptorDacl(pinned.AddrOfPinnedObject(),out present,out dacl,out defaulted) || !present || dacl==IntPtr.Zero) throw new InvalidOperationException("Invalid ancestor DACL");
     code=SetSecurityInfo(handle,1,4,IntPtr.Zero,IntPtr.Zero,dacl,IntPtr.Zero); if(code!=0) throw new Win32Exception((int)code);
    } finally { pinned.Free(); }
   } finally { LocalFree(descriptor); }
  }
 }
}
'@
}
# Verified file handles also inspect each ancestor. Grant only traversal/attributes there,
# never listing, content access or inherited access to other user folders.
$ancestors=@()
$parent=Split-Path $full -Parent
while ($parent) {
    if ((Get-Item -LiteralPath $parent -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse ancestors are not allowed.' }
    $ancestors+=,$parent
    $next=Split-Path $parent -Parent
    if ($next -eq $parent) { break }
    $parent=$next
}
foreach ($ancestor in $ancestors) {
    Write-Host "Grant ancestor traversal/attributes only: $ancestor"
    [ExplorerFolderAcl]::GrantAncestor($ancestor)
}
& icacls.exe $full /grant 'NT SERVICE\ExplorerManager:(OI)(CI)M'
if ($LASTEXITCODE -ne 0) { throw 'Could not grant service access.' }
Write-Host "Granted service read/delete/restore permission to: $full"
