# PowerShell script to fetch Antigravity quota and dump the response shape
# Reads OAuth from Windows Credential Manager (same path the CLI uses),
# then calls v1internal:fetchAvailableModels with the same body the CLI does.

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public class CredMan2 {
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct CREDENTIAL {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CredReadW")]
    public static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern void CredFree(IntPtr credential);
}
'@

# Read credential
$ptr = [IntPtr]::Zero
$ok = [CredMan2]::CredRead('LegacyGeneric:target=gemini:antigravity', 1, 0, [ref] $ptr)
if (-not $ok) {
    Write-Host "CredRead failed: $([System.Runtime.InteropServices.Marshal]::GetLastWin32Error())"
    exit 1
}
$cred = [System.Runtime.InteropServices.Marshal]::PtrToStructure($ptr, [type][CredMan2+CREDENTIAL])
$size = $cred.CredentialBlobSize
$buf = New-Object byte[] $size
if ($size -gt 0) {
    [System.Runtime.InteropServices.Marshal]::Copy($cred.CredentialBlob, $buf, 0, [int]$size)
}
$json = [System.Text.Encoding]::UTF8.GetString($buf)
[CredMan2]::CredFree($ptr)
$token = ($json | ConvertFrom-Json).token.access_token
Write-Host "Got token: $($token.Substring(0, 30))..."

# Try fetchAvailableModels with empty body (like my current code)
Write-Host ""
Write-Host "=== fetchAvailableModels with empty body ==="
try {
    $r = Invoke-WebRequest -Uri "https://cloudcode-pa.googleapis.com/v1internal:fetchAvailableModels" `
        -Method POST `
        -Headers @{
            "Authorization" = "Bearer $token"
            "User-Agent" = "antigravity"
            "Content-Type" = "application/json"
        } `
        -Body "{}" `
        -UseBasicParsing
    Write-Host "Status: $($r.StatusCode)"
    Write-Host "Content-Type: $($r.Headers['Content-Type'])"
    Write-Host "Body:"
    Write-Host $r.Content
} catch {
    Write-Host "Error: $_"
    if ($_.Exception.Response) {
        $reader = [System.IO.StreamReader]::new($_.Exception.Response.GetResponseStream())
        $errBody = $reader.ReadToEnd()
        Write-Host "Error body: $errBody"
    }
}
Write-Host ""
Write-Host "=== retrieveUserQuota with empty body ==="
try {
    $r = Invoke-WebRequest -Uri "https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuota" `
        -Method POST `
        -Headers @{
            "Authorization" = "Bearer $token"
            "User-Agent" = "antigravity"
            "Content-Type" = "application/json"
        } `
        -Body "{}" `
        -UseBasicParsing
    Write-Host "Status: $($r.StatusCode)"
    Write-Host "Body:"
    Write-Host $r.Content
} catch {
    Write-Host "retrieveUserQuota Error: $_"
    if ($_.Exception.Response) {
        $reader = [System.IO.StreamReader]::new($_.Exception.Response.GetResponseStream())
        $errBody = $reader.ReadToEnd()
        Write-Host "Error body: $errBody"
    }
}
