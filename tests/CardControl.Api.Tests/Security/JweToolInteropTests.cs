using System.Diagnostics;
using CardControl.Api.Security;

namespace CardControl.Api.Tests.Security;

/// <summary>
/// Proves the decryptor accepts exactly what the starter kit's jwe-tool (the partner's side) produces,
/// keys and all, rather than only what our own test encryptor produces.
/// Runs the real tool, so it needs the .NET SDK on PATH and takes a few seconds the first time.
/// </summary>
[Trait("Category", "Interop")]
public sealed class JweToolInteropTests : IDisposable
{
    private readonly string _keyDir = Directory.CreateTempSubdirectory("jwe-tool-keys-").FullName;

    public void Dispose() => Directory.Delete(_keyDir, recursive: true);

    [Fact]
    public async Task Decrypts_a_pan_encrypted_by_the_starter_kit_jwe_tool()
    {
        await RunTool("keygen", "--kid", "bank-interop-1", "--out", _keyDir);
        var jwe = (await RunTool("encrypt", "--key", Path.Combine(_keyDir, "bank-interop-1.public.pem"), "4111111111111111")).Trim();

        using var ring = new PanKeyRing(new PanEncryptionOptions
        {
            Keys = [new() { Kid = "bank-interop-1", PrivateKeyPath = "bank-interop-1.private.pem" }],
        }, _keyDir);

        var pan = new JweDecryptor(ring).Decrypt(jwe);

        Assert.Equal("4111111111111111", pan.RevealDigits());
    }

    private static async Task<string> RunTool(params string[] toolArgs)
    {
        var repoRoot = FindRepoRoot();
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in (string[])["run", Path.Combine("tools", "jwe-tool.cs"), "--", .. toolArgs])
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await process.WaitForExitAsync(timeout.Token);

        Assert.True(process.ExitCode == 0, $"jwe-tool {toolArgs[0]} failed: {await stderr}");
        return await stdout;
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "tools", "jwe-tool.cs")))
                return dir.FullName;
        throw new InvalidOperationException("Couldn't find the repository root (tools/jwe-tool.cs).");
    }
}
