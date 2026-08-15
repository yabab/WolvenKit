using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using WolvenKit.Common;
using WolvenKit.Common.PhysX;
using WolvenKit.Common.Services;
using WolvenKit.Core.Interfaces;
using WolvenKit.RED4.Archive.Buffer;
using WolvenKit.RED4.Archive.CR2W;
using WolvenKit.RED4.CR2W;
using WolvenKit.RED4.Types;
using static WolvenKit.RED4.Types.Enums;

using PxVec3 = SharpDX.Vector3;
using SysQuat = System.Numerics.Quaternion;
using SysVec3 = System.Numerics.Vector3;
using SysVec4 = System.Numerics.Vector4;

namespace WolvenKit.Modkit.RED4.NavBaker;

public sealed class NavExtractOptions
{
    /// <summary>Case-insensitive substring match on the sector's depot path. Null takes all.</summary>
    public string? SectorFilter { get; init; }

    /// <summary>Stop after this many sectors. Zero means no limit. Useful for a smoke test.</summary>
    public int MaxSectors { get; init; }

    /// <summary>
    /// Take only collision nodes at this LOD. Negative takes every LOD, which over-blocks where
    /// LODs overlap - safe for navigation, but wasteful.
    /// </summary>
    public int Lod { get; init; } = -1;

    /// <summary>
    /// Drop shapes whose collision preset name contains any of these, case-insensitively.
    /// </summary>
    /// <remarks>
    /// Empty by default, and deliberately so. Everything reached here already came out of a
    /// worldCollisionNode, which is by definition static world collision - the node type is the
    /// filter. An earlier version filtered on the shape's proxy-type byte instead and discarded
    /// 100% of the geometry: in the compiled buffer that byte does not carry the same meaning as
    /// the script-facing physicsProxyType enum. Use the histograms this class records to decide
    /// what is worth excluding, rather than assuming a field's semantics.
    /// </remarks>
    public string[] ExcludePresets { get; init; } = Array.Empty<string>();
}

public sealed class NavExtractStats
{
    public int SectorsScanned { get; set; }
    public int SectorsWithCollision { get; set; }
    public int SectorsFailed { get; set; }
    public int CollisionNodes { get; set; }
    public long Actors { get; set; }
    public long Instances { get; set; }
    public long SkippedByPreset { get; set; }
    public long SkippedByLod { get; set; }
    public long UnresolvedMeshes { get; set; }
    public int Meshes { get; set; }
    public long MeshTriangles { get; set; }
    public SysVec3 BoundsMin { get; set; }
    public SysVec3 BoundsMax { get; set; }

    /// <summary>Instance counts keyed by shape type, for sanity-checking a run.</summary>
    public Dictionary<NavGeoShapeType, long> ByShape { get; } = new();

    /// <summary>
    /// Shape counts keyed by proxy type, recorded before filtering. This is the histogram that
    /// tells you whether the proxy filter is keeping the right things.
    /// </summary>
    public Dictionary<byte, long> ByProxy { get; } = new();

    /// <summary>Shape counts keyed by collision preset name, recorded before filtering.</summary>
    public Dictionary<string, long> ByPreset { get; } = new();

    /// <summary>Collision-node counts keyed by LOD, so the LOD field can be understood empirically.</summary>
    public Dictionary<int, int> NodesByLod { get; } = new();
}

/// <summary>
/// Pulls every static collision shape out of the game's streaming sectors and writes it to a
/// .vnavgeo file in world space.
/// </summary>
/// <remarks>
/// This exists because the runtime alternative - probing the physics scene from inside the game -
/// can only see sectors that are currently streamed in. Anything beyond the streaming radius
/// reads back as empty air, which is not a slow answer but a wrong one. Reading the archives
/// directly sees the whole world at once.
/// </remarks>
public sealed class NavGeometryExtractor
{
    private const string SectorExtension = ".streamingsector";

    private readonly IArchiveManager _archive;
    private readonly Red4ParserService _parser;
    private readonly GeometryCacheService _geometryCache;
    private readonly ILoggerService _logger;

    public NavGeometryExtractor(
        IArchiveManager archive,
        Red4ParserService parser,
        GeometryCacheService geometryCache,
        ILoggerService logger)
    {
        _archive = archive;
        _parser = parser;
        _geometryCache = geometryCache;
        _logger = logger;
    }

    public NavExtractStats Extract(
        NavExtractOptions options,
        Stream output,
        IProgress<double>? progress = null,
        CancellationToken token = default)
    {
        var stats = new NavExtractStats();

        var grouped = _archive.GetGroupedFiles();
        if (!grouped.TryGetValue(SectorExtension, out var sectorFiles))
        {
            _logger.Error($"No {SectorExtension} files found. Are the game archives loaded?");
            return stats;
        }

        var sectors = sectorFiles.ToList();

        if (!string.IsNullOrEmpty(options.SectorFilter))
        {
            sectors = sectors
                .Where(f => f.FileName.Contains(options.SectorFilter, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        if (options.MaxSectors > 0 && sectors.Count > options.MaxSectors)
        {
            sectors = sectors.Take(options.MaxSectors).ToList();
        }

        _logger.Info($"Extracting collision from {sectors.Count} sectors.");

        // Meshes are shared across many actors, so the library is keyed on the cache object
        // itself. Reference identity is both cheaper and more reliable than re-hashing geometry
        // that the cache has already de-duplicated for us.
        var meshIndices = new Dictionary<PhysXMesh, uint>(ReferenceEqualityComparer.Instance);

        using (var writer = new NavGeoWriter(output))
        {
            for (var i = 0; i < sectors.Count; i++)
            {
                token.ThrowIfCancellationRequested();

                ProcessSector(sectors[i], options, writer, meshIndices, stats);

                if (progress is not null && sectors.Count > 0)
                {
                    progress.Report((double)(i + 1) / sectors.Count);
                }
            }

            writer.Close();
            stats.Instances = writer.InstanceCount;
            stats.Meshes = writer.MeshCount;
            stats.BoundsMin = writer.BoundsMin;
            stats.BoundsMax = writer.BoundsMax;
        }

        return stats;
    }

    private void ProcessSector(
        IGameFile file,
        NavExtractOptions options,
        NavGeoWriter writer,
        Dictionary<PhysXMesh, uint> meshIndices,
        NavExtractStats stats)
    {
        stats.SectorsScanned++;

        CR2WFile? cr2w;
        try
        {
            using var ms = new MemoryStream();
            file.Extract(ms);
            ms.Position = 0;

            using var br = new BinaryReader(ms);
            cr2w = _parser.ReadRed4File(br);
        }
        catch (Exception e)
        {
            stats.SectorsFailed++;
            _logger.Warning($"Failed to read sector {file.FileName}: {e.Message}");
            return;
        }

        if (cr2w?.RootChunk is not worldStreamingSector sector)
        {
            return;
        }

        var found = false;

        foreach (var handle in sector.Nodes)
        {
            if (handle?.Chunk is not worldCollisionNode node)
            {
                continue;
            }

            var lod = (int)(byte)node.Lod;
            stats.NodesByLod.TryGetValue(lod, out var lodCount);
            stats.NodesByLod[lod] = lodCount + 1;

            if (options.Lod >= 0 && lod != options.Lod)
            {
                stats.SkippedByLod++;
                continue;
            }

            if (node.CompiledData?.Data is not CollisionBuffer collision)
            {
                continue;
            }

            found = true;
            stats.CollisionNodes++;
            EmitActors(node, collision, options, writer, meshIndices, stats);
        }

        if (found)
        {
            stats.SectorsWithCollision++;
        }
    }

    private void EmitActors(
        worldCollisionNode node,
        CollisionBuffer collision,
        NavExtractOptions options,
        NavGeoWriter writer,
        Dictionary<PhysXMesh, uint> meshIndices,
        NavExtractStats stats)
    {
        var sectorHash = (ulong)node.SectorHash;

        foreach (var actor in collision.Actors)
        {
            stats.Actors++;

            // Actor positions are absolute world coordinates in 15.17 fixed point, which spans
            // +/-16 km - comfortably the whole map, so no sector offset is involved.
            var actorPosition = new SysVec3(actor.Position.X, actor.Position.Y, actor.Position.Z);
            var actorRotation = ToQuaternion(actor.Orientation);
            var actorScale = new SysVec3(actor.Scale.X, actor.Scale.Y, actor.Scale.Z);

            foreach (var shape in actor.Shapes)
            {
                var proxyType = (physicsProxyType)shape.ProxyType;

                var proxyKey = (byte)proxyType;
                stats.ByProxy.TryGetValue(proxyKey, out var proxyCount);
                stats.ByProxy[proxyKey] = proxyCount + 1;

                var preset = shape.Preset.GetResolvedText() ?? string.Empty;
                stats.ByPreset.TryGetValue(preset, out var presetCount);
                stats.ByPreset[preset] = presetCount + 1;

                if (IsExcludedPreset(preset, options.ExcludePresets))
                {
                    stats.SkippedByPreset++;
                    continue;
                }

                var shapeType = (physicsShapeType)shape.ShapeType;

                // Compose actor and shape transforms. The actor's scale applies to the shape's
                // offset as well as to its geometry.
                var localOffset = new SysVec3(shape.Position.X, shape.Position.Y, shape.Position.Z);
                var worldPosition = actorPosition + SysVec3.Transform(actorScale * localOffset, actorRotation);
                var worldRotation = SysQuat.Normalize(SysQuat.Concatenate(ToQuaternion(shape.Rotation), actorRotation));

                var meshIndex = NavGeoFormat.NoMesh;
                var size = SysVec3.Zero;
                SysVec3 localMin;
                SysVec3 localMax;

                if (shape is CollisionShapeMesh meshShape)
                {
                    var resolved = _geometryCache.GetEntry(sectorHash, meshShape.Hash);
                    if (resolved is null)
                    {
                        stats.UnresolvedMeshes++;
                        continue;
                    }

                    if (!meshIndices.TryGetValue(resolved, out meshIndex))
                    {
                        var converted = ConvertMesh(resolved);
                        if (converted is null)
                        {
                            stats.UnresolvedMeshes++;
                            continue;
                        }

                        meshIndex = writer.AddMesh(converted);
                        meshIndices[resolved] = meshIndex;
                        stats.MeshTriangles += converted.Indices.Count / 3;
                    }

                    var library = writer.GetMesh(meshIndex);
                    localMin = library.BoundsMin;
                    localMax = library.BoundsMax;
                }
                else if (shape is CollisionShapeSimple simple)
                {
                    size = new SysVec3(simple.Size.X, simple.Size.Y, simple.Size.Z);
                    (localMin, localMax) = PrimitiveLocalBounds(shapeType, size);
                }
                else
                {
                    continue;
                }

                writer.WriteInstance(
                    (NavGeoShapeType)(byte)shapeType,
                    (byte)proxyType,
                    worldPosition,
                    worldRotation,
                    actorScale,
                    size,
                    meshIndex);

                var (worldMin, worldMax) = TransformBounds(localMin, localMax, worldPosition, worldRotation, actorScale);
                writer.GrowBounds(worldMin, worldMax);

                var navShape = (NavGeoShapeType)(byte)shapeType;
                stats.ByShape.TryGetValue(navShape, out var shapeCount);
                stats.ByShape[navShape] = shapeCount + 1;
            }
        }
    }

    private static bool IsExcludedPreset(string preset, string[] excluded)
    {
        if (excluded.Length == 0 || preset.Length == 0)
        {
            return false;
        }

        foreach (var pattern in excluded)
        {
            if (preset.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Local-space bounds of a primitive. Box sizes are half-extents, matching PhysX; a sphere
    /// carries its radius in X, and a capsule its radius in X and half-height in Y.
    /// </summary>
    private static (SysVec3 Min, SysVec3 Max) PrimitiveLocalBounds(physicsShapeType shapeType, SysVec3 size)
    {
        switch (shapeType)
        {
            case physicsShapeType.Sphere:
            {
                var r = new SysVec3(size.X, size.X, size.X);
                return (-r, r);
            }

            case physicsShapeType.Capsule:
            {
                // Capsules extend along X in PhysX; the caps add the radius at each end.
                var extent = new SysVec3(size.Y + size.X, size.X, size.X);
                return (-extent, extent);
            }

            default:
                return (-size, size);
        }
    }

    /// <summary>
    /// World AABB of a transformed local AABB. Rotates all eight corners rather than using the
    /// absolute-matrix shortcut, because the result feeds the grid sizing and a loose bound here
    /// costs voxelisation time later.
    /// </summary>
    private static (SysVec3 Min, SysVec3 Max) TransformBounds(
        SysVec3 localMin,
        SysVec3 localMax,
        SysVec3 position,
        SysQuat rotation,
        SysVec3 scale)
    {
        var min = new SysVec3(float.MaxValue, float.MaxValue, float.MaxValue);
        var max = new SysVec3(float.MinValue, float.MinValue, float.MinValue);

        for (var corner = 0; corner < 8; corner++)
        {
            var local = new SysVec3(
                (corner & 1) == 0 ? localMin.X : localMax.X,
                (corner & 2) == 0 ? localMin.Y : localMax.Y,
                (corner & 4) == 0 ? localMin.Z : localMax.Z);

            var world = position + SysVec3.Transform(local * scale, rotation);
            min = SysVec3.Min(min, world);
            max = SysVec3.Max(max, world);
        }

        return (min, max);
    }

    private static NavGeoMesh? ConvertMesh(PhysXMesh source)
    {
        switch (source)
        {
            case BV4TriangleMesh triangles:
            {
                var mesh = new NavGeoMesh { Kind = NavGeoFormat.MeshKindTriangles };

                foreach (var v in triangles.Vertices)
                {
                    mesh.Vertices.Add(ToSystem(v));
                }

                foreach (var tri in triangles.Triangles)
                {
                    if (tri.Count < 3)
                    {
                        continue;
                    }

                    mesh.Indices.Add(tri[0]);
                    mesh.Indices.Add(tri[1]);
                    mesh.Indices.Add(tri[2]);
                }

                mesh.BoundsMin = ToSystem(triangles.AABB.Minimum);
                mesh.BoundsMax = ToSystem(triangles.AABB.Maximum);
                return mesh.Vertices.Count > 0 && mesh.Indices.Count > 0 ? mesh : null;
            }

            case ConvexMesh convex:
            {
                var mesh = new NavGeoMesh { Kind = NavGeoFormat.MeshKindConvex };

                foreach (var v in convex.HullData.HullVertices)
                {
                    mesh.Vertices.Add(ToSystem(v));
                }

                foreach (var polygon in convex.HullData.Polygons)
                {
                    mesh.Planes.Add(new SysVec4(
                        polygon.Plane.N.X,
                        polygon.Plane.N.Y,
                        polygon.Plane.N.Z,
                        polygon.Plane.D));
                }

                mesh.BoundsMin = ToSystem(convex.HullData.AABB.Minimum);
                mesh.BoundsMax = ToSystem(convex.HullData.AABB.Maximum);
                return mesh.Vertices.Count > 0 && mesh.Planes.Count > 0 ? mesh : null;
            }

            default:
                return null;
        }
    }

    private static SysVec3 ToSystem(PxVec3 v) => new(v.X, v.Y, v.Z);

    private static SysQuat ToQuaternion(WolvenKit.RED4.Types.Quaternion q) => new(q.I, q.J, q.K, q.R);
}
