using OpenTK.Mathematics;
using RDBExplorer.Utils;
using System.Text;
using static System.Collections.Specialized.BitVector32;

namespace RDBExplorer.Core.Formats.G1M
{
    public class G1MData
    {
        public List<G1MBoneInternal> Skeleton = new List<G1MBoneInternal>();
        public List<G1MVertexBufferInternal> VertexBuffers = new List<G1MVertexBufferInternal>();
        public List<G1MIndexBufferInternal> IndexBuffers = new List<G1MIndexBufferInternal>();
        public List<G1MLayoutInternal> Layouts = new List<G1MLayoutInternal>();
        public List<G1MSubmeshInternal> Submeshes = new List<G1MSubmeshInternal>();
        public List<List<uint>> BonePalettes = new List<List<uint>>();
        public Dictionary<int, int> LocalToGlobalBone = new Dictionary<int, int>();
        public List<List<uint>> ResolvedPalettes = new List<List<uint>>();
        public List<G1MMaterialInternal> Materials = new();
        public List<G1MMeshGroupInternal> MeshGroups = new();
        public List<List<uint>> PhysicsPalettes = new List<List<uint>>();
        public List<INunoEntry> NunoEntries = new List<INunoEntry>();
        public List<G1MGPropertySet> G1MGProperties  = new();
        public ushort[] BoneIDList;
        public bool IsStreamingMeshlet;
        public List<G1MStreamingSectionInternal> StreamingSections = new();
        public List<G1MStreamingQuantizationInternal> StreamingQuantizations = new();
        public List<G1MStreamingMeshletTriangleInternal> StreamingMeshletTriangles = new();
        public List<G1MStreamingMeshletDescriptorInternal> StreamingMeshlets = new();
        public List<G1MStreamingMeshletBoundsInternal> StreamingMeshletBounds = new();
        public List<G1MStreamingMeshletMapInternal> StreamingMeshletMap = new();


        public Vector3 PositionScale { get; private set; } = Vector3.One;
        public Vector3 PositionBias { get; private set; } = Vector3.Zero;
        public bool IsQuantized = false;

        public Dictionary<GeometrySectionType, byte[]> ExtendedGeometryData = new();

        public void Parse(BinaryReader r)
        {
            ResourceHeader mainResoureHeader = r.ReadStruct<ResourceHeader>();
            if (mainResoureHeader.Magic != 0x47314D5F)
            {
                throw new InvalidDataException($"Invalid magic: 0x{mainResoureHeader.Magic:X8}. Expected G1M_.");
            }
            uint headerOffset = r.ReadUInt32();
            r.ReadUInt32();
            uint chunkCount = r.ReadUInt32();

            r.BaseStream.Position = headerOffset;

            for (int i = 0; i < chunkCount; i++)
            {
                long start = r.BaseStream.Position;
                ResourceHeader resourceHeader = r.ReadStruct<ResourceHeader>();

                if (resourceHeader.Magic == 0x4F4E554E)
                    resourceHeader.Magic = 0x4E554E4F;

                switch (resourceHeader.Magic)
                {
                    case 0x47314D53: // SM1G
                    case 0x534D3147: // G1MS
                        // ReadSkeltonInfoSection
                        ParseG1MS(r, start);
                        break;
                    case 0x47314D47: // GM1G
                    case 0x474D3147: // G1MG
                        // ReadGeometryPallete
                        ParseG1MG(r, start, resourceHeader.Version);
                        break;
                    case 0x4E554E4F: // NUNO
                        // ReadClothInfoSection
                        ParseNuno(r, start);
                        break;
                }

                r.BaseStream.Position = start + resourceHeader.SectionSize;
            }
        }

        public struct ResourceHeader
        {
            // common resource header
            public uint Magic;
            public uint Version;
            public uint SectionSize;
        }

        // G1MS  –  Skeleton

        private void ParseG1MS(BinaryReader r, long start)
        {
            // Header layout (after magic/version/size that were already read):
            //   +0x00  jointDataOffset  (uint)
            //   +0x04  unknown          (uint)
            //   +0x08  jointCount       (ushort)
            //   +0x0A  jointIndicesCount(ushort)
            //   +0x0C  layer            (ushort)
            //   +0x0E  pad              (ushort)
            //   +0x10  boneIDList[jointIndicesCount]  (ushort each)
            r.BaseStream.Position = start + 0x0C; // skip magic(4)+version(4)+size(4)
            uint jointDataOffset = r.ReadUInt32();
            r.ReadUInt32(); // unknown
            ushort boneCount = r.ReadUInt16();
            ushort jointIndicesCount = r.ReadUInt16();
            r.ReadUInt16(); // layer
            r.ReadUInt16(); // pad

            // Read boneIDList (used to map joint index → global bone ID)
            BoneIDList = new ushort[jointIndicesCount];
            var boneToBoneID = new Dictionary<int, int>(); // boneID → list index
            for (int i = 0; i < jointIndicesCount; i++)
            {
                BoneIDList[i] = r.ReadUInt16();
                if (BoneIDList[i] != 0xFFFF)
                    boneToBoneID[BoneIDList[i]] = i;
            }

            // Read joint data
            r.BaseStream.Position = start + jointDataOffset;
            for (int i = 0; i < boneCount; i++)
            {
                // scale  (3 × float  = 12 bytes)
                float sx = r.ReadSingle();
                float sy = r.ReadSingle();
                float sz = r.ReadSingle();

                // parentID  (int  = 4 bytes)
                int parentIndex = r.ReadInt32();

                // rotation quaternion stored as x,y,z,w  (4 × float = 16 bytes)
                float qx = r.ReadSingle();
                float qy = r.ReadSingle();
                float qz = r.ReadSingle();
                float qw = r.ReadSingle();

                // position x,y,z,w  (4 × float = 16 bytes); w is padding
                float px = r.ReadSingle();
                float py = r.ReadSingle();
                float pz = r.ReadSingle();
                r.ReadSingle(); // w – padding, ignored

                // OpenTK Quaternion constructor is (x, y, z, w)
                Skeleton.Add(new G1MBoneInternal
                {
                    Name = $"Bone_{i}",
                    Scale = new Vector3(sx, sy, sz),
                    ParentIndex = parentIndex,
                    Rotation = new Quaternion(qx, qy, qz, qw),
                    Position = new Vector3(px, py, pz)
                });
            }
        }

        // G1MG  –  Geometry
        public class G1MGHeader
        {
            // chunk header
            public string Platform { get; set; }
            public uint Reverserd { get; set; }

            // bounds
            public Vector3 Min { get; set; }
            public Vector3 Max { get; set; }

            public uint SectionCount { get; set; }
        };

        public struct GeometrySection
        {
            public uint Magic { get; set; }
            public GeometrySectionType Type { get; set; }
            public ushort Version { get; set; }
            public uint Size { get; set; }
            public uint Count { get; set; }
        }

        public enum GeometrySectionType : ushort
        {
            Section1 = 1,
            Materials = 2,
            PropertySetPallete = 3,
            VertexBuffer = 4,
            VertexStreamSetPallete = 5,
            JointPalettes = 6,
            IndexStreamPallete = 7,
            Submesh = 8,
            Mesh = 9,
            NunoSimulationVertices = 10,
            PhysicsVertexIndices = 11,
            PhysicsConstraints = 12,
            SpringData = 13,
            CollisionDistance = 14,
            PhysicsBoneMap = 15,
            WindInfluence = 16,
            SoftBodyShapes = 17,
            SecondaryPhysicsMap = 18,
            ExtendedPhysics = 19,
            DirectionalVectors = 20,
            SimpleFlags = 21,
            DoubleIndices = 22
        }

        private void ParseG1MG(BinaryReader r, long start, uint version)
        {
            G1MGHeader g1MGHeader = new G1MGHeader();
            
            g1MGHeader.Platform = r.ReadEncodedString(4);
            g1MGHeader.Reverserd = r.ReadUInt32();

            g1MGHeader.Min = r.ReadStruct<Vector3>();
            g1MGHeader.Max = r.ReadStruct<Vector3>();

            uint sectionCount = r.ReadUInt32();
            g1MGHeader.SectionCount = sectionCount;

            long sectionsStart = r.BaseStream.Position;
            bool hasStreamingMeshletSections = HasStreamingMeshletSections(r, sectionsStart, sectionCount);
            r.BaseStream.Position = sectionsStart;

            for (int i = 0; i < sectionCount; i++)
            {
                long secStart = r.BaseStream.Position;
                GeometrySection geometrySection = ReadGeometrySection(r);

                if (hasStreamingMeshletSections && IsStreamingMeshletSectionMagic(geometrySection.Magic))
                {
                    ParseStreamingMeshletSection(r, geometrySection);
                    r.BaseStream.Position = secStart + geometrySection.Size;
                    continue;
                }

                switch (geometrySection.Type)
                {
                    case GeometrySectionType.Section1:
                        ParseSection1(r, geometrySection.Count);
                        break;
                    case GeometrySectionType.Materials: 
                        ParseMaterials(r, geometrySection.Count);
                        break;
                    case GeometrySectionType.PropertySetPallete:
                        ParsePropertySetPallete(r, geometrySection.Count);
                        break;
                    case GeometrySectionType.VertexBuffer: 
                        ParseVertexBuffers(r, geometrySection.Count, version);
                        break;
                    case GeometrySectionType.VertexStreamSetPallete: 
                        ParseVertexAttributes(r, geometrySection.Count);
                        break;
                    case GeometrySectionType.JointPalettes: 
                        ParseJointPalettes(r, geometrySection.Count); 
                        break;
                    case GeometrySectionType.IndexStreamPallete: 
                        ParseIndexBuffers(r, geometrySection.Count, version); 
                        break;
                    case GeometrySectionType.Submesh: 
                        ParseSubmeshes(r, geometrySection.Count);
                        break;
                    case GeometrySectionType.Mesh: 
                        ParseMeshGroups(r, geometrySection.Count, version);
                        break;
                    case GeometrySectionType.NunoSimulationVertices:
                        ParseSection10(r, geometrySection.Count);
                        break;
                    case GeometrySectionType.PhysicsVertexIndices:
                        IndexBuffers.Clear();
                        ParseSection11(r, geometrySection.Count);
                        break;
                    case GeometrySectionType.PhysicsConstraints:
                        ParseSection12(r, geometrySection.Count);
                        break;
                    case GeometrySectionType.SpringData:
                        ParseSection13(r, geometrySection.Count);
                        break;
                    case GeometrySectionType.CollisionDistance:
                        ParseSection14(r, geometrySection.Count);
                        break;
                    case GeometrySectionType.PhysicsBoneMap:
                        ParseSection15(r, geometrySection.Count); // flags maybe, same count element as 21
                        break;
                    case GeometrySectionType.WindInfluence:
                        ParseSection16(r, geometrySection.Count);
                        break;
                    case GeometrySectionType.SoftBodyShapes:
                        ParseSection17(r, geometrySection.Count);
                        break;
                    case GeometrySectionType.SecondaryPhysicsMap:
                        ParseSection18(r, geometrySection.Count); // same count element as 22
                        break;
                    case GeometrySectionType.ExtendedPhysics:
                        ParseSection19(r, geometrySection.Count);
                        break;
                    case GeometrySectionType.DirectionalVectors:
                        ParseSection20(r, geometrySection.Count);
                        break;
                    case GeometrySectionType.SimpleFlags:
                        ParseSection21(r, geometrySection.Count); // flags maybe, same count element as 15
                        break;
                    case GeometrySectionType.DoubleIndices:
                        ParseSection22(r, geometrySection.Count);  // same count element as 18
                        break;

                    default:
                        Console.WriteLine($"unhandle geometry section type: {((ushort)geometrySection.Type)}");
                        break;
                }

                r.BaseStream.Position = secStart + geometrySection.Size;
            }
        }

        private GeometrySection ReadGeometrySection(BinaryReader r)
        {
            uint magic = r.ReadUInt32();
            return new GeometrySection
            {
                Magic = magic,
                Version = (ushort)(magic >> 16),
                Type = (GeometrySectionType)(magic & 0xFFFF),
                Size = r.ReadUInt32(),
                Count = r.ReadUInt32()
            };
        }

        private bool HasStreamingMeshletSections(BinaryReader r, long sectionsStart, uint sectionCount)
        {
            long oldPos = r.BaseStream.Position;
            bool hasTriangles = false;
            bool hasDescriptors = false;
            bool hasMap = false;

            try
            {
                r.BaseStream.Position = sectionsStart;
                for (int i = 0; i < sectionCount; i++)
                {
                    long secStart = r.BaseStream.Position;
                    if (secStart + 20 > r.BaseStream.Length)
                        break;

                    uint magic = r.ReadUInt32();
                    uint size = r.ReadUInt32();
                    r.ReadUInt32(); // header count

                    if (size < 20 || secStart + size > r.BaseStream.Length)
                        break;

                    uint tableCount = r.ReadUInt32();
                    uint stride = r.ReadUInt32();
                    uint payloadSize = size - 12;
                    bool tableFits = payloadSize >= 8 && (ulong)tableCount * stride <= payloadSize - 8;

                    if (tableFits)
                    {
                        if (magic == 0x0001000B && stride == 4)
                            hasTriangles = true;
                        else if (magic == 0x0001000C && stride == 16)
                            hasDescriptors = true;
                        else if (magic == 0x00010010 && stride == 12)
                            hasMap = true;
                    }

                    r.BaseStream.Position = secStart + size;
                }
            }
            finally
            {
                r.BaseStream.Position = oldPos;
            }

            return hasTriangles && hasDescriptors && hasMap;
        }

        private static bool IsStreamingMeshletSectionMagic(uint magic)
        {
            return magic >= 0x0001000A && magic <= 0x00010016;
        }

        private void ParseStreamingMeshletSection(BinaryReader r, GeometrySection section)
        {
            IsStreamingMeshlet = true;

            uint payloadSize = section.Size >= 12 ? section.Size - 12 : 0;
            if (payloadSize < 8)
                return;

            uint tableCount = r.ReadUInt32();
            uint stride = r.ReadUInt32();
            long dataStart = r.BaseStream.Position;

            StreamingSections.Add(new G1MStreamingSectionInternal
            {
                Magic = section.Magic,
                Size = section.Size,
                HeaderCount = section.Count,
                TableCount = tableCount,
                Stride = stride
            });

            switch (section.Magic)
            {
                case 0x0001000A when stride >= 44:
                    ParseStreamingQuantization(r, tableCount, stride, dataStart);
                    break;
                case 0x0001000B when stride == 4:
                    ParseStreamingMeshletTriangles(r, tableCount);
                    break;
                case 0x0001000C when stride == 16:
                    ParseStreamingMeshletDescriptors(r, tableCount, stride, dataStart);
                    break;
                case 0x0001000D when stride == 60:
                    ParseStreamingMeshletBounds(r, tableCount, stride, dataStart);
                    break;
                case 0x00010010 when stride == 12:
                    ParseStreamingMeshletMap(r, tableCount, stride, dataStart);
                    break;
            }
        }

        private void ParseStreamingQuantization(BinaryReader r, uint tableCount, uint stride, long dataStart)
        {
            for (uint i = 0; i < tableCount; i++)
            {
                r.BaseStream.Position = dataStart + i * stride;
                var center = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                float radius = r.ReadSingle();
                var bboxMin = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                var bboxMax = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                float unknownFloat = r.ReadSingle();

                StreamingQuantizations.Add(new G1MStreamingQuantizationInternal
                {
                    ID = i,
                    Center = center,
                    Radius = radius,
                    BoundingBoxMin = bboxMin,
                    BoundingBoxMax = bboxMax,
                    UnknownFloat = unknownFloat
                });
            }
        }

        private void ParseStreamingMeshletTriangles(BinaryReader r, uint tableCount)
        {
            for (uint i = 0; i < tableCount; i++)
            {
                uint value = r.ReadUInt32();
                StreamingMeshletTriangles.Add(new G1MStreamingMeshletTriangleInternal
                {
                    A = (ushort)(value & 0x3FF),
                    B = (ushort)((value >> 10) & 0x3FF),
                    C = (ushort)((value >> 20) & 0x3FF)
                });
            }
        }

        private void ParseStreamingMeshletDescriptors(BinaryReader r, uint tableCount, uint stride, long dataStart)
        {
            for (uint i = 0; i < tableCount; i++)
            {
                r.BaseStream.Position = dataStart + i * stride;
                StreamingMeshlets.Add(new G1MStreamingMeshletDescriptorInternal
                {
                    ID = i,
                    TriangleOffset = r.ReadUInt32(),
                    TriangleCount = r.ReadUInt32(),
                    IndexOffset = r.ReadUInt32(),
                    IndexCount = r.ReadUInt32()
                });
            }
        }

        private void ParseStreamingMeshletBounds(BinaryReader r, uint tableCount, uint stride, long dataStart)
        {
            for (uint i = 0; i < tableCount; i++)
            {
                r.BaseStream.Position = dataStart + i * stride;
                float[] values = new float[15];
                for (int j = 0; j < values.Length; j++)
                    values[j] = r.ReadSingle();

                StreamingMeshletBounds.Add(new G1MStreamingMeshletBoundsInternal
                {
                    ID = i,
                    Values = values,
                    BoundsMinCandidate = new Vector3(values[9], values[10], values[11]),
                    BoundsMaxCandidate = new Vector3(values[12], values[13], values[14])
                });
            }
        }

        private void ParseStreamingMeshletMap(BinaryReader r, uint tableCount, uint stride, long dataStart)
        {
            for (uint i = 0; i < tableCount; i++)
            {
                r.BaseStream.Position = dataStart + i * stride;
                uint meshletIndex = r.ReadUInt32();
                uint compactIndex = r.ReadUInt32();
                uint packedSubmesh = r.ReadUInt32();

                StreamingMeshletMap.Add(new G1MStreamingMeshletMapInternal
                {
                    ID = i,
                    MeshletIndex = meshletIndex,
                    CompactIndex = compactIndex,
                    PackedSubmesh = packedSubmesh,
                    SubmeshIndex = (ushort)(packedSubmesh >> 16),
                    RemapPage = (ushort)(packedSubmesh & 0xFFFF)
                });
            }
        }

        private void ParseSection1(BinaryReader r, uint countFromHeader)
        {
            //for (int j = 0; j < countFromHeader; j++)
            {
                long count = r.ReadInt64();
                for (int i = 0; i < count; i++)
                {
                    byte[] data = r.ReadBytes(64);
                }
            }
        }

        private void ParseMaterials(BinaryReader r, uint count)
        {
            for (int j = 0; j < (int)count; j++)
            {
                r.ReadUInt32(); // unk1
                uint texCount = r.ReadUInt32();
                r.ReadUInt32(); // unk
                r.ReadUInt32(); // unk

                var mat = new G1MMaterialInternal();
                for (int k = 0; k < (int)texCount; k++)
                {
                    mat.Textures.Add(new G1MTextureRef
                    {
                        Index = r.ReadUInt16(),
                        Layer = r.ReadUInt16(),
                        TextureType = r.ReadUInt16(),
                    });
                    r.ReadUInt16(); // otherType
                    r.ReadUInt16(); // tileX
                    r.ReadUInt16(); // tileY
                }
                Materials.Add(mat);
            }
        }

        private void ParsePropertySetPallete(BinaryReader r, uint count)
        {
            for (int i = 0; i < (int)count; i++)
            {
                var propertySet = new G1MGPropertySet();
                uint propsInSet = r.ReadUInt32();

                for (int j = 0; j < (int)propsInSet; j++)
                {
                    long propStartPos = r.BaseStream.Position;

                    uint totalSize = r.ReadUInt32();
                    uint nameLength = r.ReadUInt32();
                    ushort propType = r.ReadUInt16();
                    ushort unkFlag1 = r.ReadUInt16();
                    ushort unkFlag2 = r.ReadUInt16();
                    ushort unkFlag3 = r.ReadUInt16();
 
                    var prop = new G1MGProperty {
                        Type = propType,
                        UnkFlag1 = unkFlag1,
                        UnkFlag2 = unkFlag2,
                        UnkFlag3 = unkFlag3,
                    };

                    if (nameLength > 0)
                    {
                        prop.Name = r.ReadEncodedString((int)nameLength);
                    }

                    int dataSize = (int)(totalSize - 16 - nameLength);
                    if (dataSize > 0)
                    {
                        prop.Data = r.ReadBytes(dataSize);
                    }

                    propertySet.Properties.Add(prop);
                }

                G1MGProperties.Add(propertySet);
            }
        }

        // 0x00010004  –  Vertex Buffers  (segmented)

        private void ParseVertexBuffers(BinaryReader r, uint count, uint version)
        {
            int total = 0;

            while (total < (int)count)
            {
                // Each entry: unknown1(4) + stride(4) + vCount(4) [+ extra(4) if version > 0x30303430]
                r.ReadUInt32(); // unknown1  (NOT a flag – any value is valid here)
                int stride = r.ReadInt32();
                int vCount = r.ReadInt32();
                if (version > 0x30303430)
                    r.ReadUInt32();

                byte[] physData;
                int physStride;

                if (stride == 1)
                {
                    // This buffer is a raw repository; actual data for sub-buffers lives here.
                    physStride = 1;
                    physData = r.ReadBytes(vCount);
                }
                else
                {
                    physStride = stride;
                    physData = r.ReadBytes(stride * vCount);
                }

                VertexBuffers.Add(new G1MVertexBufferInternal(physData, physStride));
                total++;

                // Peek for segmented sub-buffers flagged by unknown1 == 0x80000000
                int accOffset = 0;
                while (r.BaseStream.Position + 4 <= r.BaseStream.Length)
                {
                    long peekPos = r.BaseStream.Position;
                    uint flag = r.ReadUInt32();
                    if (flag != 0x80000000)
                    {
                        r.BaseStream.Position = peekPos; // put it back
                        break;
                    }

                    int subStride = r.ReadInt32();
                    int subCount = r.ReadInt32();
                    if (version > 0x30303430)
                        r.ReadUInt32();

                    int subBytes = subStride * subCount;
                    byte[] sub = new byte[subBytes];
                    int avail = physData.Length - accOffset;
                    Array.Copy(physData, accOffset, sub, 0, Math.Min(subBytes, avail));
                    accOffset += subBytes;

                    VertexBuffers.Add(new G1MVertexBufferInternal(sub, subStride));
                    total++;
                }
            }
        }


        // 0x00010005  –  Vertex Attributes (Layouts)

        private void ParseVertexAttributes(BinaryReader r, uint count)
        {
            for (int j = 0; j < (int)count; j++)
            {
                var layout = new G1MLayoutInternal();

                // Buffer reference list (indirect addressing)
                uint numRefs = r.ReadUInt32();
                for (int k = 0; k < (int)numRefs; k++)
                {
                    layout.BufferIndices.Add(r.ReadUInt32());
                }

                // Semantic descriptors – each is exactly 8 bytes:
                //    bufferID (ushort=2) | offset (ushort=2) | dataType (byte=1) | dummy (byte=1) | semantic (byte=1) | layer (byte=1)
                uint numSemantics = r.ReadUInt32();
                for (int k = 0; k < (int)numSemantics; k++)
                {
                    ushort bufIdx = r.ReadUInt16(); // which buffer in BufferIndices
                    ushort offset = r.ReadUInt16(); // byte offset within that buffer's stride
                    EG1MGVADatatype dataType = (EG1MGVADatatype)r.ReadUInt16();   //
                    G1MSemanticType semantic = (G1MSemanticType)r.ReadByte();   // semantic type enum
                    byte layer = r.ReadByte();   // semantic index / layer

                    layout.Semantics.Add(new G1MSemanticInternal
                    {
                        BufIdx = bufIdx,
                        Offset = offset,
                        Format = dataType,
                        Type = semantic,
                        Layer = layer
                    });
                }

                Layouts.Add(layout);
            }
        }


        // 0x00010006  –  Joint Palettes

        private void ParseJointPalettes(BinaryReader r, uint count)
        {
            for (int i = 0; i < count; i++)
            {
                uint pCount = r.ReadUInt32();
                var palette = new List<uint>();
                var physPalette = new List<uint>();
                for (int j = 0; j < pCount; j++)
                {
                    r.ReadUInt32(); // G1MM index
                    uint physIdx = r.ReadUInt32(); // read physicsIndex
                    uint jointIdx = r.ReadUInt32();

                    uint actualIdx = jointIdx;
                    if ((jointIdx & 0x80000000) != 0)
                        actualIdx ^= 0x80000000;

                    if (LocalToGlobalBone.TryGetValue((int)actualIdx, out int globalID))
                        palette.Add((uint)globalID);
                    else
                        palette.Add(actualIdx);

                    physPalette.Add(physIdx & 0xFFFF);
                }
                BonePalettes.Add(palette);
                PhysicsPalettes.Add(physPalette);
            }
        }

        // 0x00010007  –  Index Buffers

        private void ParseIndexBuffers(BinaryReader r, uint count, uint version)
        {
            for (int j = 0; j < (int)count; j++)
            {
                uint iCount = r.ReadUInt32();
                uint iType = r.ReadUInt32(); // bit-width: 16 → 2 bytes, 32 → 4 bytes
                if (version > 0x30303430)
                    r.ReadUInt32();

                int byteWidth = (int)(iType / 8); // 16/8=2, 32/8=4
                if (byteWidth < 1)
                    byteWidth = 2;  // safety fallback

                byte[] data = r.ReadBytes((int)iCount * byteWidth);
                IndexBuffers.Add(new G1MIndexBufferInternal(data, byteWidth));

                // Align to 4 bytes
                if (r.BaseStream.Position % 4 != 0)
                    r.BaseStream.Position += 4 - (r.BaseStream.Position % 4);
            }
        }


        // 0x00010008  –  Submeshes

        private void ParseSubmeshes(BinaryReader r, uint count)
        {
            for (int i = 0; i < count; i++)
            {
                r.ReadUInt32(); // flags
                int vbIdx = r.ReadInt32();
                int palIdx = r.ReadInt32();
                r.ReadUInt32();
                r.ReadUInt32();
                r.ReadUInt32(); // unks
                int matIdx = r.ReadInt32();
                int ibIdx = r.ReadInt32();
                r.ReadUInt32(); // unk
                uint prim = r.ReadUInt32();
                uint vbStart = r.ReadUInt32();
                uint vCount = r.ReadUInt32();
                uint ibStart = r.ReadUInt32();
                uint iCount = r.ReadUInt32();

                Submeshes.Add(new G1MSubmeshInternal
                {
                    ID = i,
                    VBRef = vbIdx,
                    BoneMapIndex = palIdx,
                    MaterialIndex = matIdx,
                    IBRef = ibIdx,
                    VBStart = vbStart,
                    VertexCount = vCount,
                    IBStart = ibStart,
                    IndexCount = iCount,
                    PrimType = prim
                });
            }
        }

        // 0x00010009  –  Meshes
        private void ParseMeshGroups(BinaryReader r, uint count, uint version)
        {
            for (int j = 0; j < (int)count; j++)
            {
                var group = new G1MMeshGroupInternal();

                if (version > 0x30303330)
                {
                    group.LOD = r.ReadUInt32();
                    group.Group = r.ReadUInt32();
                    r.ReadUInt32(); // GroupEntryIndex
                    uint sm1 = r.ReadUInt32(); // submeshCount1 (type 53)
                    uint sm2 = r.ReadUInt32(); // submeshCount2 (type 61)

                    if (version > 0x30303430)
                    {
                        r.ReadUInt32(); // lodRangeStart
                        r.ReadUInt32(); // lodRangeLength
                        r.ReadUInt32(); r.ReadUInt32(); // padding
                    }

                    for (int k = 0; k < (int)(sm1 + sm2); k++)
                    {
                        group.Meshes.Add(ReadMesh(r));
                    }
                }
                else
                {
                    group.LOD = r.ReadUInt32();
                    uint sm1 = r.ReadUInt32();
                    uint sm2 = r.ReadUInt32();
                    for (int k = 0; k < (int)(sm1 + sm2); k++)
                    {
                        group.Meshes.Add(ReadMesh(r));
                    }
                }

                MeshGroups.Add(group);
            }
        }

        private G1MMeshInternal ReadMesh(BinaryReader r)
        {
            var mesh = new G1MMeshInternal();
            string name = r.ReadEncodedString(16);
            //  Console.WriteLine(name);
            mesh.ClothID = r.ReadUInt16();
            r.ReadUInt16();                       // unk
            mesh.ExternalID = r.ReadUInt32();
            uint idxCount = r.ReadUInt32();

            if (idxCount > 0)
            {
                for (int i = 0; i < (int)idxCount; i++)
                {
                    mesh.SubmeshIndices.Add(r.ReadUInt32());
                }
            }
            else
            {
                r.ReadUInt32();
            }

            return mesh;
        }

        public struct CommonSubSection
        {
            public uint Count {  get; set; }
            public uint Stride { get; set; }
        };

        // might be quanted geometry
        private void ParseSection10(BinaryReader r, uint sectionCount)
        {
            for (int i = 0; i < sectionCount; i++)
            {
                CommonSubSection section = r.ReadStruct<CommonSubSection>();
                Console.WriteLine($"Section11, count: {section.Count}, stride: {section.Stride}");
                for (int j = 0; j < section.Count; j++)
                {
                    r.ReadUInt32(); // unk1
                    Vector3 unk2 = r.ReadStruct<Vector3>();
                    Vector3 minBounds = r.ReadStruct<Vector3>();
                    Vector3 maxBounds = r.ReadStruct<Vector3>();
                    r.ReadUInt32(); // unk5

                    // Формула для UShort_x4:
                    // Scale = (Max - Min) / 65535.0
                    // Bias = Min
                    this.PositionScale = (maxBounds - minBounds);
                    this.PositionBias = minBounds;

                    this.IsQuantized = true;

                    Console.WriteLine($"[G1M Quantization] Min={minBounds}, Max={maxBounds}");
                    Console.WriteLine($"[G1M Quantization] Scale={PositionScale}, Bias={PositionBias}");
                }
            }
        }

        private void ParseSection11(BinaryReader r, uint sectionCount)
        {
            for (int i = 0; i < sectionCount; i++)
            {
                CommonSubSection section = r.ReadStruct<CommonSubSection>();

                Console.WriteLine($"Section11, count: {section.Count}, stride: {section.Stride}");

                byte[] data = r.ReadBytes((int)((int)section.Stride * section.Count));
                //VertexBuffers.Add(new G1MVertexBufferInternal(data, (int)section.Stride));
                IndexBuffers.Add(new G1MIndexBufferInternal(data, (int)section.Stride));
                /*Console.WriteLine($"Section11, count: {section.Count}, stride: {section.Stride}");
                List<byte[]> bytes = new List<byte[]>();
                for (int j = 0; j < section.Count; j++)
                {
                    byte[] data = r.ReadBytes((int)section.Stride);
                    bytes.Add(data);
                }*/
            }
        }
        private void ParseSection12(BinaryReader r, uint sectionCount)
        {
            for (int i = 0; i < sectionCount; i++)
            {
                CommonSubSection section = r.ReadStruct<CommonSubSection>();
                Console.WriteLine($"Section12, count: {section.Count}, stride: {section.Stride}");
                List<byte[]> bytes = new List<byte[]>();
                for (int j = 0; j < section.Count; j++)
                {
                    byte[] data = r.ReadBytes((int)section.Stride);
                    bytes.Add(data);
                }
            }
        }
        

        // moight be geometry
        private void ParseSection13(BinaryReader r, uint sectionCount)
        {
            for (int i = 0; i < sectionCount; i++)
            {
                CommonSubSection section = r.ReadStruct<CommonSubSection>();
                Console.WriteLine($"Section13, count: {section.Count}, stride: {section.Stride}");
                List<byte[]> bytes = new List<byte[]>();
                for (int j = 0; j < section.Count; j++)
                {
                    byte[] data = r.ReadBytes((int)section.Stride);
                    bytes.Add(data);
                }
            }
        }

        private void ParseSection14(BinaryReader r, uint sectionCount)
        {
            for (int i = 0; i < sectionCount; i++)
            {
                CommonSubSection section = r.ReadStruct<CommonSubSection>();
                Console.WriteLine($"Section14, count: {section.Count}, stride: {section.Stride}");
                List<byte[]> bytes = new List<byte[]>();
                for (int j = 0; j < section.Count; j++)
                {
                    byte[] data = r.ReadBytes((int)section.Stride);
                    bytes.Add(data);
                }
            }
        }

        private void ParseSection15(BinaryReader r, uint sectionCount)
        {
            for (int i = 0; i < sectionCount; i++)
            {
                uint count = r.ReadUInt32();
                Console.WriteLine($"Section15, count: {count}");
                List<uint> list = new List<uint>();
                for (int j = 0; j < count; j++)
                {
                    uint flag = r.ReadUInt32();
                    list.Add(flag);
                }
            }
        }

        // indexes
        private void ParseSection16(BinaryReader r, uint sectionCount)
        {
            for (int i = 0; i < sectionCount; i++)
            {
                CommonSubSection section = r.ReadStruct<CommonSubSection>();
                Console.WriteLine($"Section16, count: {section.Count}, stride: {section.Stride}");
                List<byte[]> bytes = new List<byte[]>();
                for (int j = 0; j < section.Count; j++)
                {
                    byte[] data = r.ReadBytes((int)section.Stride);
                    bytes.Add(data);
                }
            }
        }

        private void ParseSection17(BinaryReader r, uint sectionCount)
        {
            for (int i = 0; i < sectionCount; i++)
            {
                CommonSubSection section = r.ReadStruct<CommonSubSection>();
                Console.WriteLine($"Section17, count: {section.Count}, stride: {section.Stride}");
                List<byte[]> bytes = new List<byte[]>();
                for (int j = 0; j < section.Count; j++)
                {
                    byte[] data = r.ReadBytes((int)section.Stride);
                    bytes.Add(data);
                }
            }
        }
        private void ParseSection18(BinaryReader r, uint sectionCount)
        {
            for (int i = 0; i < sectionCount; i++)
            {
                CommonSubSection section = r.ReadStruct<CommonSubSection>();
                Console.WriteLine($"Section18, count: {section.Count}, stride: {section.Stride}");
                List<byte[]> bytes = new List<byte[]>();
                for (int j = 0; j < section.Count; j++)
                {
                    byte[] data = r.ReadBytes((int)section.Stride);
                    bytes.Add(data);
                }
            }
        }

        private void ParseSection19(BinaryReader r, uint sectionCount)
        {
            for (int i = 0; i < sectionCount; i++)
            {
                CommonSubSection section = r.ReadStruct<CommonSubSection>();
                Console.WriteLine($"Section19, count: {section.Count}, stride: {section.Stride}");
                List<byte[]> bytes = new List<byte[]>();
                for (int j = 0; j < section.Count; j++)
                {
                    byte[] data = r.ReadBytes((int)section.Stride);
                    bytes.Add(data);
                }

            }
        }

        public struct Section20
        {
            public uint Index; 
            public uint Unk1;
            public uint Unk2;
        }

        public struct Section22
        {
            public uint Unk1;
            public uint Unk2;
        }

        // indexes
        private void ParseSection20(BinaryReader r, uint sectionCount)
        {
            for (int i = 0; i < sectionCount; i++)
            {
                uint count = r.ReadUInt32();
                Console.WriteLine($"Section20, count: {count}");
                List<Section20> list = new List<Section20>();
                for (int j = 0; j < count; j++)
                {
                    Section20 section = r.ReadStruct<Section20>();
                    list.Add(section);
                }
            }
        }
        private void ParseSection21(BinaryReader r, uint sectionCount)
        {
            for (int i = 0; i < sectionCount; i++)
            {
                uint count = r.ReadUInt32();
                Console.WriteLine($"Section21, count: {count}");
                List<uint> list = new List<uint>();
                for (int j = 0; j < count; j++)
                {
                    uint flag = r.ReadUInt32();
                    list.Add(flag);
                }
            }

        }
        private void ParseSection22(BinaryReader r , uint sectionCount)
        {
            for (int i = 0; i < sectionCount; i++)
            {
                uint count = r.ReadUInt32();
                Console.WriteLine($"Section22, count: {count}");
                List<Section22> list = new List<Section22>();
                for (int j = 0; j < count; j++)
                {
                    Section22 section = r.ReadStruct<Section22>();
                    list.Add(section);
                }
            }

        }

        private void ParseNuno(BinaryReader r, long start)
        {
            r.BaseStream.Position = start + 4; // skip 'NUNO'
            uint version = r.ReadUInt32();
            r.ReadUInt32(); // size
            uint sectionCount = r.ReadUInt32();

            for (int i = 0; i < sectionCount; i++)
            {
                long sectionStart = r.BaseStream.Position;
                uint magic = r.ReadUInt32();
                uint chunkSize = r.ReadUInt32();
                uint entryCount = r.ReadUInt32();

                var entryIDToNunoID = new Dictionary<uint, int>();
                var tempEntries = new List<INunoEntry>();

                long currentEntryOffset = r.BaseStream.Position;
                if (version >= 0x30303335 && magic == 0x00030005)
                {
                    currentEntryOffset += 4;
                }

                for (int j = 0; j < entryCount; j++)
                {
                    r.BaseStream.Position = currentEntryOffset;
                    long entrySize = 0;
                    INunoEntry entry = null;

                    switch (magic)
                    {
                        case 0x00030001: 
                            entry = ParseNuno1Entry(r, version, out entrySize);
                            break;
                        case 0x00030003: 
                            entry = ParseNuno3Entry(r, version, out entrySize);
                            break;
                        case 0x00030005: 
                            entry = ParseNuno5Entry(r, version, entryIDToNunoID, out entrySize);
                            break;
                    }

                    if (entry != null)
                    {
                        if (entry is Nuno5Data n5)
                        {
                            if (!entryIDToNunoID.ContainsKey(n5.EntryID))
                            {
                                entryIDToNunoID[n5.EntryID] = j;
                            }
                        }
                        tempEntries.Add(entry);
                    }
                    currentEntryOffset += entrySize;
                }

                // Subset processing for NUNO5
                if (magic == 0x00030005)
                {
                    foreach (var entry in tempEntries)
                    {
                        if (entry is Nuno5Data n5 && n5.ParentSetID != -1)
                        {
                            var parentNuno = tempEntries[n5.ParentSetID] as Nuno5Data;
                            if (parentNuno != null)
                            {
                                var parentMap = new Dictionary<float, int>();
                                for (int k = 0; k < parentNuno.ControlPoints.Count; k++)
                                {
                                    var cp = parentNuno.ControlPoints[k];
                                    parentMap[cp.X + cp.Y + cp.Z] = k; // use sum as key
                                }

                                for (int k = 0; k < n5.ControlPoints.Count; k++)
                                {
                                    var cp = n5.ControlPoints[k];
                                    if (parentMap.TryGetValue(cp.X + cp.Y + cp.Z, out int parentIndex))
                                    {
                                        var infl = n5.Influences[k];
                                        infl.P1 = parentIndex;
                                        n5.Influences[k] = infl;
                                    }
                                }
                            }
                        }
                    }
                }

                NunoEntries.AddRange(tempEntries);
                r.BaseStream.Position = sectionStart + chunkSize;
            }
        }

        private INunoEntry ParseNuno1Entry(BinaryReader r, uint version, out long entrySize)
        {
            long entryStart = r.BaseStream.Position;
            var nuno1 = new Nuno1Data();
            nuno1.ParentID = r.ReadUInt32();
            uint cpCount = r.ReadUInt32();
            uint unknownSectionCount = r.ReadUInt32();
            uint skip1 = r.ReadUInt32();
            uint skip2 = r.ReadUInt32();
            uint skip3 = r.ReadUInt32();

            long dataOffset = entryStart + 24 + 0x3C;
            if (version > 0x30303233)
            {
                dataOffset += 0x10;
            }
            if (version >= 0x30303235)
            {
                dataOffset += 0x10;
            }
            r.BaseStream.Position = dataOffset;

            for (int k = 0; k < cpCount; k++)
            {
                nuno1.ControlPoints.Add(new Vector4(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()));
            }
            for (int k = 0; k < cpCount; k++)
            {
                nuno1.Influences.Add(r.ReadStruct<NunInfluence>());
            }

            entrySize = (dataOffset + (cpCount * 16) + (cpCount * 24) + (48 * unknownSectionCount) + (4 * (skip1 + skip2 + skip3))) - entryStart;
            return nuno1;
        }

        private INunoEntry ParseNuno3Entry(BinaryReader r, uint version, out long entrySize)
        {
            long entryStart = r.BaseStream.Position;
            var nuno3 = new Nuno3Data();
            nuno3.ParentID = r.ReadUInt32();
            uint cpCount = r.ReadUInt32();
            uint unknownSectionCount = r.ReadUInt32();
            uint skip1 = r.ReadUInt32();
            r.ReadUInt32(); // unk
            uint skip2 = r.ReadUInt32();
            uint skip3 = r.ReadUInt32();
            uint skip4 = r.ReadUInt32();

            long dataOffset = entryStart + 32;
            if (version < 0x30303330)
            {
                dataOffset += 0xA8;
                if (version >= 0x30303235)
                    dataOffset += 0x10;
            }
            else
            {
                r.BaseStream.Position = dataOffset;
                uint temp = r.ReadUInt32();
                dataOffset += 4 + temp;
            }
            r.BaseStream.Position = dataOffset;

            for (int k = 0; k < cpCount; k++)
            {
                nuno3.ControlPoints.Add(new Vector4(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()));
            }
            for (int k = 0; k < cpCount; k++)
            {
                nuno3.Influences.Add(r.ReadStruct<NunInfluence>());
            }

            entrySize = (r.BaseStream.Position + (48 * unknownSectionCount) + (4 * skip1 + 8 * skip2 + 12 * skip3 + 8 * skip4)) - entryStart;
            return nuno3;
        }

        private INunoEntry ParseNuno5Entry(BinaryReader r, uint version, Dictionary<uint, int> entryIDToNunoID, out long entrySize)
        {
            long entryStart = r.BaseStream.Position;
            var nuno5 = new Nuno5Data();
            nuno5.ParentID = r.ReadUInt32();
            r.ReadUInt32(); // unk
            uint lodCount = r.ReadUInt32();
            nuno5.EntryID = r.ReadUInt16();
            ushort entryFlag = r.ReadUInt16();

            if ((entryFlag & 0x7FF) != 0 && entryIDToNunoID.TryGetValue(nuno5.EntryID, out int parentId))
            {
                nuno5.ParentSetID = parentId;
            }

            r.BaseStream.Position = entryStart + 0x24;

            for (int l = 0; l < lodCount; l++)
            {
                long lodStart = r.BaseStream.Position;
                uint cpCount = r.ReadUInt32();
                uint flags = r.ReadUInt32();
                uint[] skips = new uint[9];
                for (int s = 0; s < 9; s++)
                {
                    skips[s] = r.ReadUInt32();
                }

                bool useSkip10 = r.ReadUInt32() != 0;
                uint skip10Size = 0, skip10Count = 0;
                if (useSkip10) { 
                    skip10Size = r.ReadUInt32();
                    skip10Count = r.ReadUInt32(); 
                }

                long currentOffset = r.BaseStream.Position;
                uint cpOffset = r.ReadUInt32();
                r.BaseStream.Position = lodStart + 48 + (useSkip10 ? 8 : 0) + cpOffset;
                {
                    for (int k = 0; k < cpCount; k++)
                    {
                        nuno5.ControlPoints.Add(new Vector4(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), 1.0f));
                        r.BaseStream.Seek(12, SeekOrigin.Current);
                        Nuno5Influence n5Infl = r.ReadStruct<Nuno5Influence>();
                        nuno5.Influences.Add(new NunInfluence(n5Infl));
                    }
                }

                long nextLodPos = lodStart + 48 + (useSkip10 ? 8 : 0) + cpOffset + (cpCount * 0x2C);
                if ((flags & 1) != 0) 
                    nextLodPos += 0x20 * cpCount;
                if ((flags & 2) != 0) 
                    nextLodPos += 0x18 * cpCount;
                nextLodPos += (skips[0] * 4 + skips[1] * 12 + skips[2] * 16 + skips[3] * 12 + skips[4] * 8 + skips[5] * 0x30 + skips[6] * 0x48 + skips[7] * 0x20);
                if ((flags & 4) != 0) 
                    nextLodPos += 0x4 * cpCount;
                r.BaseStream.Position = nextLodPos;
                for (int s = 0; s < skips[8]; s++)
                {
                    uint tempCount = r.ReadUInt32();
                    r.BaseStream.Seek(tempCount * 4 + 12, SeekOrigin.Current);
                }
                r.BaseStream.Seek(skip10Size * skip10Count, SeekOrigin.Current);
            }
            entrySize = r.BaseStream.Position - entryStart;
            return nuno5;
        }
    }
}
