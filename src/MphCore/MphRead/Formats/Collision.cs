using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using MphRead.Entities;
using MphRead.Formats.Culling;
using OpenTK.Mathematics;

namespace MphRead.Formats.Collision
{
    public class EntityCollision
    {
        public Matrix4 Transform { get; set; }
        public Matrix4 Inverse1 { get; set; } // todo: do we need both?
        public Matrix4 Inverse2 { get; set; }
        public Vector3 InitialCenter { get; set; }
        public Vector3 CurrentCenter { get; set; }
        public float MaxDistance { get; set; }
        public EntityBase Entity { get; }
        public CollisionInstance? Collision { get; }

        public List<Vector3> DrawPoints { get; } = new List<Vector3>();

        public EntityCollision(CollisionInstance? collision, EntityBase entity)
        {
            Collision = collision;
            Entity = entity;
        }
    }

    public static class Collision
    {
        private static readonly Dictionary<string, CollisionInfo> _cache = new Dictionary<string, CollisionInfo>();
        private static readonly Dictionary<string, CollisionInfo> _fhCache = new Dictionary<string, CollisionInfo>();

        public static CollisionInstance GetCollision(ModelMetadata meta, bool extra = false)
        {
            string? path = extra ? meta.ExtraCollisionPath : meta.CollisionPath;
            Debug.Assert(path != null);
            string name = meta.Name;
            if (name == "AlimbicCapsule" && extra)
            {
                name = "AlmbCapsuleShld";
            }
            return GetCollision(path, name, meta.FirstHunt, roomLayerMask: -1, isEntity: true);
        }

        public static CollisionInstance GetCollision(RoomMetadata meta, int roomLayerMask = 0)
        {
            if (roomLayerMask == 0 && meta.NodeLayer > 0)
            {
                roomLayerMask = ((1 << meta.NodeLayer) & 0xFF) << 6;
            }
            return GetCollision(meta.CollisionPath, meta.Name, meta.FirstHunt || meta.Hybrid, roomLayerMask, isEntity: false);
        }

        private static CollisionInstance GetCollision(string path, string name, bool firstHunt, int roomLayerMask, bool isEntity)
        {
            Dictionary<string, CollisionInfo> cache = firstHunt ? _fhCache : _cache;
            if (roomLayerMask == -1 && cache.TryGetValue(path, out CollisionInfo? info))
            {
                return new CollisionInstance(name, info, isEntity);
            }
            var bytes = new ReadOnlySpan<byte>(File.ReadAllBytes(Paths.Combine(firstHunt ? Paths.FhFileSystem : Paths.FileSystem, path)));
            CollisionHeader header = Read.ReadStruct<CollisionHeader>(bytes);
            if (header.Type.MarshalString() == "wc01")
            {
                info = ReadMphCollision(header, bytes, roomLayerMask);
            }
            else
            {
                info = ReadFhCollision(bytes);
            }
            if (roomLayerMask == -1)
            {
                cache.Add(path, info);
            }
            return new CollisionInstance(name, info, isEntity);
        }

        public static MphCollisionInfo ReadMphCollision(CollisionHeader header, ReadOnlySpan<byte> bytes, int roomLayerMask)
        {
            IReadOnlyList<Vector3Fx> points = Read.DoOffsets<Vector3Fx>(bytes, header.PointOffset, header.PointCount);
            IReadOnlyList<Vector4Fx> planes = Read.DoOffsets<Vector4Fx>(bytes, header.PlaneOffset, header.PlaneCount);
            IReadOnlyList<ushort> pointIdxs = Read.DoOffsets<ushort>(bytes, header.PointIndexOffset, header.PointIndexCount);
            IReadOnlyList<CollisionData> data = Read.DoOffsets<CollisionData>(bytes, header.DataOffset, header.DataCount);
            IReadOnlyList<ushort> dataIdxs = Read.DoOffsets<ushort>(bytes, header.DataIndexOffset, header.DataIndexCount);
            IReadOnlyList<CollisionEntry> entries = Read.DoOffsets<CollisionEntry>(bytes, header.EntryOffset, header.EntryCount);
            var portals = new List<Portal>();
            foreach (RawCollisionPortal portal in Read.DoOffsets<RawCollisionPortal>(bytes, header.PortalOffset, header.PortalCount))
            {
                if ((portal.LayerMask & 4) != 0 || roomLayerMask == -1 || (portal.LayerMask & roomLayerMask) != 0)
                {
                    portals.Add(new Portal(portal));
                }
            }
            var indexMap = new Dictionary<ushort, ushort>();
            var finalData = new List<CollisionData>();
            var finalIndices = new List<ushort>();
            var finalEntries = new List<CollisionEntry>();
            if (roomLayerMask == -1)
            {
                // preserve order
                finalData.AddRange(data);
                finalIndices.AddRange(dataIdxs);
                finalEntries.AddRange(entries);
            }
            else
            {
                foreach (CollisionEntry entry in entries)
                {
                    if (entry.DataCount > 0)
                    {
                        ushort newCount = 0;
                        ushort newStartIndex = (ushort)finalIndices.Count;
                        for (int i = 0; i < entry.DataCount; i++)
                        {
                            ushort oldIndex = dataIdxs[entry.DataStartIndex + i];
                            if (indexMap.TryGetValue(oldIndex, out ushort newIndex))
                            {
                                finalIndices.Add(newIndex);
                                newCount++;
                            }
                            else
                            {
                                CollisionData item = data[oldIndex];
                                if ((item.LayerMask & 4) != 0 || (item.LayerMask & roomLayerMask) != 0)
                                {
                                    newIndex = (ushort)finalData.Count;
                                    finalIndices.Add(newIndex);
                                    finalData.Add(item);
                                    newCount++;
                                    indexMap.Add(oldIndex, newIndex);
                                }
                            }
                        }
                        finalEntries.Add(new CollisionEntry(newCount, newStartIndex));
                    }
                    else
                    {
                        finalEntries.Add(entry);
                    }
                }
            }
            return new MphCollisionInfo(header, points, planes, pointIdxs, finalData, finalIndices, finalEntries, portals);
        }

        private static FhCollisionInfo ReadFhCollision(ReadOnlySpan<byte> bytes)
        {
            FhCollisionHeader header = Read.ReadStruct<FhCollisionHeader>(bytes);
            IReadOnlyList<FhCollisionData> data = Read.DoOffsets<FhCollisionData>(bytes, header.DataOffset, header.DataCount);
            IReadOnlyList<FhCollisionVector> vectors = Read.DoOffsets<FhCollisionVector>(bytes, header.VectorOffset, header.VectorCount);
            IReadOnlyList<ushort> dataIndices = Read.DoOffsets<ushort>(bytes, header.DataIndexOffset, header.DataIndexCount);
            IReadOnlyList<Vector3Fx> points = Read.DoOffsets<Vector3Fx>(bytes, header.PointOffset, header.PointCount);
            IReadOnlyList<Vector4Fx> planes = Read.DoOffsets<Vector4Fx>(bytes, header.PlaneOffset, header.PlaneCount);
            IReadOnlyList<FhCollisionEntry> entries = Read.DoOffsets<FhCollisionEntry>(bytes, header.EntryOffset, header.EntryCount);
            IReadOnlyList<int> treeNodeIndices = Read.DoOffsets<int>(bytes, header.TreeNodeIndexOffset, header.TreeNodeIndexCount);
            IReadOnlyList<FhCollisionTreeNode> treeNodes = Read.DoOffsets<FhCollisionTreeNode>(bytes, header.TreeNodeOffset, header.TreeNodeCount);
            var portals = new List<Portal>();
            foreach (FhCollisionPortal portal in Read.DoOffsets<FhCollisionPortal>(bytes, header.PortalOffset, header.PortalCount))
            {
                portals.Add(new Portal(portal, vectors, points, planes));
            }
            return new FhCollisionInfo(header, points, planes, data, vectors, dataIndices, portals, entries, treeNodeIndices, treeNodes);
        }
    }

    // size: 84
    public readonly struct CollisionHeader
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        public readonly char[] Type; // wc01
        public readonly uint PointCount;
        public readonly uint PointOffset;
        public readonly uint PlaneCount;
        public readonly uint PlaneOffset;
        public readonly uint PointIndexCount;
        public readonly uint PointIndexOffset;
        public readonly uint DataCount;
        public readonly uint DataOffset;
        public readonly uint DataIndexCount;
        public readonly uint DataIndexOffset;
        public readonly int PartsX;
        public readonly int PartsY;
        public readonly int PartsZ;
        public readonly Vector3Fx MinPosition;
        public readonly uint EntryCount;
        public readonly uint EntryOffset;
        public readonly uint PortalCount;
        public readonly uint PortalOffset;
    }

    // size: 16
    public readonly struct CollisionData
    {
        public readonly int Counter; // only set at runtime
        public readonly ushort PlaneIndex;
        public readonly CollisionFlags Flags;
        public readonly ushort LayerMask;
        public readonly ushort PaddingA;
        public readonly ushort PointIndexCount; // does not include the copy of the first index at the end of the sequence
        public readonly ushort PointStartIndex;

        // bits 3-4
        public int Slipperiness => ((ushort)Flags & 0x18) >> 3;

        // bits 5-8
        public Terrain Terrain => (Terrain)(((ushort)Flags & 0x1E0) >> 5);

        public bool IgnorePlayers => Flags.TestFlag(CollisionFlags.IgnorePlayers);

        public bool IgnoreBeams => Flags.TestFlag(CollisionFlags.IgnoreBeams);

        // bits 0-1
        public int Axis => LayerMask & 3;
    }

    // size: 4
    public readonly struct CollisionEntry
    {
        public readonly ushort DataCount;
        public readonly ushort DataStartIndex;

        public CollisionEntry(ushort dataCount, ushort dataStartIndex)
        {
            DataCount = dataCount;
            DataStartIndex = dataStartIndex;
        }
    }

    // size: 224
    public readonly struct RawCollisionPortal
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 40)]
        public readonly char[] Name;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 24)]
        public readonly char[] NodeName1; // side 0 room node
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 24)]
        public readonly char[] NodeName2; // side 1 room node
        public readonly Vector3Fx Point1;
        public readonly Vector3Fx Point2;
        public readonly Vector3Fx Point3;
        public readonly Vector3Fx Point4;
        public readonly Vector4Fx Plane1;
        public readonly Vector4Fx Plane2;
        public readonly Vector4Fx Plane3;
        public readonly Vector4Fx Plane4;
        public readonly Vector4Fx Plane;
        public readonly ushort Flags;
        public readonly ushort LayerMask;
        public readonly ushort PointCount;
        public readonly byte UnusedDE;
        public readonly byte UnusedDF;
    }

    public class Portal
    {
        public bool Active { get; set; } = true;
        public string Name { get; }
        public string NodeName1 { get; }
        public string NodeName2 { get; }
        public NodeRef NodeRef1 { get; set; } = NodeRef.None;
        public NodeRef NodeRef2 { get; set; } = NodeRef.None;
        public ushort LayerMask { get; }
        public bool IsForceField { get; }
        public IReadOnlyList<Vector3> Points { get; }
        public IReadOnlyList<Vector4> Planes { get; }
        public Vector4 Plane { get; }
        public Vector3 Position { get; }
        public ushort Flags { get; }
        public byte Unknown00 { get; }
        public byte Unknown01 { get; }

        public Portal(RawCollisionPortal raw)
        {
            Debug.Assert(raw.PointCount == 4);
            Name = raw.Name.MarshalString();
            NodeName1 = raw.NodeName1.MarshalString();
            NodeName2 = raw.NodeName2.MarshalString();
            LayerMask = raw.LayerMask;
            IsForceField = Name.StartsWith("pmag");
            var points = new List<Vector3>();
            points.Add(raw.Point1.ToFloatVector());
            points.Add(raw.Point2.ToFloatVector());
            points.Add(raw.Point3.ToFloatVector());
            points.Add(raw.Point4.ToFloatVector());
            Points = points;
            Position = new Vector3(
                points.Sum(p => p.X) / points.Count,
                points.Sum(p => p.Y) / points.Count,
                points.Sum(p => p.Z) / points.Count
            );
            var planes = new List<Vector4>();
            planes.Add(raw.Plane1.ToFloatVector());
            planes.Add(raw.Plane2.ToFloatVector());
            planes.Add(raw.Plane3.ToFloatVector());
            planes.Add(raw.Plane4.ToFloatVector());
            Planes = planes;
            Plane = raw.Plane.ToFloatVector();
            Flags = raw.Flags;
            Unknown00 = raw.UnusedDE;
            Unknown01 = raw.UnusedDF;
        }

        public Portal(FhCollisionPortal raw, IReadOnlyList<FhCollisionVector> rawVectors,
            IReadOnlyList<Vector3Fx> rawPoints, IReadOnlyList<Vector4Fx> rawPlanes)
        {
            Name = raw.Name.MarshalString();
            NodeName1 = raw.NodeName1.MarshalString();
            NodeName2 = raw.NodeName2.MarshalString();
            LayerMask = 4; // always on
            var points = new List<Vector3>();
            var planes = new List<Vector4>();
            for (int i = 0; i < raw.VectorCount; i++)
            {
                FhCollisionVector vector = rawVectors[raw.VectorStartIndex + i];
                // use Point2Index so vertex order is the same as MPH
                points.Add(rawPoints[vector.Point2Index].ToFloatVector());
                planes.Add(rawPlanes[vector.PlaneIndex].ToFloatVector());
            }
            Points = points;
            Position = new Vector3(
                points.Sum(p => p.X) / points.Count,
                points.Sum(p => p.Y) / points.Count,
                points.Sum(p => p.Z) / points.Count
            );
            Planes = planes;
            Plane = raw.Plane.ToFloatVector();
            Unknown00 = raw.Field5C;
            Unknown01 = raw.Field5D;
        }

        public Portal(string nodeName1, string nodeName2, IReadOnlyList<Vector3> points,
            IReadOnlyList<Vector4> planes, Vector4 plane)
        {
            Name = $"port_{nodeName1}_{nodeName2}";
            NodeName1 = nodeName1;
            NodeName2 = nodeName2;
            LayerMask = 4;
            IsForceField = false;
            Points = points;
            Planes = planes;
            Plane = plane;
            Position = new Vector3(
                points.Sum(p => p.X) / points.Count,
                points.Sum(p => p.Y) / points.Count,
                points.Sum(p => p.Z) / points.Count
            );
            Flags = 1;
        }
    }

    [Flags]
    public enum CollisionFlags : ushort
    {
        None = 0x0,
        Damaging = 0x1,
        Bit01 = 0x2,
        Bit02 = 0x4,
        // bits 3-4: slipperiness
        // bits 5-8: terrain type
        ReflectBeams = 0x200,
        Bit10 = 0x400,
        Bit11 = 0x800,
        Bit12 = 0x1000,
        IgnorePlayers = 0x2000,
        IgnoreBeams = 0x4000,
        IgnoreScan = 0x8000
    }

    public class CollisionInstance
    {
        public string Name { get; }
        public bool Active { get; set; } = true;
        public CollisionInfo Info { get; }
        public bool IsEntity { get; }

        public Vector3 Translation { get; set; }

        public CollisionInstance(string name, CollisionInfo info, bool isEntity)
        {
            Name = name;
            Info = info;
            IsEntity = isEntity;
        }
    }

    public abstract class CollisionInfo
    {
        public bool FirstHunt { get; }
        public IReadOnlyList<Vector3> Points { get; }
        public IReadOnlyList<Vector4> Planes { get; }
        public IReadOnlyList<Portal> Portals { get; }

        public CollisionInfo(IReadOnlyList<Vector3Fx> points, IReadOnlyList<Vector4Fx> planes,
            IReadOnlyList<Portal> portals, bool firstHunt)
        {
            Points = points.Select(v => v.ToFloatVector()).ToList();
            Planes = planes.Select(p => p.ToFloatVector()).ToList();
            Portals = portals;
            FirstHunt = firstHunt;
        }

    }

    public class MphCollisionInfo : CollisionInfo
    {
        public CollisionHeader Header { get; }
        public IReadOnlyList<ushort> PointIndices { get; }
        public IReadOnlyList<CollisionData> Data { get; }
        public IReadOnlyList<ushort> DataIndices { get; }
        public IReadOnlyList<CollisionEntry> Entries { get; }
        public Vector3 MinPosition { get; }

        public MphCollisionInfo(CollisionHeader header, IReadOnlyList<Vector3Fx> points, IReadOnlyList<Vector4Fx> planes,
            IReadOnlyList<ushort> ptIdxs, IReadOnlyList<CollisionData> data, IReadOnlyList<ushort> dataIdxs,
            IReadOnlyList<CollisionEntry> entries, IReadOnlyList<Portal> portals)
            : base(points, planes, portals, firstHunt: false)
        {
            Header = header;
            PointIndices = ptIdxs;
            Data = data;
            DataIndices = dataIdxs;
            Entries = entries;
            MinPosition = header.MinPosition.ToFloatVector();
        }

        private static readonly IReadOnlyList<Vector4> _colors = new List<Vector4>()
        {
            /*  0 */ new Vector4(0.69f, 0.69f, 0.69f, 1f), // metal (gray)
            /*  1 */ new Vector4(1f, 0.612f, 0.153f, 1f), // orange holo (orange)
            /*  2 */ new Vector4(0f, 1f, 0f, 1f), // green holo (green)
            /*  3 */ new Vector4(0f, 0f, 0.858f, 1f), // blue holo (blue)
            /*  4 */ new Vector4(0.141f, 1f, 1f, 1f), // ice (light blue)
            /*  5 */ new Vector4(1f, 1f, 1f, 1f), // snow (white)
            /*  6 */ new Vector4(0.964f, 1f, 0.058f, 1f), // sand (yellow)
            /*  7 */ new Vector4(0.505f, 0.364f, 0.211f, 1f), // rock (brown)
            /*  8 */ new Vector4(0.984f, 0.701f, 0.576f, 1f), // lava (salmon)
            /*  9 */ new Vector4(0.988f, 0.463f, 0.824f, 1f), // acid (pink)
            /* 10 */ new Vector4(0.615f, 0f, 0.909f, 1f), // Gorea (purple)
            /* 11 */ new Vector4(0.85f, 0.85f, 0.85f, 1f) // unused (dark gray)
        };


        public Vector3i PartIndexFromEntry(int index)
        {
            int x = Header.PartsX;
            int xz = x * Header.PartsZ;
            int yInc = index / xz;
            int zInc = (index - yInc * xz) / x;
            int xInc = index - yInc * xz - zInc * x;
            return new Vector3i(xInc, yInc, zInc);
        }

        public int EntryIndexFromPoint(Vector3 point)
        {
            if (point.X < MinPosition.X || point.Y < MinPosition.Y || point.Z < MinPosition.Z)
            {
                return -1;
            }
            int xInc = (int)((point.X - MinPosition.X) / 4f);
            int yInc = (int)((point.Y - MinPosition.Y) / 4f);
            int zInc = (int)((point.Z - MinPosition.Z) / 4f);
            if (xInc >= Header.PartsX || yInc >= Header.PartsY || zInc >= Header.PartsZ)
            {
                return -1;
            }
            return yInc * Header.PartsX * Header.PartsZ + zInc * Header.PartsX + xInc;
        }

    }

    // size: 96
    public readonly struct FhCollisionPortal
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 40)]
        public readonly char[] Name;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public readonly char[] NodeName1; // side 0 room node
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public readonly char[] NodeName2; // side 1 room node
        public readonly Vector4Fx Plane;
        public readonly ushort VectorCount;
        public readonly ushort VectorStartIndex;
        public readonly byte Field5C;
        public readonly byte Field5D;
        public readonly ushort Padding5E;
    }

    // size: 72
    public readonly struct FhCollisionHeader
    {
        public readonly uint PointCount;
        public readonly uint PointOffset;
        public readonly uint PlaneCount;
        public readonly uint PlaneOffset;
        public readonly uint VectorCount;
        public readonly uint VectorOffset;
        public readonly ushort DataCount;
        public readonly ushort DataStartIndex;
        public readonly uint DataOffset;
        public readonly uint DataIndexCount;
        public readonly uint DataIndexOffset;
        public readonly uint EntryCount;
        public readonly uint EntryOffset;
        public readonly uint TreeNodeIndexCount;
        public readonly uint TreeNodeIndexOffset;
        public readonly uint TreeNodeCount;
        public readonly uint TreeNodeOffset;
        public readonly uint PortalCount;
        public readonly uint PortalOffset;
    }

    // size: 6
    public readonly struct FhCollisionData
    {
        public readonly ushort PlaneIndex;
        public readonly ushort VectorCount;
        public readonly ushort VectorStartIndex;
    }

    // size: 6
    public readonly struct FhCollisionVector
    {
        public readonly ushort Point1Index;
        public readonly ushort Point2Index;
        public readonly ushort PlaneIndex;
    }

    // size: 28
    public readonly struct FhCollisionEntry
    {
        public readonly Vector3Fx MinBounds;
        public readonly Vector3Fx MaxBounds;
        public readonly ushort DataCount;
        public readonly ushort DataStartIndex;
    }

    // size: 28
    public readonly struct FhCollisionTreeNode
    {
        public readonly Vector3Fx MinBounds;
        public readonly Vector3Fx MaxBounds;
        // for rooms, a RightIndex value of 0x8000 indicates the last node in a branch, and LeftIndex is then used as an entry index;
        // for entities, RightIndex and LeftIndex are always 0x8000 as there is no hierarchy to entity collision
        public readonly ushort LeftIndex;
        public readonly ushort RightIndex;
    }

    public class FhCollisionInfo : CollisionInfo
    {
        public FhCollisionHeader Header { get; }
        public IReadOnlyList<FhCollisionData> Data { get; }
        public IReadOnlyList<FhCollisionVector> Vectors { get; }
        public IReadOnlyList<ushort> DataIndices { get; }
        public IReadOnlyList<FhCollisionEntry> Entries { get; }
        public IReadOnlyList<int> TreeNodeIndices { get; }
        public IReadOnlyList<FhCollisionTreeNode> TreeNodes { get; }

        public FhCollisionInfo(FhCollisionHeader header, IReadOnlyList<Vector3Fx> points, IReadOnlyList<Vector4Fx> planes,
            IReadOnlyList<FhCollisionData> data, IReadOnlyList<FhCollisionVector> vectors, IReadOnlyList<ushort> dataIndices,
            IReadOnlyList<Portal> portals, IReadOnlyList<FhCollisionEntry> entries, IReadOnlyList<int> treeNodeIndices,
            IReadOnlyList<FhCollisionTreeNode> treeNodes) : base(points, planes, portals, firstHunt: true)
        {
            Header = header;
            Data = data;
            Vectors = vectors;
            DataIndices = dataIndices;
            Entries = entries;
            TreeNodeIndices = treeNodeIndices;
            TreeNodes = treeNodes;
        }


    }
}
