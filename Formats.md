### WoLong KTGL Resource Types Table

> WoLong Fallen Dynasty uses the KTGL engine (same family as Nioh 3) but with different TypeInfoKtid values.
> All 36 unique types found across 8 RDB files (350,922 entries, ~94 GB total).

| TypeInfoKtid | Resource Type | Extension | Magic | Description | Entries |
| :--- | :--- | :--- | :--- | :--- | :--- |
| 0xAFBEC60C | **Texture** | `.g1t` | `0x47543147` (GT1G) | Texture container (base + streaming) | 120,093 |
| 0x7BCD279F | **CompiledShader** | `.g1s` | `0x5F533247` (_S2G) | Compiled shaders | 61,078 |
| 0x8E39AA37 | **BinaryFile** | `.bin` | — | Generic binary / config data | 29,720 |
| 0x6FA91671 | **Animation** | `.g1a` | `0x5F413147` (_A1G) | Skeletal animation | 27,021 |
| 0x563BDEF1 | **ModelData** | `.g1m` | `0x5F4D3147` (_M1G) | 3D model (with deps: textures, shaders) | 19,339 |
| 0x56EFE45C | **ObjectDatabase** | `.kidsobjdb` | varies | Object property database entries | 19,301 |
| 0x27BC54B7 | **RigBinFile** | `.rigbin` | `0x42474952` (BGIR) | Binary rigging / bone constraints | 12,682 |
| 0x1AB40AE8 | **BinaryFile2** | `.bin` | — | Binary data (int header) | 10,360 |
| 0xB340861A | **BinaryFile3** | `.bin` | — | Binary data (int header) | 10,360 |
| 0x54738C76 | **CollisionData** | `.g1co` | `0x4F433147` (OC1G) | Collision mesh data | 7,970 |
| 0xED410290 | **TexStageTable** | `.kts` | `0x4753544B` (GSTK) | Texture stage configuration | 7,909 |
| 0xBF6B52C7 | **DatabaseSelfRef** | — | — | DB self-reference (no extractable data) | 7,212 |
| 0x20A6A0BB | **ObjectDatabaseFile** | `.kidsobjdb` | `0x5F444F4B` (_DOK) | Main object property database | 7,191 |
| 0xB097D41F | **EffectData** | `.g1e` | `0x58463147` (XF1G) | Visual effect parameters | 5,339 |
| 0xF20DE437 | **BinaryConfig** | `.bin` | — | Configuration binary (int header) | 2,108 |
| 0xD7F47FB1 | **TextureVariant** | `.g1t` | `0x47543147` (GT1G) | Texture (variant / streaming) | 585 |
| 0xA027E46B | **ExternalResource** | `.file` | — | External resource (stored as .file) | 441 |
| 0xBBD39F2D | **AssetData** | `.srsa` | `0x41535253` (ASRS) | Audio data (sound effects, banks) | 381 |
| 0x17614AF5 | **ModelContainer** | `.g1mx` | `0x4D31474B` (M1GK) | Model pack container for scenes | 318 |
| 0xA8D88566 | **Unknown_C1GK** | ? | `0x4331474B` (C1GK) | Unknown (KTGL format) | 318 |
| 0x79C724C2 | **Unknown_P1GK** | ? | `0x5031474B` (P1GK) | Unknown (KTGL format) | 318 |
| 0x5599AA51 | **CompiledUILayout** | `.kscl` | `0x4C43534B` (LCSK) | Compiled UI layout | 282 |
| 0x5C3E543C | **SwingData** | `.swg` | `0x53574751` (SWGQ) | Physics swing (hair, cloth, soft body) | 280 |
| 0x4D0102AC | **ExtendedMesh** | `.g1em` | `0x4D453147` (ME1G) | Extended mesh data | 113 |
| 0x0D34474D | **ExternalResource2** | `.file` | — | External resource (stored as .file) | 79 |
| 0x1A6300FD | **EffectShapeMesh** | `.g1es` | `0x53453147` (SE1G) | Shape-specific effect meshes | 33 |
| 0x5B2970FC | **Unknown_2FTK** | ? | `0x3246544B` (2FTK) | Unknown (KTGL format) | 31 |
| 0x133D2C3B | **MiscBinary** | `.bin` | varies | Mixed binary data | 27 |
| 0xB1630F51 | **RenderGraph** | `.kidsrender` | `0x5F52474B` (_RGK) | Render graph definition | 12 |
| 0x786DCD84 | **FontData** | `.g1n` | `0x5F4E3147` (_N1G) | Font glyph data | 10 |
| 0xF13845EF | **BinaryConfig2** | `.bin` | — | Configuration binary (int header) | 4 |
| 0x193D2E44 | **RBFData** | `.grbf` | `0x46425247` (FBRG) | RBF data | 2 |
| 0x82945A44 | **ZeroHeader** | `.bin` | `0x00000000` | Unknown (null-header binary) | 2 |
| 0x6DBD6EA6 | **SmallBinary** | `.bin` | — | Small binary data | 1 |
| 0xB0A14534 | **GlobalConfig** | `.sgcbin` | `0x43475253` (CGRS) | Global system configuration | 1 |
| 0x1FDCAA40 | **Unknown_TGK** | ? | `0x5F54474B` (_TGK) | Unknown (KTGL format) | 1 |


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
