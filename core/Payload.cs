using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Barista.Services;

/// <summary>Which commit of which repository the built-in binaries came from.</summary>
public sealed record PayloadSource(string Name, string Repository, string Commit);

/// <summary>
/// One file the release carries, as the build machine saw it.
/// <see cref="Path"/> is relative and uses forward slashes: "Doppio/TacProvider.dll".
/// </summary>
public sealed record PayloadFile(string Path, string Sha256, long Size)
{
    /// <summary>"Doppio" or "Ristretto" - the first path segment.</summary>
    public string Component => Path.Split('/')[0];

    public string FileName => Path.Split('/')[^1];
}

/// <summary>
/// What tools\build-payload.ps1 writes next to payload.zip, and what the
/// release build embeds into Barista.exe beside it.
///
/// It is the whole answer to "which binaries is this about to put into
/// lsass": the commit each one was built from, and the SHA-256 of every file.
/// Nothing on the target machine is compiled or downloaded, so this list is
/// also the complete list of what can end up there.
/// </summary>
public sealed class PayloadManifest
{
    public const int CurrentSchema = 1;

    public int SchemaVersion { get; set; }

    /// <summary>The Barista release this payload was built for, e.g. "0.2.0".</summary>
    public string Version { get; set; } = "";

    public DateTime BuiltUtc { get; set; }

    public List<PayloadSource> Sources { get; set; } = [];

    public List<PayloadFile> Files { get; set; } = [];

    /// <summary>
    /// Short id of this exact manifest - the first 16 hex digits of its own
    /// SHA-256. Names the folder the payload is unpacked into, so two releases
    /// never share a folder and a resumed run finds the files it started with.
    /// </summary>
    public string Id { get; private set; } = "";

    private static readonly Regex Hex64 = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);
    private static readonly Regex Hex40 = new("^[0-9a-f]{40}$", RegexOptions.CultureInvariant);
    private static readonly Regex SafeName = new(@"^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant);

    private static readonly string[] Components = ["Doppio", "Ristretto"];

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Parses and validates. Throws <see cref="InvalidDataException"/> with a
    /// readable reason on anything this version does not fully understand: a
    /// manifest that is half-read is a list of hashes that is half-checked.
    /// </summary>
    public static PayloadManifest Parse(byte[] json)
    {
        PayloadManifest? m;
        try
        {
            m = JsonSerializer.Deserialize<PayloadManifest>(json, Json);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"The payload manifest is not valid JSON: {ex.Message}");
        }

        if (m is null) throw new InvalidDataException("The payload manifest is empty.");
        if (m.SchemaVersion != CurrentSchema)
            throw new InvalidDataException(
                $"The payload manifest has schema {m.SchemaVersion}; this build reads {CurrentSchema}.");
        if (m.Files.Count == 0) throw new InvalidDataException("The payload manifest lists no files.");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in m.Files)
        {
            // Two segments exactly, a known component, a plain file name. That
            // rules out "..", rooted paths, drive letters, alternate data
            // streams and subfolders in one go - the unpacker writes nothing
            // whose name it has not seen here.
            var parts = (f.Path ?? "").Split('/');
            if (parts.Length != 2 || !Components.Contains(parts[0]) || !SafeName.IsMatch(parts[1]))
                throw new InvalidDataException($"The payload manifest names an unsafe path: '{f.Path}'.");
            if (!Hex64.IsMatch(f.Sha256 ?? ""))
                throw new InvalidDataException($"'{f.Path}' has no valid lowercase SHA-256 in the manifest.");
            if (f.Size < 0)
                throw new InvalidDataException($"'{f.Path}' has a negative size in the manifest.");
            if (!seen.Add(f.Path!))
                throw new InvalidDataException($"'{f.Path}' is listed twice in the payload manifest.");
        }

        foreach (var s in m.Sources)
        {
            if (!Hex40.IsMatch(s.Commit ?? ""))
                throw new InvalidDataException($"Source '{s.Name}' has no full commit id in the manifest.");
        }

        m.Id = Convert.ToHexStringLower(SHA256.HashData(json))[..16];
        return m;
    }

    public IEnumerable<PayloadFile> FilesOf(string component)
        => Files.Where(f => f.Component.Equals(component, StringComparison.OrdinalIgnoreCase));

    public bool Has(string component) => FilesOf(component).Any();
}

public enum HashStatus
{
    /// <summary>Not looked at yet.</summary>
    Pending,
    /// <summary>On disk, and its SHA-256 is the one in the manifest.</summary>
    Match,
    /// <summary>On disk, with a different SHA-256 or size.</summary>
    Mismatch,
    /// <summary>Not on disk at all.</summary>
    Missing,
}

/// <summary>One row of the comparison the integrity page shows.</summary>
public sealed record FileVerification(PayloadFile File, string? ActualSha256, long? ActualSize, HashStatus Status)
{
    public bool Ok => Status == HashStatus.Match;
}

public static class PayloadVerifier
{
    public static string Sha256Hex(Stream stream) => Convert.ToHexStringLower(SHA256.HashData(stream));

    public static string Sha256OfFile(string path)
    {
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Sha256Hex(s);
    }

    /// <summary>Path of one manifest entry under <paramref name="root"/>.</summary>
    public static string LocalPath(string root, PayloadFile f)
        => Path.Combine(root, f.Component, f.FileName);

    /// <summary>
    /// Reads one file back from disk and compares it with the manifest. Always
    /// from disk: the point is what powershell.exe and LSA are going to load,
    /// not what was in memory while it was being written.
    /// </summary>
    public static FileVerification VerifyFile(string root, PayloadFile f)
    {
        var path = LocalPath(root, f);
        if (!File.Exists(path)) return new(f, null, null, HashStatus.Missing);

        var size = new FileInfo(path).Length;
        var actual = Sha256OfFile(path);
        var ok = size == f.Size && string.Equals(actual, f.Sha256, StringComparison.Ordinal);
        return new(f, actual, size, ok ? HashStatus.Match : HashStatus.Mismatch);
    }

    /// <summary>
    /// Every file in the manifest, in manifest order. <paramref name="onFile"/>
    /// sees each row as it is finished, so a page can fill in while it runs.
    /// </summary>
    public static List<FileVerification> VerifyFolder(
        PayloadManifest manifest, string root, Action<FileVerification>? onFile = null)
    {
        var rows = new List<FileVerification>(manifest.Files.Count);
        foreach (var f in manifest.Files)
        {
            var row = VerifyFile(root, f);
            rows.Add(row);
            onFile?.Invoke(row);
        }
        return rows;
    }

    /// <summary>
    /// Files in the component folders that the manifest does not name. The
    /// install scripts run with $PSScriptRoot pointing here, so a stray file
    /// beside them is a file they might pick up.
    /// </summary>
    public static List<string> Unexpected(PayloadManifest manifest, string root)
    {
        var known = new HashSet<string>(
            manifest.Files.Select(f => LocalPath(root, f)), StringComparer.OrdinalIgnoreCase);

        var extra = new List<string>();
        if (!Directory.Exists(root)) return extra;

        foreach (var entry in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
        {
            if (Directory.Exists(entry))
            {
                var rel = Path.GetRelativePath(root, entry);
                if (!manifest.Has(rel)) extra.Add(rel);
                continue;
            }
            if (!known.Contains(entry)) extra.Add(Path.GetRelativePath(root, entry));
        }
        return extra;
    }
}

/// <summary>What <see cref="PayloadExtractor.Prepare"/> found and did.</summary>
public sealed record PayloadPreparation(
    string Root,
    bool Unpacked,
    IReadOnlyList<FileVerification> Files,
    IReadOnlyList<string> Unexpected)
{
    public bool AllMatch => Files.Count > 0 && Files.All(f => f.Ok) && Unexpected.Count == 0;
}

public static class PayloadExtractor
{
    /// <summary>
    /// Makes sure <paramref name="root"/> holds exactly the files in the
    /// manifest, then checks every one of them from disk.
    ///
    /// A folder left by an earlier start (a resumed run, or the user going
    /// back a page) is reused only if every file in it already matches.
    /// Anything else - a missing file, a changed one, an extra one - and the
    /// folder is removed and unpacked again from the copy inside the exe.
    /// Repairing in place would mean trusting the parts of a folder that has
    /// just been shown to be wrong.
    /// </summary>
    /// <param name="openZip">Opens the embedded payload.zip. Called only when unpacking.</param>
    /// <param name="createRoot">
    /// Creates the (empty) root folder. The app passes
    /// <see cref="ProtectedFolder.Create"/>; tests pass a plain create.
    /// </param>
    public static PayloadPreparation Prepare(
        PayloadManifest manifest,
        Func<Stream> openZip,
        string root,
        Action<string> createRoot,
        Action<FileVerification>? onFile = null)
    {
        if (Directory.Exists(root))
        {
            // Before reading anything in it: hashes checked through a junction
            // say nothing about the folder the scripts will run from.
            if (new DirectoryInfo(root).Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new IOException($"{root} is a junction or symbolic link. Refusing to use it.");

            var existing = PayloadVerifier.VerifyFolder(manifest, root);
            var extra = PayloadVerifier.Unexpected(manifest, root);
            if (existing.All(f => f.Ok) && extra.Count == 0)
            {
                foreach (var row in existing) onFile?.Invoke(row);
                return new(root, Unpacked: false, existing, extra);
            }

            Directory.Delete(root, recursive: true);
        }

        createRoot(root);

        using (var zip = openZip())
            Unpack(zip, manifest, root);

        // Second, independent pass over what is now on disk. This is what the
        // page shows and what decides whether the install may continue.
        var rows = PayloadVerifier.VerifyFolder(manifest, root, onFile);
        return new(root, Unpacked: true, rows, PayloadVerifier.Unexpected(manifest, root));
    }

    /// <summary>
    /// Writes the manifest's files out of <paramref name="zipStream"/>.
    ///
    /// Driven by the manifest, not by the archive: an entry the manifest does
    /// not name is an error, never a file on disk. That is the whole defence
    /// against "../" names and friends - the only paths ever written are the
    /// ones <see cref="PayloadManifest.Parse"/> has already accepted.
    /// </summary>
    public static void Unpack(Stream zipStream, PayloadManifest manifest, string root)
    {
        using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);

        var wanted = manifest.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (name.EndsWith('/')) continue;           // a folder entry, nothing to write

            if (!wanted.TryGetValue(name, out var f))
                throw new InvalidDataException(
                    $"payload.zip contains '{entry.FullName}', which the manifest does not list.");
            if (!written.Add(f.Path))
                throw new InvalidDataException($"payload.zip contains '{f.Path}' twice.");
            if (entry.Length != f.Size)
                throw new InvalidDataException(
                    $"'{f.Path}' is {entry.Length} bytes in payload.zip; the manifest says {f.Size}.");

            var dir = Path.Combine(root, f.Component);
            Directory.CreateDirectory(dir);

            using var src = entry.Open();
            using var dst = new FileStream(
                Path.Combine(dir, f.FileName), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            src.CopyTo(dst);
        }

        var missing = manifest.Files.Where(f => !written.Contains(f.Path)).Select(f => f.Path).ToArray();
        if (missing.Length > 0)
            throw new InvalidDataException(
                $"payload.zip is missing {string.Join(", ", missing)}.");
    }
}

/// <summary>
/// The payload built into this copy of Barista.exe, if there is one.
///
/// A release build has one: CI compiles Doppio and Ristretto from pinned
/// commits and embeds the result, so the target machine needs no git, no
/// compiler and no network. A developer build made with a plain
/// "dotnet build" has none, and the wizard falls back to asking for folders.
/// </summary>
public sealed class EmbeddedPayload
{
    public const string ZipResource = "Barista.Payload.zip";
    public const string ManifestResource = "Barista.Payload.json";

    private readonly Assembly _assembly;

    public PayloadManifest Manifest { get; }

    private EmbeddedPayload(Assembly assembly, PayloadManifest manifest)
    {
        _assembly = assembly;
        Manifest = manifest;
    }

    /// <summary>
    /// Null when the assembly carries no payload. Throws when it carries one
    /// that cannot be read - a release whose own manifest is broken must not
    /// quietly turn into a developer build that asks for folders.
    /// </summary>
    public static EmbeddedPayload? TryLoad(Assembly assembly)
    {
        using var manifest = assembly.GetManifestResourceStream(ManifestResource);
        using var zip = assembly.GetManifestResourceStream(ZipResource);

        if (manifest is null && zip is null) return null;
        if (manifest is null || zip is null)
            throw new InvalidDataException(
                "This build of Barista carries only half of its payload (manifest or archive missing).");

        using var buffer = new MemoryStream();
        manifest.CopyTo(buffer);
        return new EmbeddedPayload(assembly, PayloadManifest.Parse(buffer.ToArray()));
    }

    public Stream OpenZip()
        => _assembly.GetManifestResourceStream(ZipResource)
           ?? throw new InvalidDataException("The embedded payload.zip has disappeared.");

    /// <summary>
    /// %ProgramFiles%\TheAdminCafe\Barista\payload\&lt;id&gt;. Under Program
    /// Files so that only administrators can change a script between the
    /// check and the run; never under %TEMP% or ProgramData, which are the
    /// places any local user can write to.
    /// </summary>
    public static string RootFor(PayloadManifest m) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "TheAdminCafe", "Barista", "payload", m.Id);

    public string Root => RootFor(Manifest);

    public string DoppioDir => Path.Combine(Root, "Doppio");

    public string? RistrettoDir => Manifest.Has("Ristretto") ? Path.Combine(Root, "Ristretto") : null;
}
