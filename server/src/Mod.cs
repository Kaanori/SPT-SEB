using System.Threading;
using System.Threading.Tasks;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SemanticVersioning;

namespace SoundEngineBackport;

/// <summary>
/// Backports the EFT 1.0 sound engine and weapon sound banks into SPT 4.1.6.
/// The actual work is done by the bundles shipped in ./bundles (loaded by
/// SPTarkov's BundleLoader from ./bundles.json). This class only exists to
/// satisfy the server ModLoader contract.
/// </summary>
public sealed class ModMetadata : IModMetadata
{
    public string ModGuid { get; init; } = "red.soundenginebackport";
    public string Name { get; init; } = "Sound Engine Backport";
    public string Author { get; init; } = "red";
    public List<string> Contributors { get; init; } = [];
    public SemanticVersioning.Version Version { get; init; } = new SemanticVersioning.Version("1.0.0");
    public SemanticVersioning.Range SptVersion { get; init; } = new SemanticVersioning.Range("~4.1");
    public List<string> Incompatibilities { get; init; } = [];
    public Dictionary<string, SemanticVersioning.Range> ModDependencies { get; init; } = [];
    public string Url { get; init; } = "https://github.com/RedTheFoxx/SPT-SEB";
    public string License { get; init; } = "MIT";
    public bool HasPrepatcher { get; init; } = false;
}

public sealed class SoundEngineMod : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
