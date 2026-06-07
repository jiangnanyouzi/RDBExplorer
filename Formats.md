### WoLong KTGL Resource Types Table

> WoLong Fallen Dynasty uses the KTGL engine (same family as Nioh 3) but with different TypeInfoKtid values.
> All 36 unique types found across 8 RDB files (350,922 entries, ~94 GB total).
> KTFileType enum / ResourceFactory parser are from Nioh 3 codebase — same IDs apply to WoLong.

| TypeInfoKtid | KTFileType Enum | Extension | Magic | Parser | Description | Entries |
| :--- | :--- | :--- | :--- | :---: | :--- | :--- |
| 0xAFBEC60C | TexContext | `.g1t` | `0x47543147` (GT1G) | ❌ | Texture container (base + streaming) | 120,093 |
| 0x7BCD279F | G1SFile | `.g1s` | `0x5F533247` (_S2G) | ❌ | Compiled shaders | 61,078 |
| 0x8E39AA37 | KTIDFileBinary | `.ktid` | — | ✅ | Resource ID registry | 29,720 |
| 0x6FA91671 | G1AFile | `.g1a` | `0x5F413147` (_A1G) | ❌ | Skeletal animation | 27,021 |
| 0x563BDEF1 | ModelData | `.g1m` | `0x5F4D3147` (_M1G) | ❌ | 3D model (with deps) | 19,339 |
| 0x56EFE45C | PartsModelGroupBindTableBinaryFile | `.grp` | varies | ✅ | Parts model grouping | 19,301 |
| 0x27BC54B7 | RigBinFile | `.rigbin` | `0x42474952` (BGIR) | ❌ | Binary rigging / bone constraints | 12,682 |
| 0x1AB40AE8 | OIDBindTableBinaryFile | `.oid` | — | ✅ | Object ID bind table | 10,360 |
| 0xB340861A | MaterialGroupBindTableBinaryFile | `.mtl` | — | ✅ | Material / texture bindings | 10,360 |
| 0x54738C76 | G1COFile | `.g1co` | `0x4F433147` (OC1G) | ✅ | Collision mesh data | 7,970 |
| 0xED410290 | TexStageTableBinaryFile | `.kts` | `0x4753544B` (GSTK) | ✅ | Texture stage configuration | 7,909 |
| 0xBF6B52C7 | NameDatabaseFile | — | — | ❌ | DB self-reference (no extractable data) | 7,212 |
| 0x20A6A0BB | ObjectDatabaseFile | `.kidsobjdb` | `0x5F444F4B` (_DOK) | ✅ | Main object property database | 7,191 |
| 0xB097D41F | EffectData | `.g1e` | `0x58463147` (XF1G) | ❌ | Visual effect parameters | 5,339 |
| 0xF20DE437 | StaticScreenLayoutTexInfoFile | `.texinfo` | — | ✅ | UI sprite UV coordinates | 2,108 |
| 0xD7F47FB1 | BinaryFile | `.g1t` | `0x47543147` (GT1G) | ❌ | Texture variant / streaming | 585 |
| 0xA027E46B | VideoStreamset | `.file` | — | ❌ | External resource (video) | 441 |
| 0xBBD39F2D | AssetData | `.srsa` | `0x41535253` (ASRS) | ❌ | Audio data | 381 |
| 0x17614AF5 | G1MXFile | `.g1mx` | `0x4D31474B` (M1GK) | ✅ | Model pack container | 318 |
| 0xA8D88566 | G1COXFile | `.g1cox` | `0x4331474B` (C1GK) | ❌ | Extended collision data | 318 |
| 0x79C724C2 | G1PFile | `.g1p` | `0x5031474B` (P1GK) | ❌ | KTGL pack file | 318 |
| 0x5599AA51 | KSCLFile | `.kscl` | `0x4C43534B` (LCSK) | ✅ | Compiled UI layout | 282 |
| 0x5C3E543C | SwingData | `.swg` | `0x53574751` (SWGQ) | ✅ | Physics swing (hair, cloth) | 280 |
| 0x4D0102AC | EffectMeshData | `.g1em` | `0x4D453147` (ME1G) | ❌ | Effect mesh geometry | 113 |
| 0x0D34474D | StreamAssetDataFile | `.srst` | — | ❌ | Streaming audio | 79 |
| 0x1A6300FD | EffectShapeMeshData | `.g1es` | `0x53453147` (SE1G) | ❌ | Shape-specific effect meshes | 33 |
| 0x5B2970FC | KTF2File | ? | `0x3246544B` (2FTK) | ❌ | Unknown KTGL format | 31 |
| 0x133D2C3B | ShaderBindTableBinaryFile | `.sid` | varies | ✅ | Shader bindings | 27 |
| 0xB1630F51 | RenderGraphFile | `.kidsrender` | `0x5F52474B` (_RGK) | ❌ | Render graph definition | 12 |
| 0x786DCD84 | G1NFile | `.g1n` | `0x5F4E3147` (_N1G) | ❌ | Font glyph data | 10 |
| 0xF13845EF | ScreenLayoutShapeInfoFile | `.bin` | — | ❌ | Screen layout shape info | 4 |
| 0x193D2E44 | RBFData | `.grbf` | `0x46425247` (FBRG) | ✅ | RBF data | 2 |
| 0x82945A44 | LandscapeQuadtree | `.bin` | `0x00000000` | ✅ | Spatial hierarchy | 2 |
| 0x6DBD6EA6 | CSVFile | `.mit` | — | ❌ | CSV data | 1 |
| 0xB0A14534 | GlobalConfiguration | `.sgcbin` | `0x43475253` (CGRS) | ❌ | Global system configuration | 1 |
| 0x1FDCAA40 | TaskGraphFile | ? | `0x5F54474B` (_TGK) | ❌ | Task graph | 1 |


### EntryType → Dependency Structure

| EntryType | Dependencies | Trailing | allParamsSize | Description |
| :--- | :--- | :--- | :--- | :--- |
| 0x00 | 0 | 0 | 8 bytes | Simple entry (no deps) |
| 0x01 | 1 | 1 | 21 bytes | Single dependency |
| 0x04 | 1 | 4 | 24 bytes | Single dep + params |
| 0x08 | 2 | 8 | 40 bytes | Two deps + params |
| 0x10 | 4 | 16 | 72 bytes | Four deps + params |

### Flags

| Flags | Storage | Compression | Description |
| :--- | :--- | :--- | :--- |
| `0x00000000` | — | None | External uncompressed (.file) |
| `0x00010000` | External | None | Entry pointing to external .file |
| `0x00400000` | — | Extended | Block in .rdb.bin |
| `0x00410000` | Self-ref | — | DB self-reference (no data) |
| `0x00420000` | Internal | Extended | Normal entry in .rdb.bin |
