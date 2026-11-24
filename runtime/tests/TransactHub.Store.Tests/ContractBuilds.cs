using System.Buffers.Binary;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using TransactHub.Activities.Native;

namespace TransactHub.Tests.Support;

/// <summary>
/// A module as a build against an earlier minor of the native contract leaves it (R-09-276): the same assembly, its reference to
/// <c>TransactHub.Activities.Contract</c> at that version. A module that uses nothing the later minor added is, byte for byte but the
/// version, what its build against the earlier contract is; so a 2.0 module is had without keeping the 2.0 contract's sources. The
/// assembly still loads and runs: the runtime resolves the contract in its own context, at its own version.
/// </summary>
public static class ContractBuilds
{
    public static byte[] BuiltAgainst(byte[] assembly, string contract)
    {
        var bytes = assembly.ToArray();
        var v = Version.Parse(contract);
        using var pe = new PEReader(new MemoryStream(assembly));
        var md = pe.GetMetadataReader();
        var table = pe.PEHeaders.MetadataStartOffset + md.GetTableMetadataOffset(TableIndex.AssemblyRef);
        var size = md.GetTableRowSize(TableIndex.AssemblyRef);
        var found = false;
        foreach (var h in md.AssemblyReferences)
        {
            if (md.GetString(md.GetAssemblyReference(h).Name) != NativeContract.AssemblyName) continue;
            // An AssemblyRef row starts with its version: major, minor, build and revision, two bytes each, little-endian.
            var at = table + (MetadataTokens.GetRowNumber(h) - 1) * size;
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at), (ushort)v.Major);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at + 2), (ushort)v.Minor);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at + 4), (ushort)Math.Max(v.Build, 0));
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at + 6), 0);
            found = true;
        }
        if (!found) throw new InvalidOperationException($"The assembly does not reference {NativeContract.AssemblyName}");
        return bytes;
    }
}
