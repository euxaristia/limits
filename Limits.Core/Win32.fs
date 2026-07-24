namespace Limits.Core

// Win32 interop for the Antigravity credential reader. Kept separate from
// Library.fs because F# struct syntax with [StructLayout] and member val
// fields has a chicken-and-egg constraint: member val requires a primary
// constructor, but [StructLayout] forbids one. C# expresses this cleanly.

open System
open System.Runtime.InteropServices
open System.Runtime.InteropServices.ComTypes

[<StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)>]
type internal CREDENTIAL =
    val mutable Flags : uint32
    val mutable Type : uint32
    val mutable TargetName : IntPtr
    val mutable Comment : IntPtr
    val mutable LastWritten : FILETIME
    val mutable CredentialBlobSize : uint32
    val mutable CredentialBlob : IntPtr
    val mutable Persist : uint32
    val mutable AttributeCount : uint32
    val mutable Attributes : IntPtr
    val mutable TargetAlias : IntPtr
    val mutable UserName : IntPtr

    new () = {
        Flags = 0u
        Type = 0u
        TargetName = IntPtr.Zero
        Comment = IntPtr.Zero
        LastWritten = Unchecked.defaultof<FILETIME>
        CredentialBlobSize = 0u
        CredentialBlob = IntPtr.Zero
        Persist = 0u
        AttributeCount = 0u
        Attributes = IntPtr.Zero
        TargetAlias = IntPtr.Zero
        UserName = IntPtr.Zero
    }

module internal Win32 =
    [<DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)>]
    extern bool CredRead(string target, uint32 credType, uint32 reservedFlag, IntPtr& credentialPtr)

    [<DllImport("advapi32.dll", SetLastError = true)>]
    extern void CredFree(IntPtr credentialPtr)
