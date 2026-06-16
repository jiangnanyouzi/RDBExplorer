using RDBExplorer.Core.Formats.G1M;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RDBExplorer.Core.Wrappers
{
    public class StreamingMeshletModelDataWrapper : ResourceWrapper<G1MData>
    {
        public override bool IsConvertedToText => true;

        public override List<EntryData> GetEntries() => new();

        public override void Load(byte[] data)
        {
            using var reader = new BinaryReader(new MemoryStream(data));
            var model = new G1MData();
            model.Parse(reader);
            Model = model;
        }

        public override async Task SerializeJsonToStreamAsync(Stream stream)
        {
            var summary = new
            {
                skeleton_bones = Model?.Skeleton.Count ?? 0,
                vertex_buffers = Model?.VertexBuffers.Select((x, i) => new
                {
                    id = i,
                    stride = x.Stride,
                    count = x.Count,
                    size = x.Data.Length
                }),
                index_buffers = Model?.IndexBuffers.Select((x, i) => new
                {
                    id = i,
                    stride = x.Step,
                    count = x.Count,
                    size = x.Data.Length
                }),
                layouts = Model?.Layouts.Select((x, i) => new
                {
                    id = i,
                    buffer_indices = x.BufferIndices,
                    semantics = x.Semantics.Select(s => new
                    {
                        buffer_id = s.BufIdx,
                        offset = s.Offset,
                        format = s.Format.ToString(),
                        semantic = s.Type.ToString(),
                        layer = s.Layer
                    })
                }),
                submeshes = Model?.Submeshes.Select(x => new
                {
                    id = x.ID,
                    vertex_buffer = x.VBRef,
                    index_buffer = x.IBRef,
                    material = x.MaterialIndex,
                    meshlet_offset = x.VBStart,
                    meshlet_count = x.VertexCount,
                    index_offset = x.IBStart,
                    index_count = x.IndexCount,
                    primitive_type = x.PrimType
                }),
                streaming_meshlet = new
                {
                    enabled = Model?.IsStreamingMeshlet ?? false,
                    quantization_records = Model?.StreamingQuantizations.Count ?? 0,
                    packed_triangles = Model?.StreamingMeshletTriangles.Count ?? 0,
                    meshlet_descriptors = Model?.StreamingMeshlets.Count ?? 0,
                    meshlet_bounds = Model?.StreamingMeshletBounds.Count ?? 0,
                    meshlet_map = Model?.StreamingMeshletMap.Count ?? 0,
                    sections = Model?.StreamingSections.Select(x => new
                    {
                        magic = $"0x{x.Magic:X8}",
                        size = x.Size,
                        header_count = x.HeaderCount,
                        table_count = x.TableCount,
                        stride = x.Stride
                    })
                }
            };

            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            };

            await JsonSerializer.SerializeAsync(stream, summary, options);
        }
    }
}
