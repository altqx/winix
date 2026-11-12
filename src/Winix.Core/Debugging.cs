using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;

namespace Winix;

/// <summary>
/// Static facts about a PE image (exe, dll, sys): headers, exploit mitigations, sections, imports, exports, the managed header
/// and the CodeView record a symbol server keys its PDB on. Reads the file only; it never loads or runs it, and works on any OS.
/// </summary>
public static class PeInspector
{
    public sealed record Section(string Name, string Rva, uint VirtualSize, int RawSize, string Access);
    public sealed record Import(string Dll, IReadOnlyList<string> Functions);
    public sealed record Pdb(string Path, Guid Guid, int Age, string SymbolKey);
    public sealed record Managed(bool IlOnly, bool StrongNameSigned, bool Requires32Bit, string? MetadataVersion, string? Assembly, string? Version, string? TargetFramework);
    public sealed record Report(string Path, long Size, string Format, string Machine, string Kind, string Subsystem, string ImageBase, string EntryPoint,
        uint TimeDateStamp, string Linker, string OperatingSystem, IReadOnlyList<string> Characteristics, IReadOnlyDictionary<string, bool> Mitigations,
        bool HasCertificate, IReadOnlyList<Section> Sections, IReadOnlyList<Import> Imports, IReadOnlyList<string> DelayImports,
        string? ExportName, IReadOnlyList<string> Exports, IReadOnlyList<Pdb> Pdbs, IReadOnlyList<string> DebugEntries, Managed? Managed);

    public static Report Inspect(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        try
        {
            using var pe = new PEReader(stream);
            var headers = pe.PEHeaders;
            var coff = headers.CoffHeader;
            var opt = headers.PEHeader ?? throw new WinixException("not_pe", $"{path} has no optional header (an object file?).");
            var dll = (ushort)opt.DllCharacteristics;
            var mitigations = new Dictionary<string, bool>
            {
                ["aslr"] = (dll & 0x0040) != 0, ["highEntropyVa"] = (dll & 0x0020) != 0, ["dep"] = (dll & 0x0100) != 0, ["cfg"] = (dll & 0x4000) != 0,
                ["noSeh"] = (dll & 0x0400) != 0, ["appContainer"] = (dll & 0x1000) != 0, ["forceIntegrity"] = (dll & 0x0080) != 0,
            };
            var sections = headers.SectionHeaders.Select(s => new Section(s.Name, Hex(s.VirtualAddress), (uint)s.VirtualSize, s.SizeOfRawData,
                ((s.SectionCharacteristics & SectionCharacteristics.MemRead) != 0 ? "r" : "-") + ((s.SectionCharacteristics & SectionCharacteristics.MemWrite) != 0 ? "w" : "-") +
                ((s.SectionCharacteristics & SectionCharacteristics.MemExecute) != 0 ? "x" : "-"))).ToList();
            var pe32Plus = opt.Magic == PEMagic.PE32Plus;
            var (exportName, exports) = Exports(pe, opt.ExportTableDirectory);
            List<Pdb> pdbs = []; List<string> debugEntries = [];
            foreach (var entry in pe.ReadDebugDirectory())
            {
                debugEntries.Add(entry.Type.ToString());
                if (entry.Type != DebugDirectoryEntryType.CodeView) continue;
                var codeView = pe.ReadCodeViewDebugDirectoryData(entry);
                // The symbol server layout: <pdb name>/<GUID without dashes><age in hex>/<pdb name>.
                var signature = codeView.Guid.ToString("N").ToUpperInvariant() + codeView.Age.ToString("X");
                var pdbName = Path.GetFileName(codeView.Path.Replace('\\', '/'));
                pdbs.Add(new(codeView.Path, codeView.Guid, codeView.Age, pdbName.Length > 0 ? pdbName + "/" + signature : signature));
            }
            return new(Path.GetFullPath(path), stream.Length, pe32Plus ? "PE32+" : "PE32", coff.Machine.ToString(),
                coff.Characteristics.HasFlag(Characteristics.Dll) ? "dll" : opt.Subsystem == Subsystem.Native ? "driver" : "exe",
                opt.Subsystem.ToString(), "0x" + opt.ImageBase.ToString("x"), Hex(opt.AddressOfEntryPoint), (uint)coff.TimeDateStamp,
                $"{opt.MajorLinkerVersion}.{opt.MinorLinkerVersion}", $"{opt.MajorOperatingSystemVersion}.{opt.MinorOperatingSystemVersion}",
                Enum.GetValues<Characteristics>().Where(c => c != 0 && coff.Characteristics.HasFlag(c)).Select(c => c.ToString()).Distinct().ToList(),
                mitigations, opt.CertificateTableDirectory.Size > 0, sections, Imports(pe, opt.ImportTableDirectory, pe32Plus), DelayImports(pe, opt.DelayImportTableDirectory),
                exportName, exports, pdbs, debugEntries, ManagedInfo(pe, headers));
        }
        catch (BadImageFormatException e) { throw new WinixException("not_pe", $"{path} is not a valid PE image: {e.Message}"); }
    }

    private static string Hex(int rva) => "0x" + rva.ToString("x");
    private static string? AsciiAt(PEReader pe, int rva)
    {
        if (rva <= 0) return null;
        var block = pe.GetSectionData(rva);
        if (block.Length == 0) return null;
        var reader = block.GetReader();
        var bytes = new List<byte>();
        while (reader.RemainingBytes > 0 && bytes.Count < 4096) { var b = reader.ReadByte(); if (b == 0) break; bytes.Add(b); }
        return Encoding.ASCII.GetString(bytes.ToArray());
    }
    private static BlobReader? At(PEReader pe, int rva)
    {
        if (rva <= 0) return null;
        var block = pe.GetSectionData(rva);
        return block.Length == 0 ? null : block.GetReader();
    }

    private static List<Import> Imports(PEReader pe, DirectoryEntry directory, bool pe32Plus)
    {
        List<Import> imports = [];
        if (directory.Size == 0 || At(pe, directory.RelativeVirtualAddress) is not { } reader) return imports;
        while (reader.RemainingBytes >= 20 && imports.Count < 4096)
        {
            int lookup = reader.ReadInt32(); reader.ReadInt32(); reader.ReadInt32(); int name = reader.ReadInt32(); int thunk = reader.ReadInt32();
            if (lookup == 0 && name == 0 && thunk == 0) break;
            List<string> functions = [];
            if (At(pe, lookup != 0 ? lookup : thunk) is { } thunks)
            {
                while (thunks.RemainingBytes >= (pe32Plus ? 8 : 4) && functions.Count < 65536)
                {
                    var value = pe32Plus ? thunks.ReadUInt64() : thunks.ReadUInt32();
                    if (value == 0) break;
                    var ordinal = pe32Plus ? (value & 0x8000000000000000UL) != 0 : (value & 0x80000000UL) != 0;
                    // A hint/name entry: a two-byte hint, then the name.
                    functions.Add(ordinal ? "#" + (value & 0xFFFF) : AsciiAt(pe, (int)(value & 0x7FFFFFFF) + 2) ?? "?");
                }
            }
            imports.Add(new(AsciiAt(pe, name) ?? "?", functions));
        }
        return imports;
    }
    private static List<string> DelayImports(PEReader pe, DirectoryEntry directory)
    {
        List<string> dlls = [];
        if (directory.Size == 0 || At(pe, directory.RelativeVirtualAddress) is not { } reader) return dlls;
        while (reader.RemainingBytes >= 32 && dlls.Count < 4096)
        {
            reader.ReadInt32(); int name = reader.ReadInt32();
            for (var i = 0; i < 6; i++) reader.ReadInt32();
            if (name == 0) break;
            dlls.Add(AsciiAt(pe, name) ?? "?");
        }
        return dlls;
    }
    private static (string?, List<string>) Exports(PEReader pe, DirectoryEntry directory)
    {
        List<string> names = [];
        if (directory.Size == 0 || At(pe, directory.RelativeVirtualAddress) is not { } reader || reader.RemainingBytes < 40) return (null, names);
        reader.Offset = 12; var dllName = AsciiAt(pe, reader.ReadInt32());
        reader.Offset = 24; var count = reader.ReadInt32(); reader.ReadInt32(); var nameTable = reader.ReadInt32();
        if (At(pe, nameTable) is { } table)
            for (var i = 0; i < Math.Min(count, 65536) && table.RemainingBytes >= 4; i++) names.Add(AsciiAt(pe, table.ReadInt32()) ?? "?");
        return (dllName, names);
    }
    private static Managed? ManagedInfo(PEReader pe, PEHeaders headers)
    {
        if (headers.CorHeader is not { } cor) return null;
        string? version = null, assembly = null, assemblyVersion = null, framework = null;
        if (pe.HasMetadata)
        {
            var metadata = pe.GetMetadataReader();
            version = metadata.MetadataVersion;
            if (metadata.IsAssembly)
            {
                var definition = metadata.GetAssemblyDefinition();
                assembly = metadata.GetString(definition.Name); assemblyVersion = definition.Version.ToString();
                foreach (var handle in definition.GetCustomAttributes())
                {
                    var attribute = metadata.GetCustomAttribute(handle);
                    if (attribute.Constructor.Kind != HandleKind.MemberReference) continue;
                    var parent = metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent;
                    if (parent.Kind != HandleKind.TypeReference || metadata.GetString(metadata.GetTypeReference((TypeReferenceHandle)parent).Name) != "TargetFrameworkAttribute") continue;
                    var value = metadata.GetBlobReader(attribute.Value);
                    if (value.ReadUInt16() == 1) framework = value.ReadSerializedString();
                }
            }
        }
        return new((cor.Flags & CorFlags.ILOnly) != 0, (cor.Flags & CorFlags.StrongNameSigned) != 0, (cor.Flags & CorFlags.Requires32Bit) != 0, version, assembly, assemblyVersion, framework);
    }
}

/// <summary>The fields of cdb's <c>!analyze -v</c> output an agent triages on, plus the faulting stack.</summary>
public static class CdbAnalysis
{
    private static readonly string[] Keys = ["FAILURE_BUCKET_ID", "EXCEPTION_CODE_STR", "ERROR_CODE", "EXCEPTION_RECORD", "PROCESS_NAME", "MODULE_NAME", "IMAGE_NAME",
        "SYMBOL_NAME", "FAULTING_SOURCE_FILE", "FAULTING_SOURCE_LINE_NUMBER", "FAILURE_ID_HASH", "EXCEPTION_MESSAGE"];
    public sealed record Summary(IReadOnlyDictionary<string, string> Fields, IReadOnlyList<string> Stack);
    public static Summary Parse(string text)
    {
        var fields = new Dictionary<string, string>();
        List<string> stack = [];
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.StartsWith("STACK_TEXT:") && stack.Count == 0)
            {
                for (var j = i + 1; j < lines.Length && !string.IsNullOrWhiteSpace(lines[j]) && stack.Count < 64; j++) stack.Add(lines[j].TrimEnd());
                continue;
            }
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var key = line[..colon];
            if (Keys.Contains(key) && !fields.ContainsKey(key) && line[(colon + 1)..].Trim() is { Length: > 0 } value) fields[key] = value;
        }
        return new(fields, stack);
    }
}
