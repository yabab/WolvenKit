using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using WolvenKit.Common.Services;
using WolvenKit.Modkit.RED4.NavBaker;

namespace CP77Tools.Tasks;

public record NavBakeTaskOptions
{
    public FileInfo? outfile { get; init; }
    public string? gamepath { get; init; }
    public string? filter { get; init; }
    public int maxSectors { get; init; }
    public int lod { get; init; } = -1;
    public string? excludePresets { get; init; }
}

public partial class ConsoleFunctions
{
    /// <summary>
    /// Extracts every static collision shape in the world to a .vnavgeo file, for the vnav
    /// voxeliser to turn into a baked navigation volume.
    /// </summary>
    public int NavBakeTask(NavBakeTaskOptions options)
    {
        if (options.outfile is null)
        {
            _loggerService.Error("Please supply an output file with --outfile.");
            return ERROR_BAD_ARGUMENTS;
        }

        if (string.IsNullOrEmpty(options.gamepath) || !Directory.Exists(options.gamepath))
        {
            _loggerService.Error("Please supply the Cyberpunk 2077 folder with --gamepath.");
            return ERROR_BAD_ARGUMENTS;
        }

        var exePath = new FileInfo(Path.Combine(options.gamepath, "bin", "x64", "Cyberpunk2077.exe"));
        if (!exePath.Exists)
        {
            _loggerService.Error($"Not a game folder (no {exePath.FullName}).");
            return ERROR_BAD_ARGUMENTS;
        }

        _loggerService.Info("Loading game archives...");
        _archiveManager.LoadGameArchives(exePath);

        // GeometryCacheService only needs the archive manager and the parser, both of which we
        // already hold, so it is built here rather than threaded through the shared constructor.
        var geometryCache = new GeometryCacheService(_archiveManager, _wolvenkitFileService);

        var extractor = new NavGeometryExtractor(
            _archiveManager,
            _wolvenkitFileService,
            geometryCache,
            _loggerService);

        var extractOptions = new NavExtractOptions
        {
            SectorFilter = options.filter,
            MaxSectors = options.maxSectors,
            Lod = options.lod,
            ExcludePresets = string.IsNullOrWhiteSpace(options.excludePresets)
                ? Array.Empty<string>()
                : options.excludePresets.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        };

        options.outfile.Directory?.Create();

        var stopwatch = Stopwatch.StartNew();
        NavExtractStats stats;

        try
        {
            using var stream = options.outfile.Open(FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            stats = extractor.Extract(extractOptions, stream, _progressService);
        }
        catch (Exception e)
        {
            _loggerService.Error($"Extraction failed: {e}");
            return ERROR_GENERAL_ERROR;
        }

        stopwatch.Stop();

        _loggerService.Success($"Wrote {options.outfile.FullName} in {stopwatch.Elapsed:hh\\:mm\\:ss}.");
        _loggerService.Info(
            $"Sectors: {stats.SectorsScanned} scanned, {stats.SectorsWithCollision} with collision, {stats.SectorsFailed} failed.");
        _loggerService.Info(
            $"Collision nodes: {stats.CollisionNodes}, actors: {stats.Actors}, instances: {stats.Instances}.");
        _loggerService.Info(
            $"Meshes: {stats.Meshes} unique ({stats.MeshTriangles} triangles), {stats.UnresolvedMeshes} unresolved.");
        _loggerService.Info(
            $"Skipped: {stats.SkippedByPreset} by preset, {stats.SkippedByLod} by LOD.");

        if (stats.ByShape.Count > 0)
        {
            var shapes = string.Join(", ", stats.ByShape.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"));
            _loggerService.Info($"By shape: {shapes}");
        }

        if (stats.ByProxy.Count > 0)
        {
            var proxies = string.Join(", ", stats.ByProxy.OrderBy(kv => kv.Key)
                .Select(kv => $"{(WolvenKit.RED4.Types.Enums.physicsProxyType)kv.Key}({kv.Key})={kv.Value}"));
            _loggerService.Info($"By proxy type: {proxies}");
        }

        if (stats.ByPreset.Count > 0)
        {
            var presets = string.Join(", ", stats.ByPreset.OrderByDescending(kv => kv.Value).Take(25)
                .Select(kv => $"{(kv.Key.Length == 0 ? "<none>" : kv.Key)}={kv.Value}"));
            _loggerService.Info($"Top presets ({stats.ByPreset.Count} distinct): {presets}");
        }

        if (stats.NodesByLod.Count > 0)
        {
            var lods = string.Join(", ", stats.NodesByLod.OrderBy(kv => kv.Key).Select(kv => $"lod{kv.Key}={kv.Value}"));
            _loggerService.Info($"Collision nodes by LOD: {lods}");
        }

        _loggerService.Info(
            $"World bounds: ({stats.BoundsMin.X:F1}, {stats.BoundsMin.Y:F1}, {stats.BoundsMin.Z:F1}) .. " +
            $"({stats.BoundsMax.X:F1}, {stats.BoundsMax.Y:F1}, {stats.BoundsMax.Z:F1})");

        if (stats.Instances == 0)
        {
            _loggerService.Warning("No collision instances were extracted - the output is empty.");
            return ERROR_COMPLETED_WITH_WARNINGS;
        }

        return 0;
    }
}
