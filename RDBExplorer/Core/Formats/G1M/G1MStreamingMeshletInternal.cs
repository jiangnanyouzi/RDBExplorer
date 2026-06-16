using OpenTK.Mathematics;

namespace RDBExplorer.Core.Formats.G1M;

public class G1MStreamingSectionInternal
{
    public uint Magic;
    public uint Size;
    public uint HeaderCount;
    public uint TableCount;
    public uint Stride;
}

public class G1MStreamingQuantizationInternal
{
    public uint ID;
    public Vector3 Center;
    public float Radius;
    public Vector3 BoundingBoxMin;
    public Vector3 BoundingBoxMax;
    public float UnknownFloat;
}

public class G1MStreamingMeshletTriangleInternal
{
    public ushort A;
    public ushort B;
    public ushort C;
}

public class G1MStreamingMeshletDescriptorInternal
{
    public uint ID;
    public uint TriangleOffset;
    public uint TriangleCount;
    public uint IndexOffset;
    public uint IndexCount;
}

public class G1MStreamingMeshletBoundsInternal
{
    public uint ID;
    public float[] Values = Array.Empty<float>();
    public Vector3 BoundsMinCandidate;
    public Vector3 BoundsMaxCandidate;
}

public class G1MStreamingMeshletMapInternal
{
    public uint ID;
    public uint MeshletIndex;
    public uint CompactIndex;
    public uint PackedSubmesh;
    public ushort SubmeshIndex;
    public ushort RemapPage;
}
