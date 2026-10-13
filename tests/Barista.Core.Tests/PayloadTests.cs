using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Barista.Services;
using Xunit;

namespace Barista.Core.Tests;

/// <summary>
/// The built-in payload: what the manifest accepts, what the unpacker writes,
/// and what the integrity page is told.
///
/// These are the checks that stand between a release download and a script
/// run as administrator, so each refusal is asserted rather than described.
/// </summary>
public class PayloadTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "barista-payload-" + Guid.NewGuid().ToString("N"));

    public PayloadTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { /* temp */ }
    }

    private const string Commit = "dec5fd2897416b85cd77a9de7aded18fa9ade56c";

    private static readonly Dictionary<string, byte[]> Files = new()
    {
        ["Doppio/TacProvider.dll"] = Encoding.ASCII.GetBytes("MZ provider"),
        ["Doppio/install-doppio.ps1"] = Encoding.ASCII.GetBytes("Write-Host install"),
        ["Ristretto/ristretto.exe"] = Encoding.ASCII.GetBytes("MZ gate"),
    };

    private static string Sha(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

    private static byte[] ManifestJson(
        IEnumerable<(string path, string sha, long size)>? files = null, int schema = 1, string commit = Commit)
    {
        files ??= Files.Select(kv => (kv.Key, Sha(kv.Value), (long)kv.Value.Length));
        var doc = new
        {
            schemaVersion = schema,
            version = "0.2.0",
            builtUtc = "2026-10-13T20:00:00Z",
            sources = new[] { new { name = "Doppio", repository = "https://github.com/Nobrac/Doppio", commit } },
            files = files.Select(f => new { path = f.path, sha256 = f.sha, size = f.size }).ToArray(),
        };
        return JsonSerializer.SerializeToUtf8Bytes(doc);
    }

    private static PayloadManifest Manifest() => PayloadManifest.Parse(ManifestJson());

    private static MemoryStream Zip(IEnumerable<KeyValuePair<string, byte[]>> entries)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, bytes) in entries)
            {
                using var s = zip.CreateEntry(name).Open();
                s.Write(bytes);
            }
        }
        ms.Position = 0;
        return ms;
    }

    private static MemoryStream GoodZip() => Zip(Files);

    private string Root => Path.Combine(_tmp, "payload", "id");

    private static void Create(string path) => Directory.CreateDirectory(path);

    // --- the manifest ---------------------------------------------------------

    [Fact]
    public void A_valid_manifest_parses_and_gets_a_stable_id()
    {
        var a = PayloadManifest.Parse(ManifestJson());
        var b = PayloadManifest.Parse(ManifestJson());

        Assert.Equal(3, a.Files.Count);
        Assert.Matches("^[0-9a-f]{16}$", a.Id);
        Assert.Equal(a.Id, b.Id);
        Assert.True(a.Has("Ristretto"));
        Assert.Equal(Commit, a.Sources[0].Commit);
    }

    [Fact]
    public void A_different_manifest_gets_a_different_folder()
    {
        var other = ManifestJson(Files.Select(kv => (kv.Key, Sha(kv.Value), (long)kv.Value.Length))
            .Take(2));
        Assert.NotEqual(Manifest().Id, PayloadManifest.Parse(other).Id);
    }

    [Theory]
    [InlineData("../evil.ps1")]
    [InlineData("Doppio/../evil.ps1")]
    [InlineData("Doppio/..")]
    [InlineData("Doppio\\evil.ps1")]
    [InlineData("C:/Windows/evil.ps1")]
    [InlineData("/Doppio/evil.ps1")]
    [InlineData("Doppio/sub/evil.ps1")]
    [InlineData("Other/evil.ps1")]
    [InlineData("Doppio/evil.ps1:stream")]
    [InlineData("Doppio/")]
    public void Unsafe_paths_are_refused(string path)
    {
        var json = ManifestJson([(path, new string('a', 64), 1)]);
        Assert.Throws<InvalidDataException>(() => PayloadManifest.Parse(json));
    }

    [Theory]
    [InlineData("ABCDEF0000000000000000000000000000000000000000000000000000000000")]
    [InlineData("abc")]
    [InlineData("")]
    public void Hashes_must_be_full_lowercase_sha256(string sha)
    {
        var json = ManifestJson([("Doppio/a.dll", sha, 1)]);
        Assert.Throws<InvalidDataException>(() => PayloadManifest.Parse(json));
    }

    [Fact]
    public void A_file_listed_twice_is_refused()
    {
        var sha = new string('a', 64);
        var json = ManifestJson([("Doppio/a.dll", sha, 1), ("doppio/A.dll", sha, 1)]);
        Assert.Throws<InvalidDataException>(() => PayloadManifest.Parse(json));
    }

    [Fact]
    public void An_unknown_schema_is_refused()
        => Assert.Throws<InvalidDataException>(() => PayloadManifest.Parse(ManifestJson(schema: 2)));

    [Fact]
    public void A_short_commit_id_is_refused()
        => Assert.Throws<InvalidDataException>(() => PayloadManifest.Parse(ManifestJson(commit: "dec5fd2")));

    [Fact]
    public void Broken_json_is_refused_with_a_readable_reason()
        => Assert.Throws<InvalidDataException>(() => PayloadManifest.Parse("{"u8.ToArray()));

    // --- unpacking --------------------------------------------------------------

    [Fact]
    public void A_good_payload_unpacks_and_every_row_matches()
    {
        var shown = new List<FileVerification>();
        var r = PayloadExtractor.Prepare(Manifest(), GoodZip, Root, Create, shown.Add);

        Assert.True(r.Unpacked);
        Assert.True(r.AllMatch);
        Assert.Equal(3, shown.Count);
        Assert.All(r.Files, f => Assert.Equal(HashStatus.Match, f.Status));
        Assert.Equal(Files["Doppio/TacProvider.dll"],
            File.ReadAllBytes(Path.Combine(Root, "Doppio", "TacProvider.dll")));
    }

    [Fact]
    public void A_folder_that_already_matches_is_reused_without_unpacking()
    {
        PayloadExtractor.Prepare(Manifest(), GoodZip, Root, Create);

        var r = PayloadExtractor.Prepare(Manifest(),
            () => throw new InvalidOperationException("must not unpack again"), Root, Create);

        Assert.False(r.Unpacked);
        Assert.True(r.AllMatch);
    }

    [Fact]
    public void A_changed_file_makes_the_folder_unpack_afresh()
    {
        PayloadExtractor.Prepare(Manifest(), GoodZip, Root, Create);
        File.WriteAllText(Path.Combine(Root, "Doppio", "install-doppio.ps1"), "Write-Host evil");

        var r = PayloadExtractor.Prepare(Manifest(), GoodZip, Root, Create);

        Assert.True(r.Unpacked);
        Assert.True(r.AllMatch);
        Assert.Equal("Write-Host install", File.ReadAllText(Path.Combine(Root, "Doppio", "install-doppio.ps1")));
    }

    [Fact]
    public void An_extra_file_makes_the_folder_unpack_afresh_and_is_gone_afterwards()
    {
        PayloadExtractor.Prepare(Manifest(), GoodZip, Root, Create);
        var planted = Path.Combine(Root, "Doppio", "profile.ps1");
        File.WriteAllText(planted, "evil");

        var r = PayloadExtractor.Prepare(Manifest(), GoodZip, Root, Create);

        Assert.True(r.Unpacked);
        Assert.True(r.AllMatch);
        Assert.False(File.Exists(planted));
    }

    [Fact]
    public void A_root_that_is_a_symbolic_link_is_refused()
    {
        var elsewhere = Path.Combine(_tmp, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        Directory.CreateDirectory(Path.GetDirectoryName(Root)!);
        Directory.CreateSymbolicLink(Root, elsewhere);

        Assert.Throws<IOException>(() => PayloadExtractor.Prepare(Manifest(), GoodZip, Root, Create));
    }

    [Fact]
    public void A_zip_entry_the_manifest_does_not_name_is_refused_and_not_written()
    {
        var entries = Files.Append(new("../evil.ps1", Encoding.ASCII.GetBytes("evil")));

        Assert.Throws<InvalidDataException>(() =>
            PayloadExtractor.Prepare(Manifest(), () => Zip(entries), Root, Create));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(Root)!, "evil.ps1")));
    }

    [Fact]
    public void A_zip_missing_a_file_is_refused()
    {
        var entries = Files.Where(kv => kv.Key != "Ristretto/ristretto.exe");
        var ex = Assert.Throws<InvalidDataException>(() =>
            PayloadExtractor.Prepare(Manifest(), () => Zip(entries), Root, Create));
        Assert.Contains("Ristretto/ristretto.exe", ex.Message);
    }

    [Fact]
    public void A_zip_entry_of_the_wrong_size_is_refused()
    {
        var entries = Files.Select(kv => kv.Key == "Doppio/TacProvider.dll"
            ? new KeyValuePair<string, byte[]>(kv.Key, Encoding.ASCII.GetBytes("MZ provider, longer"))
            : kv);
        Assert.Throws<InvalidDataException>(() =>
            PayloadExtractor.Prepare(Manifest(), () => Zip(entries), Root, Create));
    }

    [Fact]
    public void Same_size_different_content_shows_as_a_mismatch_row()
    {
        // Same length as the original, so the size check in the unpacker
        // passes and it is the hash comparison that has to catch it.
        var entries = Files.Select(kv => kv.Key == "Ristretto/ristretto.exe"
            ? new KeyValuePair<string, byte[]>(kv.Key, Encoding.ASCII.GetBytes("MZ evil"))
            : kv);

        var r = PayloadExtractor.Prepare(Manifest(), () => Zip(entries), Root, Create);

        Assert.False(r.AllMatch);
        var row = Assert.Single(r.Files, f => !f.Ok);
        Assert.Equal("Ristretto/ristretto.exe", row.File.Path);
        Assert.Equal(HashStatus.Mismatch, row.Status);
        Assert.Equal(Sha(Encoding.ASCII.GetBytes("MZ evil")), row.ActualSha256);
    }

    // --- checking -------------------------------------------------------------

    [Fact]
    public void A_deleted_file_shows_as_missing()
    {
        PayloadExtractor.Prepare(Manifest(), GoodZip, Root, Create);
        File.Delete(Path.Combine(Root, "Doppio", "TacProvider.dll"));

        var rows = PayloadVerifier.VerifyFolder(Manifest(), Root);

        Assert.Equal(HashStatus.Missing, rows.Single(r => r.File.FileName == "TacProvider.dll").Status);
        Assert.Equal(2, rows.Count(r => r.Ok));
    }

    [Fact]
    public void A_stray_subfolder_is_reported()
    {
        PayloadExtractor.Prepare(Manifest(), GoodZip, Root, Create);
        Directory.CreateDirectory(Path.Combine(Root, "Doppio", "Modules"));

        Assert.Contains(Path.Combine("Doppio", "Modules"), PayloadVerifier.Unexpected(Manifest(), Root));
    }

    // --- the exe ----------------------------------------------------------------

    [Fact]
    public void An_assembly_without_a_payload_is_a_developer_build()
        => Assert.Null(EmbeddedPayload.TryLoad(typeof(PayloadTests).Assembly));
}
