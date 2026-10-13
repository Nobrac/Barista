using System.Text.RegularExpressions;
using Barista.Services;
using Xunit;

namespace Barista.Core.Tests;

/// <summary>
/// Keeps tools\build-payload.ps1 and Preflight's file lists in step.
///
/// The standalone release is only as good as the payload's file list: a file
/// the wizard checks for and the payload builder forgot is not a build error,
/// not a test failure and not visible here at all. It shows up as a red row on
/// the integrity page of a stranger's machine, with no compiler to fix it with.
///
/// So the lists are asserted in both directions - nothing the wizard needs is
/// left out, and nothing is packed that no list names.
/// </summary>
public class PackagingTests
{
    private const string PayloadBuilder = "build-payload.ps1";

    private static string Script(string name = PayloadBuilder)
    {
        var path = Path.Combine(AppContext.BaseDirectory, name);
        Assert.True(File.Exists(path),
            $"{name} was not copied next to the tests ({path}). "
            + "See the None items in Barista.Core.Tests.csproj.");
        return File.ReadAllText(path);
    }

    /// <summary>The files one `$Name = @( ... )` list in the script packs.</summary>
    private static HashSet<string> Packed(string variable, string script = PayloadBuilder)
    {
        var block = Regex.Match(Script(script), $@"\${variable}\s*=\s*@\((.*?)\n\)", RegexOptions.Singleline);
        Assert.True(block.Success, $"${variable} is not an @( ... ) list in {script}.");

        return new HashSet<string>(
            Regex.Matches(block.Groups[1].Value, "'([^']+)'").Select(m => m.Groups[1].Value),
            StringComparer.OrdinalIgnoreCase);
    }

    private static HashSet<string> Needed(params string[][] lists) =>
        new(lists.SelectMany(l => l), StringComparer.OrdinalIgnoreCase);

    private static HashSet<string> DoppioNeeded() => Needed(
        Preflight.DoppioCoreFiles,
        Preflight.DoppioFilterFiles,
        Preflight.DoppioRecoveryFiles,
        Preflight.DoppioSubAuthFiles,
        Preflight.DoppioSubAuthRecoveryFiles,
        Preflight.DoppioAuthPackageFiles,
        Preflight.DoppioAuthPackageRecoveryFiles,
        Preflight.DoppioDenyFiles);

    private static HashSet<string> RistrettoNeeded() => Needed(
        Preflight.RistrettoFiles,
        Preflight.RistrettoRecoveryFiles);

    private static void AssertCovers(string variable, HashSet<string> needed, string script = PayloadBuilder)
    {
        var packed = Packed(variable, script);

        var missing = needed.Except(packed, StringComparer.OrdinalIgnoreCase).ToArray();
        Assert.True(missing.Length == 0,
            $"{script} ${variable} does not pack: {string.Join(", ", missing)}. "
            + "The wizard's preflight blocks on those, on a machine that cannot build them.");

        // Not tidiness. A file in the payload that no list names is still a file
        // the builder refuses to build without, so a repository that stops
        // shipping it turns into "not fully built" for a good checkout.
        var extra = packed.Except(needed, StringComparer.OrdinalIgnoreCase).ToArray();
        Assert.True(extra.Length == 0,
            $"{script} ${variable} packs files no Preflight list names: "
            + string.Join(", ", extra));
    }

    [Fact]
    public void The_standalone_payload_carries_exactly_what_the_wizard_checks_for_in_Doppio()
        => AssertCovers("DoppioFiles", DoppioNeeded(), PayloadBuilder);

    [Fact]
    public void The_standalone_payload_carries_exactly_what_the_wizard_checks_for_in_Ristretto()
        => AssertCovers("RistrettoFiles", RistrettoNeeded(), PayloadBuilder);

    [Fact]
    public void The_standalone_payload_ships_no_build_script()
    {
        // The whole point of the release is a machine with no compiler on it.
        // Shipping build.bat there offers a button whose only possible outcome
        // is "install several gigabytes of Visual Studio first".
        var packed = Packed("DoppioFiles", PayloadBuilder).Concat(Packed("RistrettoFiles", PayloadBuilder));
        Assert.DoesNotContain("build.bat", packed, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("build-ristretto.bat", packed, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_binaries_a_build_produces_are_all_in_the_payload()
    {
        // The DLLs and exes are the part nobody on the receiving end can
        // recreate, so they are worth naming separately from the lists above.
        var doppio = Packed("DoppioFiles");
        Assert.All(Preflight.DoppioBuildOutputs, f => Assert.Contains(f, doppio));

        var ristretto = Packed("RistrettoFiles");
        Assert.All(Preflight.RistrettoBuildOutputs, f => Assert.Contains(f, ristretto));
    }
}
