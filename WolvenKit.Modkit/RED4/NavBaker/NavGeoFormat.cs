using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;

namespace WolvenKit.Modkit.RED4.NavBaker;

/// <summary>
/// The ".vnavgeo" intermediate format: every static collision shape in the world, expressed in
/// world space, with a de-duplicated library of the meshes they reference.
/// </summary>
/// <remarks>
/// This is deliberately a handoff format, not a finished nav asset. It carries geometry and
/// nothing else - no voxels, no resolution, no agent assumptions - because the thing that
/// consumes it (the vnav voxeliser) owns those decisions and must be free to re-run at a
/// different resolution without re-reading 60 GB of archives.
///
/// Layout, all little-endian:
///
///     Header      (fixed size, patched on close once the totals are known)
///     Instances   InstanceCount * <see cref="InstanceRecordSize"/> bytes, fixed stride
///     Mesh table  MeshCount variable-length records, at MeshTableOffset
///
/// Instances stream straight to disk as they are found, so peak memory stays bounded by the
/// mesh library rather than by the instance count (which runs to millions).
/// </remarks>
public static class NavGeoFormat
{
    /// <summary>File magic. Written as raw bytes so it reads as text in a hex dump.</summary>
    public static readonly byte[] Magic = "VNAVGEO\0"u8.ToArray();

    public const uint Version = 1;

    /// <summary>Header is padded to a round size so instances start cache-aligned.</summary>
    public const int HeaderSize = 128;

    /// <summary>
    /// position(12) + rotation(16) + scale(12) + size(12) + meshIndex(4) + shapeType(1)
    /// + proxyType(1) + padding(2).
    /// </summary>
    public const int InstanceRecordSize = 60;

    /// <summary>Sentinel <c>meshIndex</c> for a primitive that references no mesh.</summary>
    public const uint NoMesh = 0xFFFFFFFFu;

    public const byte MeshKindTriangles = 0;
    public const byte MeshKindConvex = 1;
}

/// <summary>Shape kinds, matching the game's own physicsShapeType ordering.</summary>
public enum NavGeoShapeType : byte
{
    Box = 0,
    Sphere = 1,
    Capsule = 2,
    ConvexMesh = 3,
    TriangleMesh = 4
}

/// <summary>
/// One collision mesh, already resolved out of the geometry cache.
/// </summary>
/// <remarks>
/// A convex mesh carries its half-space planes as well as its hull vertices. The planes make
/// the voxel test a handful of dot products rather than a point-in-hull reconstruction, and the
/// vertices are still needed for a tight bounding box.
/// </remarks>
public sealed class NavGeoMesh
{
    public byte Kind { get; init; }

    public List<Vector3> Vertices { get; init; } = new();

    /// <summary>Flat triangle list, three indices per triangle. Empty for convex hulls.</summary>
    public List<uint> Indices { get; init; } = new();

    /// <summary>Half-space planes as (nx, ny, nz, d). Empty for triangle meshes.</summary>
    public List<Vector4> Planes { get; init; } = new();

    public Vector3 BoundsMin { get; set; }

    public Vector3 BoundsMax { get; set; }
}

/// <summary>
/// Streams a .vnavgeo file. Instances are written as they arrive; the mesh table is buffered and
/// flushed on <see cref="Close"/>, which then rewinds and patches the header.
/// </summary>
public sealed class NavGeoWriter : IDisposable
{
    private readonly BinaryWriter _writer;
    private readonly List<NavGeoMesh> _meshes = new();

    private uint _instanceCount;
    private Vector3 _boundsMin = new(float.MaxValue, float.MaxValue, float.MaxValue);
    private Vector3 _boundsMax = new(float.MinValue, float.MinValue, float.MinValue);
    private bool _closed;

    public NavGeoWriter(Stream stream)
    {
        _writer = new BinaryWriter(stream);

        // Reserve the header; it cannot be filled in until the totals are known.
        _writer.Write(new byte[NavGeoFormat.HeaderSize]);
    }

    public uint InstanceCount => _instanceCount;

    public int MeshCount => _meshes.Count;

    public Vector3 BoundsMin => _boundsMin;

    public Vector3 BoundsMax => _boundsMax;

    /// <summary>
    /// Adds a mesh to the library and returns its index. Callers are responsible for
    /// de-duplication - the extractor keys on the resolved cache object, which is cheaper and
    /// more reliable than hashing the geometry back out.
    /// </summary>
    public uint AddMesh(NavGeoMesh mesh)
    {
        _meshes.Add(mesh);
        return (uint)(_meshes.Count - 1);
    }

    public NavGeoMesh GetMesh(uint index) => _meshes[(int)index];

    public void WriteInstance(
        NavGeoShapeType shapeType,
        byte proxyType,
        Vector3 position,
        Quaternion rotation,
        Vector3 scale,
        Vector3 size,
        uint meshIndex)
    {
        _writer.Write(position.X);
        _writer.Write(position.Y);
        _writer.Write(position.Z);

        _writer.Write(rotation.X);
        _writer.Write(rotation.Y);
        _writer.Write(rotation.Z);
        _writer.Write(rotation.W);

        _writer.Write(scale.X);
        _writer.Write(scale.Y);
        _writer.Write(scale.Z);

        _writer.Write(size.X);
        _writer.Write(size.Y);
        _writer.Write(size.Z);

        _writer.Write(meshIndex);
        _writer.Write((byte)shapeType);
        _writer.Write(proxyType);
        _writer.Write((ushort)0);

        _instanceCount++;
    }

    /// <summary>
    /// Grows the recorded world bounds. Called with each instance's own bounds so the voxeliser
    /// can size its grid without a pre-pass over the whole file.
    /// </summary>
    public void GrowBounds(Vector3 min, Vector3 max)
    {
        _boundsMin = Vector3.Min(_boundsMin, min);
        _boundsMax = Vector3.Max(_boundsMax, max);
    }

    public void Close()
    {
        if (_closed)
        {
            return;
        }
        _closed = true;

        var meshTableOffset = _writer.BaseStream.Position;

        foreach (var mesh in _meshes)
        {
            _writer.Write(mesh.Kind);
            _writer.Write((byte)0);
            _writer.Write((ushort)0);

            _writer.Write((uint)mesh.Vertices.Count);
            _writer.Write((uint)mesh.Indices.Count);
            _writer.Write((uint)mesh.Planes.Count);

            _writer.Write(mesh.BoundsMin.X);
            _writer.Write(mesh.BoundsMin.Y);
            _writer.Write(mesh.BoundsMin.Z);
            _writer.Write(mesh.BoundsMax.X);
            _writer.Write(mesh.BoundsMax.Y);
            _writer.Write(mesh.BoundsMax.Z);

            foreach (var v in mesh.Vertices)
            {
                _writer.Write(v.X);
                _writer.Write(v.Y);
                _writer.Write(v.Z);
            }

            foreach (var i in mesh.Indices)
            {
                _writer.Write(i);
            }

            foreach (var p in mesh.Planes)
            {
                _writer.Write(p.X);
                _writer.Write(p.Y);
                _writer.Write(p.Z);
                _writer.Write(p.W);
            }
        }

        // An empty extraction would otherwise leave the bounds inverted.
        if (_instanceCount == 0)
        {
            _boundsMin = Vector3.Zero;
            _boundsMax = Vector3.Zero;
        }

        _writer.Flush();
        _writer.BaseStream.Position = 0;

        _writer.Write(NavGeoFormat.Magic, 0, NavGeoFormat.Magic.Length);
        _writer.Write(NavGeoFormat.Version);
        _writer.Write((uint)NavGeoFormat.InstanceRecordSize);
        _writer.Write(_instanceCount);
        _writer.Write((uint)_meshes.Count);
        _writer.Write((ulong)meshTableOffset);
        _writer.Write(_boundsMin.X);
        _writer.Write(_boundsMin.Y);
        _writer.Write(_boundsMin.Z);
        _writer.Write(_boundsMax.X);
        _writer.Write(_boundsMax.Y);
        _writer.Write(_boundsMax.Z);

        _writer.Flush();
    }

    public void Dispose()
    {
        Close();
        _writer.Dispose();
    }
}
