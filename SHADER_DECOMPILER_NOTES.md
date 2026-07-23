# Decompile Shader — Implementation Notes
Complete working notes for the "Decompile Shader" feature added to FModel: reconstructs
C++-style pseudocode for a cooked `UMaterial`/`UMaterialInstance`, using two independent,
complementary decompilation layers. This is a full rewrite consolidating everything learned
across the whole session — read this instead of trying to reconstruct history from git blame.

## Environment this was built/tested against

- Build: `V:\.builds\10.40\FortniteGame\Content\Paks`
- Game version: `EGame.GAME_UE4_23`
- AES key: `0x3FF229552FE0F0DC46A495F9E94766EB6B5106A136597C60E7132F413B7C016E`
- Engine source for ground-truth checks: `D:\Unreal Engine\UE_4.22\Engine\Source` (4.22, not 4.23 —
  close enough that every class checked this session was byte/logic-identical; called out
  explicitly anywhere it might matter)
- Sibling reference project (source of the ported Layer 2 code, see below):
  `D:\build\++Meyou\FModelMats` — a fork of this same FModel/CUE4Parse lineage; its
  `LegacyShaderMap.cs` is byte-identical to ours, confirming direct compatibility
- Ground-truth sample data (same material, editor vs. cooked JSON) lives in `Data/With-Editor-Data/`
  and `Data/Without-Editor-Data/` for `M_UI_Line-V` and `arrows_animated`. No editor JSON was
  pulled for the third test material, `M_FN_Character_MASTER` — it was investigated live via the
  test harness and the engine source instead (see "Verified findings" below)

## The core insight this whole feature rests on

Cooked (non-editor) Unreal materials on **pre-4.25 engines** serialize a real, symbolic,
polymorphic `FMaterialUniformExpression` **tree** for every scalar/vector value that's CPU-folded
from material-instance parameters and constant math — not opaque bytecode. This is exposed by
CUE4Parse as `FMaterialUniformExpressionLegacy` (`LegacyShaderMap.cs`, already present in this repo
before this session, byte-verified against the UE4.23 engine source).

**But** that tree only covers the CPU-folded subset of a material graph — parameters and constant
math with no texture sample or UV/vertex dependency. Anything that reads a texture, UV coordinate,
vertex normal, etc. (i.e. most of what an artist actually draws in the material graph) only exists
as **compiled DXBC pixel-shader bytecode**, usually stored *outside* the material's own package in
a shared shader code library (`.ushaderbytecode`), keyed by each shader's `OutputHash`. Getting at
the real authored graph logic requires disassembling that bytecode and reconstructing a per-output
expression tree from it — that's Layer 2.

Both layers were needed. Layer 1 alone (which is all a naive reading of "cooked materials keep a
uniform expression tree" gets you) only ever surfaces engine-injected plumbing
(`SelectionColor`/`RefractionDepthBias`, see "Verified findings") for a typical UV/texture-driven
material — none of the artist's actual node graph. Layer 2 is what actually answers the original
ask.

## Architecture: two decompilation layers

### Layer 1 — Uniform Expression Tree (CPU-folded parameters/constants)

**File**: `CUE4Parse/CUE4Parse/UE4/Assets/Exports/Material/MaterialShaderDecompiler.cs` (342 lines)

- Extension method `UMaterialInterface.DecompileShaderToPseudo()`.
- Walks `FMaterialResource.LoadedShaderMapLegacy.MaterialCompilationOutput.UniformExpressionSet`'s
  `UniformVectorExpressions[]` / `UniformScalarExpressions[]` arrays (each an
  `FMaterialUniformExpressionLegacy` tree) and pretty-prints them as flat, indexed declarations:
  `float4 UniformVector0 = SelectionColor; // VectorParameter 'SelectionColor', default 000000`.
- Covers every node type the legacy parser produces (~30 types): Constant, Vector/ScalarParameter,
  Texture/TextureParameter, ExternalTexture variants, Sine/TrigMath, SquareRoot/Length/Log2/Log10,
  FoldedMath (Add/Sub/Mul/Div/Dot/Cross), Periodic (**note**: printed as `fractional(x)`, not
  `frac(x)` — it's truncation-based per `FMath::Fractional`, which differs from HLSL's floor-based
  `frac()` for negative inputs; the *actual* `FMaterialUniformExpressionFrac` node prints as
  `frac(x)` since it genuinely is floor-based), AppendVector, Min/Max/Clamp/Saturate,
  ComponentSwizzle, Floor/Ceil/Round/Truncate/Sign/Abs/Fmod, TextureProperty.
- Unknown node types degrade to `/* unsupported: <TypeName> */ 0` rather than throwing or guessing.
- Handles multiple quality levels: a material's `LoadedMaterialResources` can have more than one
  entry (Low/High quality, etc); each is printed as its own section with a
  `Quality=... FeatureLevel=...` header.
- **Explicitly out of scope**: UE 4.25+ cooked materials use a completely different stack-based
  "preshader" bytecode (`FMaterialPreshaderData`) instead of this tree. When only the new-format
  `LoadedShaderMap` is present (not `LoadedShaderMapLegacy`), this reports "not yet supported"
  rather than guessing. (A working reference decoder for that bytecode format exists in the
  sibling project — `MaterialPreshaderDecoder.cs` there — but was reviewed and not ported; it
  covers the same *kind* of CPU-folded-only ground as this layer, just for the newer bytecode, so
  it wouldn't have solved the real problem either.)
- **Public API used by Layer 2** (all added/extended to support texture-name resolution, detailed
  in its own section below):
  - `PrintExpression(FMaterialUniformExpressionLegacy expr, IReadOnlyList<UTexture?>? referencedTextures = null)`
    — the node-printer, reused so both layers render identically.
  - `GetReferencedTextures(UMaterialInterface material)` — resolves the list
    `FMaterialUniformExpressionTexture.TextureIndex` indexes into.
  - `TryResolveTextureIdentifier(FMaterialUniformExpressionLegacy expr, IReadOnlyList<UTexture?>? referencedTextures)`
    — bare identifier (no display styling), for callers building their own names from it.

### Layer 2 — Pixel Shader DXBC Reconstruction (the real authored graph math)

This is the layer that recovers `Abs`/`Add`/`Clamp`/`DotProduct`/texture-sample math — anything
depending on UVs or textures, which Layer 1 structurally cannot reach.

**Ported wholesale** (verbatim copy, zero WPF/graph-UI coupling, compiled unchanged) from the
sibling project:
- `FModel/ViewModels/MaterialPixelShaderAnalyzer.cs` (3189 lines) — hand-written D3D SM4/SM5 (DXBC)
  token-stream disassembler + per-component forward taint/dataflow analysis + backward-slice
  expression-DAG reconstruction. Entry points: `Analyze(FMaterialShaderMap, ...)` (4.25-4.27 new
  format, unused here), `AnalyzeDxil(byte[], ...)` (UE5 SM6/DXIL, unused here), and
  **`AnalyzeLegacy(FMaterialShaderMapLegacy shaderMap, bool usesGBuffer, Func<FSHAHash, byte[]> sharedCodeResolver)`**
  — the one actually used, for pre-4.25. Returns a `PixelShaderWiring`:
  - `PinSources` — coarse: which serialized values (`PixelValueSource`: Vector/Scalar-expression
    index, or Texture slot+index+channel) reach which named output pin. Always populated.
  - `PinExpressions` — a `PixelExpressionNode` DAG per pin. Auxiliary, wrapped in try/catch
    internally; can be absent even when `PinSources` succeeded.
  - `PinDisassembly` — literal annotated SM5 assembly text per pin (used as a raw-truth
    cross-check throughout this session, e.g. to resolve the texture-channel question below).
  - `ShaderStages` — every *other* compiled shader stage in the material's shader map (vertex,
    geometry, compute, shadow/depth pixel, other base-pass permutations), each with
    `OutputValues`/`BindsMaterial` — used to prove a scalar parameter is genuinely dead everywhere,
    not just unused in the one analyzed pixel shader (see `M_FN_Character_MASTER` finding below).
- `FModel/ViewModels/MaterialDxil.cs` (1056 lines) — DXIL/LLVM-bitcode support for UE5 SM6. Ported
  for completeness; **never exercised** — no UE5 test asset was available this session.

**New code written this session** — `FModel/ViewModels/PixelShaderDecompiler.cs` (536 lines):

- `CreateLegacyShaderCodeResolver` / `FindLegacyShaderMap` — ported near-verbatim from the sibling
  project's `MaterialGraphViewModel.cs`. Locates every `.ushaderbytecode` file in the mounted
  provider (scanned by filename, cached per-provider via `ConditionalWeakTable`), opens each as
  `FShaderCodeArchive`, and looks up a shader's compiled bytecode by `OutputHash` via
  `FLegacyShaderCodeArchive.TryGetCode(hash)`.
- `public static string? DecompilePixelShaderToPseudo(UMaterialInterface material)` — the main
  entry point. Loops **every** `LoadedMaterialResources` entry with a `LoadedShaderMapLegacy` (see
  bug #7 below), calling `DecompileOneResource` for each and printing every quality level as its
  own section.
- `public static (PixelShaderWiring, FUniformExpressionSetLegacy)? AnalyzeForDiagnostics(...)` —
  two overloads (auto-pick the first shader map found, or pass an explicit one) exposing the raw
  wiring for tooling (the test harness) that wants to inspect the DAG directly rather than trust
  the printer's output.
- **CSE (common-subexpression-elimination) pass over the DAG**: `CountRefs` → `AssignNames` →
  `Ref`, using a `PrintCtx` carrying the expression set, resolved texture list, ref-counts, and
  assigned names. Any `PixelExpressionNode` reached more than once (by reference identity) across
  the whole shader is hoisted into a `var _N = ...;` declaration in dependency order; every other
  reference becomes just that name. This is necessary, not cosmetic — before this pass, a shared
  subtree printed its full text at every occurrence (`cb0[44]` alone appeared **246 times** in one
  raw unbounded print of `M_UI_Line-V`, an 88.8KB single line; after CSE, 56 lines / 3.4KB).
  Refinements on top of the base rule:
  - Bare numeric immediates (`"imm"` op) are never hoisted — naming a literal `0` isn't clearer
    than inlining it, only adds an indirection to look through.
  - Every texture `"sample"` node is hoisted **unconditionally**, even if only referenced once —
    see "Texture identification" below for why and what it looks like.
  - Hoisted names use an `_N` prefix (`_0`, `_1`, ...), deliberately **not** `t0`/`v0`/`r0`/`o0`
    etc. — those single-letter prefixes are the real DXBC register classes (`t#` texture/SRV, `v#`
    input, `r#` temp, `o#` output), and they legitimately appear inside `Detail` strings the
    analyzer itself produces (e.g. `"sample_l — t1 (engine resource)"` names *real* register `t1`).
    An `_N` name can never collide with those. When a texture resolves to a name, that name is used
    directly instead of `_N` (see below), with `MakeUniqueName` disambiguating collisions (same
    texture sampled twice → `Pattern_HeavyArrows`, `Pattern_HeavyArrows_1`).
- `TryCollapseUniformSelect` — a **verified-equivalent** peephole simplification, not a guess: an
  `append(cond?A.x:B.x, cond?A.y:B.y, ...)` built from phi nodes that all share the identical
  condition edge and the identical two source nodes (with the expected identity swizzle — `""`/`x`
  at position 0, `y` at 1, etc — and no stray negate/abs) collapses to `(cond ? A : B).xy...`.
  Every single component must match exactly (reference identity on condition/then/else nodes, and
  edge modifiers); any mismatch anywhere aborts the whole collapse and falls back to the fully
  explicit `append(...)` form.
- Constant-buffer reads (`"cbrow"` nodes) resolve two ways: reads from the identified **Material**
  uniform buffer resolve through `MaterialShaderDecompiler.PrintExpression` (giving the real
  parameter name/expression, e.g. `SelectionColor`); reads from any **other** bound buffer (View,
  Primitive, translucent-lighting, ...) print as a labeled comment placeholder using the analyzer's
  own `Detail` string, e.g. `/* FViewUniformShaderParameters cb0[66] */ 0` — never silently hidden,
  never fabricated as a number.
- `"phi"` nodes (value that differs between if/else branches) print as
  `(Condition ? Then : Else)`, using all 3 args the analyzer's `MergeBranches` always emits in that
  exact order (verified against `MaterialPixelShaderAnalyzer.cs` ~line 2370).

### Texture identification

Sampled textures originally printed as bare `Texture[slot=N, index=M]`. Fully resolved now, with
every step verified against real data rather than guessed:

1. **The `(Slot, Index)` → array mapping is read straight out of the analyzer's own
   `BuildLegacyTextureRegisterMap`** (`MaterialPixelShaderAnalyzer.cs` ~line 1256): Slot 0 =
   `Uniform2DTextureExpressions[Index]`, Slot 1 = `UniformCubeTextureExpressions[Index]`, Slot 3 =
   `UniformVolumeTextureExpressions[Index]`, Slot 4 = `UniformVirtualTextureExpressions[Index]` —
   the exact same arrays the analyzer itself indexed into when building its register table.
   (Slot 2 — Texture2DArray — and external textures are never entered into that map at all, a
   quirk of the analyzer; those still fall back to a bare index, honestly.) Each array entry is an
   `FMaterialUniformExpressionLegacy`, printed via the existing `PrintExpression` — so a texture
   **parameter** (has a `ParameterName`, e.g. `SpecularMasks`, `Diffuse`, `Normals`) already
   resolved correctly before any of the work below; only **hard-referenced** textures (a
   `TextureSample` node pointing directly at a specific asset, no parameter) needed it.
2. **`FMaterialUniformExpressionTexture::TextureIndex` is documented in the engine source itself**
   (`MaterialShared.h`) as `"Index into FMaterial::GetReferencedTextures"`. For UE4.25+ cooks that
   list is pre-serialized (`CachedExpressionData`) and CUE4Parse already exposed it as
   `UMaterial.ReferencedTextures` — but confirmed empirically that for our target game version
   (GAME_UE4_23), CUE4Parse's population code is gated to `Game >= GAME_UE4_25` only
   (`UMaterial.cs` ~line 46), so the list is **always empty** pre-4.25. Also confirmed the raw
   cooked JSON has no top-level `"ReferencedTextures"` property to fall back to either (checked
   directly against `Data/Without-Editor-Data/arrows_animated.json` — absent).
3. **Read the UE4.22 engine source directly to find the real mechanism** rather than guess one:
   pre-4.25, `FMaterialResource::GetReferencedTextures()` (`MaterialShared.cpp:928`) returns
   `Material->ExpressionTextureReferences`, a **transient runtime-only cache** (never serialized)
   that `UMaterial::CacheExpressionTextureReferences` rebuilds on load by calling
   `UMaterial::AppendReferencedTextures` (`Material.cpp:5470`), which walks the material's own
   `Expressions` array. Its own comment: it appends a slot for every `CanReferenceTexture()`-capable
   expression **even when the resolved texture is null** — *"Append even if null as textures can be
   stripped at cook without our knowledge so we want to maintain the indices."* That's exactly why
   this still works even though this cook's `Expressions` array has its *input wiring* stripped
   (confirmed: `Coordinates.Expression` is `null` on both `arrows_animated` `TextureSample` nodes)
   — the `Texture` property and node **order** survive independently of the wiring (confirmed
   directly in the raw cooked JSON, `Data/Without-Editor-Data/arrows_animated.json:3172-3233`: both
   `MaterialExpressionTextureSample` exports are present with an intact `Texture` reference, e.g.
   `Texture2D'Pattern-HeavyArrows'`).
4. **Implementation** — `MaterialShaderDecompiler.GetReferencedTextures`: walks up a
   `UMaterialInstance` chain to the base `UMaterial`; if `ReferencedTextures` is non-empty (4.25+
   path) uses it as-is; otherwise walks `baseMaterial.Expressions` and, for every entry that
   resolves to a `UMaterialExpressionTextureBase`-derived export (`UMaterialExpressionTextureSample`,
   `TextureSampleParameter`, `TextureSampleParameter2D`, ... — CUE4Parse already has dedicated C#
   classes for these with a real `.Texture` property, `UMaterialExpressionTexture.cs`), appends its
   `Texture` (possibly null) in order.
5. **Known, explicit gap, never silently wrong**: `MaterialFunctionCall` nodes are not recursed
   into — the engine's real algorithm walks into a called function's own texture references too
   (`GetDependentFunctions` + recursive `AppendReferencedTextures`); this implementation doesn't.
   A material whose texture indices depend on a function call's own textures will have later
   indices misaligned. Never surfaces silently, though: `DescribeHardTexture`/`DescribeTexture`
   only print a resolved name when the index actually falls inside the (possibly incomplete) list
   and the entry isn't null; any miss falls back to the same honest bare `Texture[N]`. Confirmed
   this exact fallback firing correctly on `M_FN_Character_MASTER` (a material that visibly uses
   functions given its complexity) — two texture reads stayed as bare indices
   (`Texture[9]`/`Texture[10]`) while every other texture in the same material resolved correctly.
6. **Hoisting refinement**: initially a resolved name only appeared as a trailing comment at the
   `sample(...)` call's point of use, easy to miss inside a larger expression. Changed so every
   `sample` node is unconditionally hoisted (see CSE section above), so the texture identity is the
   variable's own name, e.g.:
   ```
   var Pattern_HeavyArrows = sample(...) /* Pattern_HeavyArrows [Texture 0] */;
   var linear_gradient = sample(...) /* linear_gradient [Texture 1] */;
   var _9 = saturate((Pattern_HeavyArrows * (-linear_gradient + 1)));
   ```
7. Cosmetic bug caught along the way: the resolved name was first wrapped in its own
   `/* Texture[N] */` comment, which nested inside the `sample` node's own comment wrapper,
   producing invalid `/* Name /* Texture[N] */ */`. Fixed by using `[Texture N]` (square brackets,
   not a comment) for the verification suffix.
8. **End-to-end verification**, all three test materials, after every change above:
   `arrows_animated`'s two hard-referenced textures resolve to the exact names at the exact indices
   the earlier `ChannelMap` diagnostic had independently confirmed. `M_UI_Line-V` (no
   material-bound textures at all) unaffected. `M_FN_Character_MASTER`'s parameter-named textures
   (`SpecularMasks`, `Diffuse`, `M`, `Normals`) still resolve with no name collisions; its two
   function-nested textures fall back honestly as described in point 5, consistently hoisted like
   every other sample now.

### Material Parameter Collection (MPC) identification

Requested after the user pointed out `M_FN_Character_MASTER` calls a material function
(`FN_Char_RimColor_v3`) that references the `FortniteMaterialParameters` MPC via three
`MaterialExpressionCollectionParameter` nodes, with the raw with-editor JSON for those nodes as
evidence.

**Correction (this session, after the user firmly disputed an earlier wrong conclusion here):**
an initial diagnostic pass reported `ParameterCollections.Length == 0` for this material's compiled
shader map and concluded the compiled shader never references the MPC despite the source-level
node existing. The user rejected that outright ("It is used buddy... it is factual"). Re-investigating
instead of standing by the prior finding turned up a real parsing bug in CUE4Parse, not a dead
code path in the material:

- `FUniformExpressionSet::Serialize` (engine source, `MaterialUniformExpressions.cpp:102-116`)
  serializes, in order: the five expression arrays, then a **reserved/always-empty-at-this-engine-
  version `Uniform2DTextureArrayExpressions` array** (the comment there literally says "Adding 2D
  texture array now to prevent bumping version when the feature gets added" — i.e. serialized
  even though nothing populates it yet), and only *after that* `ParameterCollections`.
- CUE4Parse's `FUniformExpressionSetLegacy` constructor (`LegacyShaderMap.cs`) had a `PreVirtualTexture`
  branch that read `UniformExternalTextureExpressions` and then jumped straight to
  `ParameterCollections = Ar.ReadArray<FGuid>()`, skipping the reserved array's read entirely. Since
  that reserved array's serialized count is always `0`, the code was reading *that* `0` as if it
  were `ParameterCollections`'s own count, then returning immediately — leaving the real
  `ParameterCollections` array (count + GUIDs, whenever non-empty) unconsumed in the stream, where it
  silently fell into the `SkipToDebugDescription` anchor-skip a few fields later. This is exactly what
  an old comment in that same file had already half-noticed ("M_FN_Character_MASTER measures 78 [bytes],
  ... consistent with its material parameter collection reference") without tracing it to root cause.
- **Fix**: added the missing `ReadExpressionArray(Ar)` call for the reserved array before reading
  `ParameterCollections`, matching the engine order exactly.
- **Verified against real data after the fix**: `M_FN_Character_MASTER`'s shader map now reports
  `ParameterCollections.Length=1` (both quality levels), GUID `BEC8A175-4188E7B2-...`, which
  `MaterialParameterCollectionResolver` correctly resolves to `FortniteMaterialParameters` — matching
  the user's evidence exactly. Re-ran the full harness afterward: zero exceptions, no change to any
  other material's decompiled output (the bug only affected the `ParameterCollections` tail, not the
  expression arrays themselves).

1. **Confirmed via engine source that MPC values structurally never appear in an
   `FMaterialUniformExpression` tree at all**: `AccessCollectionParameter`
   (`HLSLMaterialTranslator.h:2327`) emits a direct HLSL constant-buffer read,
   `MaterialCollection{N}.Vectors[ParameterIndex]`, completely bypassing the uniform-expression
   system. So Layer 1 can never show these by design, not by omission.
2. **`FUniformExpressionSet(Legacy).ParameterCollections[N]` stores exactly the referenced
   collection's own `StateId`** — confirmed directly in `FUniformExpressionSet::SetParameterCollections`
   (`MaterialUniformExpressions.cpp:220`: `ParameterCollections.Add(InCollections[CollectionIndex]->StateId)`).
   A bare GUID, with no path back to which package it belongs to.
3. **`M_FN_Character_MASTER`'s compiled shader map does reference the MPC** (see the correction
   above for the parsing bug that originally hid this): `ParameterCollections.Length == 1` for both
   quality levels, resolving to `FortniteMaterialParameters`. Separately, though: a scan of every
   distinct uniform buffer name bound across all 233 compiled shader variants (every stage, every
   permutation) in this specific cooked resource still turns up zero shaders that actually bind a
   `MaterialCollection0/1` buffer at the bytecode level. That's not a contradiction —
   `ParameterCollections` is populated once per material during translation (whenever
   `AccessCollectionParameter` is called for *any* output property during HLSL generation,
   `FMaterial::CompileProperty`/`GetReferencedParameterCollections`), while the specific, already-
   optimized DXBC bytecode for a given compiled permutation can still end up not reading it if that
   branch got compiled away for that permutation/quality — e.g. the same Static Switch pattern
   already confirmed for `EQ_MaxIntensityTreble`/`Rim_Light_Overall_Boost` earlier in this material.
   The pixel-shader reconstruction path also deliberately picks the "cleanest" permutation
   (`FNoLightMapPolicy`, non-Skylight) for readability, which may not be the specific permutation
   where a given conditional branch is live — this remains unverified per-permutation (see point 8).
4. **The same "properties survive cooking, wiring doesn't" pattern already relied on for textures
   holds here too** — verified by loading `FN_Char_RimColor_v3` directly and dumping its cooked,
   non-editor `MaterialExpressionCollectionParameter` exports: `Collection` (a fully resolvable
   reference to the `FortniteMaterialParameters` package), `ParameterName`, and `ParameterId` are
   all intact, exactly matching the with-editor JSON the user provided, byte-for-byte on the three
   GUIDs given (`SunAndMoonModelDirectionalVector`/`SunLightColor`/`FogDirectionalInscatteringColor`).
5. **The scalar/vector packing layout is read straight from the engine source, not inferred**:
   `UMaterialParameterCollection::GetParameterIndex` (`ParameterCollection.cpp:303`) - scalar
   parameters pack 4-to-a-row in declaration order (`row = i/4, component = i%4`), vector
   parameters occupy one full row each, starting immediately after the last (possibly partial)
   scalar row.
6. **New file**: `CUE4Parse/CUE4Parse/UE4/Assets/Exports/Material/MaterialParameterCollectionResolver.cs`.
   `FindReferencedCollections(UMaterialInterface)` walks the base material's own package plus every
   `MaterialExpressionMaterialFunctionCall` target it can reach (bounded depth, cycle-safe via a
   visited-package set), collecting every `MaterialExpressionCollectionParameter.Collection`
   found and building one `ResolvedCollection` per unique asset (deduped by `StateId`) with a
   complete row map built from the collection's *entire* `ScalarParameters`/`VectorParameters`
   lists - not just the specific parameters the material happens to reference by name. None of the
   involved classes (`MaterialExpressionCollectionParameter`, `MaterialExpressionMaterialFunctionCall`,
   `UMaterialParameterCollection`) have dedicated CUE4Parse C# types, so every read goes through the
   generic `GetOrDefault<T>` property-holder API rather than typed members.
   **Verified against real data**: run against `M_FN_Character_MASTER`, correctly found the one
   collection, computed all 71 rows, and placed all three named parameters as full-row vectors at
   the exact row indices matching the real asset dump (`SunLightColor` row 22,
   `FogDirectionalInscatteringColor` row 27, `SunAndMoonModelDirectionalVector` row 29) - the row
   contents for the scalar-packed rows (0-3) also matched the collection's raw JSON dump exactly,
   in declaration order.
7. **Wired into `PixelShaderDecompiler.cs`**: a foreign `cbrow`'s `Detail` string is built by the
   analyzer as the exact literal `"{bufferName} cb{Index0}[{Index1}]"` whenever the bound buffer's
   name is known (already relied on for the `FViewUniformShaderParameters`-style labels). A regex
   matching that exact format specifically for a `MaterialCollectionN` buffer name resolves `N` to
   `expressionSet.ParameterCollections[N]`, calls the resolver, and prints either the vector
   parameter's name directly (`SunLightColor /* FortniteMaterialParameters[22] */`) or, for a
   scalar-packed row, all 4 possible names at that row as a comment (since which *specific*
   component a given read means is decided by the swizzle the caller already prints alongside this
   value, not by anything visible at this leaf) - never guesses which one.
8. **Fully verified end-to-end, including the `cbrow` textual output.** The Quality=High resource's
   `TBasePassPSFNoLightMapPolicy` bytecode does read the collection - at constant-buffer register cb2,
   rows 22/27/29 - it just isn't labeled by name there, because **MaterialCollectionN buffers are
   never named in a shader's own reflected `UniformBufferParameters` list, for any shader**: confirmed
   against engine source, `FShaderUniformBufferParameter::ModifyCompilationEnvironment`
   (`HLSLMaterialTranslator.h:1082-1090`) only declares the raw HLSL cbuffer/resource-table entry for
   it; the buffer is bound at draw time through a separate runtime path, never through a serialized
   `FShaderUniformBufferParameter` the way View/Primitive/Material are. That's also why the "233
   shaders" buffer-name scan in point 3 never finds one, for any material, by design - not a bug.
   So the `cbrow` printer was extended with a second resolution path, purely by elimination: any
   foreign cbrow that reaches the plain `cb{N}[{row}]` fallback (no name resolved, and N isn't the
   Material buffer's own register - confirmed distinct: Quality=Low's `MaterialUniformBuffer.BaseIndex`
   is 2 with no other register in play at all, matching that quality level's whole rim-light/MPC branch
   being stripped; Quality=High's is 3, with cb2 as the one leftover register) is, by elimination, one
   of the shader map's own `ParameterCollections` entries - matched to `ParameterCollections` in order
   of first appearance while printing (exact for the single-collection case verified here; a
   hypothetical multi-collection material would need the ordering assumption revisited).
   **Result, verified against the real compiled output**: `M_FN_Character_MASTER`'s Quality=High
   pseudocode now reads `SunLightColor /* FortniteMaterialParameters[22] */`,
   `FogDirectionalInscatteringColor /* FortniteMaterialParameters[27] */`, and
   `SunAndMoonModelDirectionalVector /* FortniteMaterialParameters[29] */` at exactly the DXBC
   instructions that dataflow into `Emissive Color` - matching the user's original with-editor JSON
   evidence by name, not just by mechanism. Re-ran the full harness afterward: zero exceptions, no
   regressions elsewhere.

### Material Instance parameter override resolution

Requested after testing a real `MaterialInstanceConstant` for the first time this session
(`FortniteGame/Content/.../F_MED_Body_Grave`, a skin instance of `M_FN_Character_MASTER`). Its
`LoadedMaterialResources` decompiles fine (byte-identical DXBC to its parent, as expected — a plain
value-parameter skin doesn't force a separate shader compile), but every `VectorParameter`/
`ScalarParameter`/`TextureParameter` node printed only the *base material's* compile-time default
(e.g. bare `Diffuse`, `ColorHigh` identifiers) - never this specific instance's own override, even
though the instance genuinely has one (dumped directly: 4 texture overrides incl.
`Diffuse=T_F_MED_Grave_Body_D`, plus `Skin Boost Color And Exponent=FFD2D4`, `RoughnessMax=1`,
`RoughnessMin=0`, etc.). Decompiling an instance specifically to only ever see the master's generic
defaults defeats the purpose, so this was fixed rather than left as a known gap:

- **New `MaterialShaderDecompiler.InstanceParameterOverrides`**: walks from the queried material up
  through `UMaterialInstance.Parent` (guarded, max 16 hops), collecting `VectorParameterValues`/
  `ScalarParameterValues`/`TextureParameterValues` with first-seen-wins per parameter name. This
  matches `UMaterialInstance::GetVectorParameterValue`'s own real lookup order exactly (checks its
  own value first, only delegates to `Parent->Get...()` when it has none) - not an approximation of
  it.
- **Threaded through both decompiler layers** as an optional trailing parameter (`null` default, so
  every existing call site - and a plain `UMaterial` with no instance chain, which resolves to an
  `Empty` override set - is behaviorally unchanged): `MaterialShaderDecompiler.Print`/`PrintExpression`
  now annotate a `VectorParameter`/`ScalarParameter`/`TextureParameter` node with
  `[instance: <value>]` whenever the override dictionary has an entry for that exact parameter name;
  `PixelShaderDecompiler.PrintCtx` builds one `InstanceParameterOverrides` per resource and passes it
  into every `ResolveUniform`/`DescribeTexture` call. Square brackets, not a `/* */` comment - the
  same reason `DescribeHardTexture`'s `[Texture N]` suffix uses them: this can appear inside a
  `sample`/`cbrow` node's own trailing comment, and a nested `/* */` was already a real bug fixed
  earlier this session.
- **Verified against real data**: `F_MED_Body_Grave`'s decompile now reads
  `sample_b(_4, _5.y) /* Diffuse [instance: T_F_MED_Grave_Body_D] */` (and the same for `M`/
  `Normals`/`SpecularMasks`, each resolving to this skin's own texture asset), and
  `Skin_Boost_Color_And_Exponent [instance: Color(1, 0.645, 0.654, 2)]` - which, decoded through
  linear-to-sRGB (the same encoding `FLinearColor`'s own hex `ToString()` uses), is exactly `FFD2D4`,
  matching the raw override dump byte-for-byte. `RoughnessMin`/`RoughnessMax` show `[instance: 0]`/
  `[instance: 1]`, matching too. Parameters this instance does *not* override (`ColorHigh`, `HitGlow`,
  ...) print exactly as before, with no spurious annotation. Re-ran the full harness against
  `M_FN_Character_MASTER` (a plain `UMaterial`, no instance chain) afterward to confirm zero
  regression: output byte-identical to before this change, including the MPC resolution above.

### Engine uniform buffer (View/Primitive) row identification - "Camera Vector", "Pixel Normal WS", etc.

Requested after the user asked how to recognize common material-graph concepts like "Camera Vector"
or "Pixel Normal WS" in the decompiled output. Until this point, every read from the View or
Primitive uniform buffer that wasn't the material's own data printed as an opaque placeholder, e.g.
`/* FViewUniformShaderParameters cb0[66] */ 0` - the buffer *name* resolved fine (a different path,
via `shader.UniformBufferParameters`), but the row number inside it was untouched.

- **Key realization, confirmed against engine source, not assumed**: unlike a material's own
  parameters or a Parameter Collection (both per-asset data with no fixed layout),
  `FViewUniformShaderParameters` and `FPrimitiveUniformShaderParameters` are literally hardcoded C++
  structs (`Engine/Public/SceneView.h`'s `VIEW_UNIFORM_BUFFER_MEMBER_TABLE`,
  `Engine/Public/PrimitiveUniformShaderParameters.h`), the same for every shader that binds them - so
  their row layout is fully determined by their own field declaration order, not anything
  material-specific.
- **The exact packing rule was read from engine source, not guessed**:
  `RenderCore/Private/ShaderParameters.cpp`'s `CreateHLSLUniformBufferStructMembersDeclaration` shows
  the HLSL row of a member is its real C++ `STRUCT_OFFSET` (`Member.GetOffset()`); each field's
  required alignment is likewise confirmed per-type from `TShaderParameterTypeInfo<T>::Alignment`
  (`RenderCore/Public/ShaderParameterMacros.h`): `float`/`int32`/`uint32`/`bool` = 4,
  `FVector2D` = 8, `FVector`/`FVector4`/`FLinearColor`/`FMatrix` = 16 (a `FMatrix` always spans
  exactly 4 full rows, each 16-byte aligned; an array's elements are each 16-byte aligned, so an
  array element never shares a row with anything else). Given the field list and these alignments,
  standard C struct packing (each field starts at the next offset that's a multiple of its own
  alignment) determines every row/component exactly - meaning a lone scalar like `MaterialTextureMipBias`
  can end up sharing a row with unrelated neighbors declared right after it (row 137 packs
  `DeltaTime`/`MaterialTextureMipBias`/`MaterialTextureDerivativeMultiply`/`Random` into
  `.x`/`.y`/`.z`/`.w`), while a 16-byte-aligned `FVector`/`FVector4` claims a row (or, for `FVector`'s
  true 12-byte size, up to 3 of its components) by itself.
- **Computed with a script, not by hand**: transcribed the full ~130-field `VIEW_UNIFORM_BUFFER_MEMBER_TABLE`
  (171 rows total) and the smaller `FPrimitiveUniformShaderParameters` field list (26 rows), then
  wrote a small Python script (`layout.py`) to apply the alignment rule above and compute every
  field's exact (row, component) - deliberately not done by hand, since a single arithmetic slip
  partway through 130+ fields would silently misalign everything after it. The script asserts no two
  fields ever claim the same component (caught and fixed one real bug this way mid-session: a naive
  first draft let a second `FVector2D` sharing a row silently overwrite the first one's components -
  `FieldOfViewWideAngles`/`PrevFieldOfViewWideAngles` at row 124).
- **Cross-checked against real compiled output before being trusted for a single row** - not just
  derived in the abstract: on `M_FN_Character_MASTER`'s actual decompiled pixel shader,
  - row 44-47 is read as a 4-row matrix multiplied against `SV_Position` and divided by `.w` - exactly
    the `SVPositionToTranslatedWorld` reconstruction idiom.
  - row 66 is subtracted from a world-space vertex interpolator - exactly what `PreViewTranslation`
    (translated-world → true world) is for.
  - rows 130/131/132 each blend into diffuse/specular/normal computation right where
    `Diffuse`/`Specular`/`NormalOverrideParameter` (debug view-mode overrides) would.
  - row 139 multiplies the entire lit-emissive expression - exactly `UnlitViewmodeMask`'s role.
  - row 137's second component feeds a biased texture sample's LOD-bias argument - exactly
    `MaterialTextureMipBias`.
  - Primitive row 19 (`ObjectBounds`) and row 5 (`ObjectWorldPositionAndRadius`) combine as
    `abs(WorldPos - Center) < (HalfExtents + 1)` - a textbook "is this pixel inside the object's
    (expanded) bounding box" check.

  Seven independent rows, zero mismatches, before any of this was wired into the printer.
- **New file**: `CUE4Parse/CUE4Parse/UE4/Assets/Exports/Material/EngineUniformBufferLayout.cs` -
  `Dictionary<int, string?[]> ViewRows`/`PrimitiveRows`, row → 4 per-component names (`null` where
  that byte range is real padding, not a named field).
- **Wired into `PixelShaderDecompiler.cs`**: a `cbrow` node represents one *whole, unswizzled* row
  (any `.x`/`.y`/`.z`/`.w` selection happens separately, at the reference site via `PrintArg`), so a
  row where every component names the same field (any vector/matrix row) prints that name directly
  (`View.SVPositionToTranslatedWorld`, `View.PreViewTranslation`, `Primitive.ObjectWorldPositionAndRadius`,
  ...); a row packing several unrelated scalars can't collapse to one name without guessing which the
  caller's swizzle will pick, so all four are shown instead (`View.Row137 /* x=DeltaTime,
  y=MaterialTextureMipBias, ... */`) - the same rule `TryDescribeCollectionRow`'s scalar branch
  already follows for Parameter Collection rows.
- **Sets up, but does not by itself produce, the answer to the question that prompted this**: with
  rows resolved, the shape `_23 = normalize(-_22)` (where `_22` is the reconstructed world position)
  is recognizably **Camera Vector**, and `_24 = max(dot(_19, _23), 0)` (where `_19` is the
  TBN-transformed, renormalized tangent-space normal) recognizably feeds a standard NdotV/Fresnel
  term with **Pixel Normal WS** - but at this point that was still only a description *of* the
  output, not literal text *in* it (the variables were still auto-numbered `_19`/`_22`/`_23`). The
  user caught this directly ("i don't see Camera Vector in the file") - see the next section for the
  actual fix.
- Re-ran the full harness against both `F_MED_Body_Grave` and `M_FN_Character_MASTER` afterward: zero
  exceptions, and the base material's output is unaffected apart from the new resolved names (no
  `[instance: ...]` annotations appear there, as expected for a plain `UMaterial`).

### Semantic idiom recognition - literal "CameraVector"/"PixelNormalWS"/"WorldPosition_CamRelative" names

The row-name work above only resolves individual buffer *reads*; it doesn't recognize the
multi-instruction *pattern* built on top of them. Implemented after the user pointed out the gap
directly. Each recognized shape was confirmed against the engine's own compiled formula first, not
pattern-matched speculatively:

- **`Parameters.CameraVector`** (`Engine/Shaders/Private/MaterialTemplate.ush:2120`):
  `Parameters.CameraVector = normalize(-Parameters.WorldPosition_CamRelative.xyz)` - the file's own
  comment: "TranslatedWorldPosition is the world position translated to the camera position, which
  is just -CameraVector".
- **`Parameters.WorldPosition_CamRelative`** (`MaterialTemplate.ush:2101`): set directly from the
  vertex shader's `TranslatedWorldPosition` interpolator - the value the SV_Position reconstruction
  matrices (`SVPositionToTranslatedWorld`/`ScreenToWorld`/`ScreenToTranslatedWorld`/`ClipToTranslatedWorld`
  - different pipeline paths pick different ones) perspective-divide their way back to in the pixel
  shader.
- **`PixelNormalWS`** (`HLSLMaterialTranslator.h PixelNormalWS()`): `return
  AddInlinedCodeChunk(MCT_Float3, TEXT("Parameters.WorldNormal"))` - i.e. exactly the material's own
  (unencoded) Normal output value, confirmed by UE's deferred GBuffer convention
  (`Normal_output = WorldNormal*0.5+0.5`, observed consistently across every material decompiled this
  session) - so the node feeding the "Normal" pin's `*0.5+0.5` encode *is* `Parameters.WorldNormal`.

Implementation (`PixelShaderDecompiler.cs`):
- `TryGetGBufferNormalEncodeInput` - matches the "Normal" pin's root against the `X*0.5+0.5` shape
  (a `mad` node whose second and third args are both `imm` nodes ≈ 0.5); on match, `ctx.PixelNormalWsNode`
  is seeded with `X` before the CSE/naming pass runs, so any later reference to that same node (by
  reference identity, not by shape) gets the name `PixelNormalWS`.
- `IsTranslatedWorldPositionDivide` - matches a `div` node dividing a node by its own `.w`
  (a homogeneous-to-Euclidean divide), **and separately confirms** the numerator is built (through
  any depth of `add`/`mul`) from at least one `cbrow` read that `EngineUniformBufferLayout` resolves
  to one of the four position-reconstruction matrices above. The div-of-self shape alone is
  deliberately not sufficient - it's the matrix-identity check that rules out mislabeling an unrelated
  homogeneous divide.
- `IsCameraVectorNode` - matches `rsqrt(dot3(N,N)) * -N` (by reference identity on `N`, not just
  matching shape) where `N` independently passes `IsTranslatedWorldPositionDivide` - so a light
  direction or any other `normalize(-X)` in the shader does *not* get relabeled, since `X` there was
  never built from a position-reconstruction matrix row.
- All three are added to `AssignNames`'s existing force-hoist set (previously only texture `sample`
  nodes) and given their real name instead of `_N`, ahead of the generic texture-name/fallback checks.
- **Verified against real output**: `M_FN_Character_MASTER` now reads
  `var PixelNormalWS = (rsqrt(dot3(_18, _18)).www * _18); ... var WorldPosition_CamRelative = (_20 /
  _20.www); var CameraVector = (rsqrt(dot3(-WorldPosition_CamRelative, -WorldPosition_CamRelative)).www
  * -WorldPosition_CamRelative); var _21 = max(dot3(PixelNormalWS, CameraVector), 0); ... Normal =
  (PixelNormalWS * Const(0.5, 0.5, 0.5, 0) + Const(0.5, 0.5, 0.5, 0));` - the last line is the strongest
  possible confirmation, since `PixelNormalWS` there is literally its own detected GBuffer-encode
  input. Checked the whole file afterward for any `rsqrt(dot3(-...` left un-renamed (would indicate a
  missed case) - none found. Cross-checked against `arrows_animated` (a 2D UI material): its output
  shows `WorldPosition_CamRelative` (it does reconstruct world position for an effect) but *no*
  `CameraVector`/`PixelNormalWS` at all - correctly absent, since that material never actually
  computes either, confirming the recognizers don't over-fire.

### Tangent/Bitangent/VertexNormal - the TBN basis feeding PixelNormalWS

Requested as a direct follow-up ("is there anything else we can clean up without guessing?"). `_15`/
`_16`/`_17` in the earlier output (the raw `TEXCOORD10`/`TEXCOORD11` vertex interpolants and their
cross-product combine) were still unlabeled. Before touching anything, confirmed which vertex factory
actually compiled this shader rather than assuming: dumped `FMeshMaterialShaderMapLegacy.VertexFactoryTypeName`
for every `TBasePassPS*` shader in `M_FN_Character_MASTER`'s shader map -
**`TGPUSkinVertexFactorytrue`** (a skinned character material, not `LocalVertexFactory`/static mesh -
worth checking, not assuming, since the two pack interpolants differently in principle).

- **Confirmed against `GpuSkinVertexFactory.ush`'s `CalcTangentToWorld`**: `TangentToWorld0 =
  TangentToWorld[0]` (Tangent) and `TangentToWorld2 = float4(TangentToWorld[2],
  Input.TangentZ.w * Primitive.InvNonUniformScaleAndDeterminantSign.w)` (Normal, with the handedness
  sign packed into `.w`).
- **Confirmed against `MaterialTemplate.ush`'s `AssembleTangentToWorld`**: `TangentToWorld1 =
  cross(TangentToWorld2.xyz, TangentToWorld0) * TangentToWorld2.w` (the reconstructed Bitangent), then
  `half3x3(TangentToWorld0, TangentToWorld1, TangentToWorld2.xyz)` as the full TBN matrix - which
  matches the decompiled math component-for-component: `_17 = cross(_15, _16) * _15.w` and
  `_18 = TangentNormal.z*_15 + TangentNormal.x*_16 + TangentNormal.y*_17`.
- **Deliberately not keyed off a fixed `TEXCOORD10`/`TEXCOORD11` register**: interpolator slot
  assignment is compiler-packed per material (however many UV channels/other interpolants a specific
  material also declares shifts which register everything after them lands on), unlike the
  View/Primitive uniform buffers which are one fixed global struct - so hardcoding "TEXCOORD10 is
  always Tangent" the way the row tables do for View/Primitive would be a real guess for a different
  material. Instead, Tangent/Normal are identified by their *structural role* in the cross-product
  reconstruction itself, which is identical regardless of which register they end up bound to.
- **Raw DAG dumped first** (`PixelExpressionNode`/`PixelExpressionArg`'s actual `Op`/`Swizzle`/`Negate`
  fields via a throwaway recursive printer in the test harness) rather than inferring the exact
  swizzle/negate shape from the printed text, since printed text can hide which side of a subtraction
  actually carries the `Negate` flag.
- **`TryMatchCrossProduct`**: matches `mad(A.yzx, B.zxy, -mul(B.yzx, A.zxy))` - the only way to express
  a *complete* 3-component cross product as one SIMD swizzle/mad/mul triple (not an arbitrary rotation
  among several), so matching the literal `"yzx"`/`"zxy"` swizzle strings isn't narrower than the real
  instruction shape.
- **`TryMatchBitangent`**: confirms the outer `mul`'s second operand is the cross product's *own first
  operand* (by reference identity) read again with a bare `.w`/`.www` swizzle - exactly
  `TangentToWorld2.w`, ruling out a coincidentally cross-shaped node scaled by something unrelated.
- **`ScanForTangentBasis`**: a one-time pre-pass (before the CSE/naming pass) walking every pin's DAG
  for the first `TryMatchBitangent` match, seeding `PrintCtx.TangentNode`/`VertexNormalNode`/
  `BitangentNode`; a material with no tangent-space normal map (no TBN reconstruction at all) leaves
  all three null and nothing gets mislabeled.
- **Naming collision avoided deliberately**: the reconstructed vertex normal is named `VertexNormal`,
  not `Normal` - the bare identifier `Normal` is already the literal assignment target for the
  material's `Normal` *output pin* (`Normal = (PixelNormalWS * 0.5 + 0.5);`), and `ctx.UsedNames` isn't
  seeded with output-pin identifiers, so a hoisted variable literally named `Normal` would silently
  collide with it.
- **Verified against real output**: `M_FN_Character_MASTER` (and `F_MED_Body_Grave`, the instance) now
  read `var VertexNormal = TEXCOORD11 (v1); var Tangent = TEXCOORD10 (v0); var Bitangent =
  ((VertexNormal.yzx * Tangent.zxy + -(Tangent.yzx * VertexNormal.zxy)) * VertexNormal.www);` feeding
  directly into the already-verified `PixelNormalWS`/`CameraVector` chain, with no regressions to
  either. Re-checked `arrows_animated` (no tangent-space normal map): correctly zero
  `Tangent`/`VertexNormal`/`Bitangent` output, confirming the recognizer doesn't over-fire there
  either.

### `lerp(a, b, t)` recognition for `mad(t, b-a, a)`-shaped math

Requested directly: a lot of the output was long `(T * (-A + B) + A)`-shaped arithmetic that's just
HLSL's `lerp` intrinsic in its compiled form. Unlike every other idiom in this file (`CameraVector`,
`PixelNormalWS`, the View/Primitive row names, the TBN basis), **this one isn't tied to one specific
engine source location** - `lerp(a,b,t) = a + t*(b-a)` is a generic mathematical identity, true
regardless of whether the material graph used a dedicated `Lerp` node or hand-built the same blend
from `Add`/`Subtract`/`Multiply`. That actually makes it lower-risk, not higher: rewriting
`mad(t, b-a, a)` as `lerp(a, b, t)` never claims anything about which graph node produced it or what
it semantically represents - it's provably the same expression, just shorter to read, the same class
of transformation as a peephole optimizer would do, not an inference.

- **`TryMatchLerp`** (`PixelShaderDecompiler.cs`, checked from the `mad` case in `PrintInstruction`
  since HLSL's `lerp` always compiles to one `mad` instruction, never a dedicated opcode): requires
  the `mad`'s third (additive) operand `A` to appear, completely unmodified (same node reference, same
  swizzle, no extra negate/abs), negated inside the *other* operand's own `add(-A, B)` subtraction -
  checked with either `mad` argument order, since multiplication is commutative and the compiler is
  free to place `t` or the subtraction result first. Deliberately conservative: a subtraction result
  that carries its own extra swizzle/negate/abs is left as fully expanded arithmetic rather than
  guessed at, and `A` itself must be unnegated/non-absolute at the outer `mad` too - anything less
  exact doesn't match, it just isn't simplified (never a wrong simplification).
- **Verified against real output**: `M_FN_Character_MASTER`/`F_MED_Body_Grave` now read, e.g.,
  `var _22 = lerp(Diffuse, (...skin-shaded computation...), HumanSkin);` (previously a much longer
  `(HumanSkin * (-Diffuse + (...)) + Diffuse)` expression) - semantically sensible too, since blending
  raw diffuse against a computed subsurface-scattering result by a `HumanSkin` factor is exactly the
  kind of thing a "Human Skin" toggle/blend parameter would do. Several more instances matched
  elsewhere in the same file. Re-ran the full harness afterward: zero exceptions; `arrows_animated`
  (a material with no lerp-shaped math at all) correctly produced zero `lerp(...)` output, confirming
  the recognizer doesn't over-fire.

## CUE4Parse fixes made this session

The shared shader code library (`.ushaderbytecode`) — needed to actually fetch a shader's compiled
DXBC bytecode by `OutputHash` — **did not deserialize at all** before this session for pre-4.25 pak
games (Fortnite 10.40 included): `FShaderCodeArchive.cs`'s branch for `archiveVersion == 1 &&
!bIsIoStore` was a dead TODO stub, leaving `SerializedShaders` null.

Fixed by porting (from the sibling project) and verifying compilation against our CUE4Parse:
- **`CUE4Parse/CUE4Parse/UE4/Shaders/FLegacyShaderCodeArchive.cs`** (new, 57 lines) — the actual
  pak-cooked shader library parser: `Dictionary<FSHAHash, FShaderCodeEntryLegacy>` (hash → offset,
  size, uncompressed size, frequency) followed by the raw code blob; `TryGetCode(hash)`
  decompresses (Zlib) on demand.
- `FShaderCodeArchive.cs` — wired the dead branch to `new FLegacyShaderCodeArchive(Ar)`.
- `FIoStoreShaderCodeArchive.cs`, `FSerializedShaderArchive.cs`, `FShaderTypeHashes.cs` — small
  consistency syncs from the sibling project (version-gated hash lengths for GAME_UE5_8+,
  correctly-typed bitfield accessors). Not required for the UE4.23 target but harmless and keeps
  the fork in sync.

`FUniformExpressionSetLegacy`'s `PreVirtualTexture` branch (`LegacyShaderMap.cs`) was silently
misreading `ParameterCollections` as always-empty for every pre-4.25 material, regardless of whether
it actually referenced an MPC — see the "Material Parameter Collection identification" section above
for the full root-cause trace (a missing reserved-array read that the real engine always serializes
just before `ParameterCollections`, per `FUniformExpressionSet::Serialize`). Fixed by reading and
discarding that reserved array before `ParameterCollections`, matching engine order exactly.

Verified end-to-end by the user opening/exporting the shared shader library as JSON in the running
app — confirmed working (real hash → offset/size/frequency entries, matching
`FShaderCodeEntryLegacy` exactly).

## UI wiring

- `FModel/ViewModels/CUE4ParseViewModel.cs` — `DecompileShader(GameFile entry, bool AddTab = true)`,
  mirrors the existing `Decompile()` (blueprint) method's tab-management pattern. Runs Layer 2
  first (`PixelShaderDecompiler.DecompilePixelShaderToPseudo`), then always appends Layer 1
  (`material.DecompileShaderToPseudo()`) as a cross-check section.
- `FModel/ViewModels/Commands/RightClickMenuCommand.cs` — new `EShowAssetType.DecompileShader`,
  `"Assets_Decompile_Shader"` trigger string.
- `FModel/Views/Resources/Controls/ContextMenus/FileContextMenu.xaml` — new "Decompile Shader" menu
  item next to "Decompile Blueprint", enabled via `ItemCategoryCondition Category="Material"`
  (matches `UMaterial`/`UMaterialInstance`, both map to `EAssetCategory.Material`).
  `FModel/Views/SearchView.xaml` — same, in both context-menu locations there.

## Bugs found and fixed this session (chronological, each verified against real game data)

1. **`cbrow` reads from non-Material buffers showed as `/* missing cb source */ 0`** — discarded
   the analyzer's own `Detail` label (e.g. `"View cb2[5]"`) for reads from the View/Primitive
   uniform buffers. Fixed to print `/* {Detail} */ 0` when `Source` is null but `Detail` isn't.
2. **`phi` nodes silently dropped the "Else" branch and printed `(A : B)` with no `?`** — the
   analyzer's `MergeBranches` always emits exactly 3 args `[Condition, Then, Else]`; the printer
   only used the first two and used `:` with no ternary operator. Fixed to
   `(Condition ? Then : Else)` using all 3 args.
3. **No CSE / uncontrolled duplication** — a shared subtree printed its full text at every
   occurrence; fixed with the `CountRefs`/`AssignNames`/`Ref` pass (88.8KB single line → 56
   lines/3.4KB on `M_UI_Line-V`).
4. **CSE temp-variable names (`t0`, `t1`, ...) collided with real DXBC register names** — SM5
   disassembly genuinely uses `t#` for texture/SRV registers (confirmed: a `sample` node's `Detail`
   can literally read `"sample_l — t1 (engine resource)"`, meaning register `t1` — nothing to do
   with a hoisted variable that happened to also be named `t1`). Renamed the CSE prefix to `_N`.
5. **Bare numeric literals (`var t0 = 0;`) were needlessly hoisted** — naming a literal `0` is no
   clearer than inlining it. Fixed: `AssignNames` skips hoisting `"imm"`-op nodes.
6. **Ambiguous texture-channel reads investigated** — checked whether `sample(...)` printed with no
   visible channel suffix was silently dropping information. Added a `ChannelMap` diagnostic to the
   test harness and cross-checked the raw DXBC disassembly text directly. Conclusion: the omission
   is correct in every case checked (see "Verified findings").
7. **`DecompilePixelShaderToPseudo` only ever analyzed the *first* `LoadedMaterialResources`
   entry** — a material can have independently-indexed uniform expression arrays per quality level
   (in `M_FN_Character_MASTER`, uniform-scalar-index `30` means `DistanceRim_Power` in the Low
   quality shader map and `Rim_Light_Overall_Boost` in the High quality one — completely different
   parameters at the same index in different resources). A scalar used only in a higher-quality
   permutation (common: expensive rim-light/fresnel refinements get statically stripped at lower
   quality) would never appear. Fixed: now loops every resource with a `LoadedShaderMapLegacy`.
8. **Texture names unresolved / `UMaterial.ReferencedTextures` empty pre-4.25** — see "Texture
   identification" above for the full chain; fixed by replicating `AppendReferencedTextures`.
9. **Nested `/* */` comments** in the texture verification suffix — fixed with `[Texture N]`
   bracket styling instead of a second comment.

## Verified findings (real data, cross-checked against editor ground truth and/or engine source)

- **`M_UI_Line-V`**: the reconstructed `Emissive_Color`/`Opacity` math matches
  `Data/With-Editor-Data/M_UI_Line-V.json`'s node graph exactly, down to literal constants (0.475,
  0.25, 8, 48, -0.5 all land exactly where the `Abs→Subtract→Multiply→OneMinus→Clamp→DotProduct`
  chain says they should). Also discovered *why* `SelectionColor`/`RefractionDepthBias` appear in
  the uniform-expression section despite not being in the authored graph at all: `SelectionColor`
  is Unreal's own editor-only "tint the mesh when selected" feature, compiled into every material
  and blended into `EmissiveColor` at compile time; `RefractionDepthBias` is a property every
  `BLEND_Translucent` material gets a uniform slot for regardless of graph content. The shader
  permutation analyzed (`TBasePassPSFSimpleNoLightmapLightingPolicySkylight`) turned out to contain
  **two branches**: the real material math, and a completely unrelated
  editor-viewport-selection-outline checkerboard overlay (`dot3(worldPos, 0.577) * 0.002` → frac →
  compare → pick hardcoded blue/cyan) — the decompiler correctly separated the two without being
  told to.
- **`arrows_animated`**: `Panner` (`SpeedX=-0.35`) reconstructed exactly as
  `cb0[136].z * -0.35 + U` (Time × speed + base UV); `TextureCoordinate` (`UTiling=1.25`)
  reconstructed exactly as `Const(1.25, 1, 0, 0)`.
- **The `linear_gradient` texture channel question**: the graph specifies `TextureSample_4` reads
  `.A` (Alpha, `OutputIndex=4`), but the compiled shader's `sample r1.x, ..., t3.xyzw, s2`
  instruction (confirmed via raw DXBC disassembly, and via a `ChannelMap=[0,1,2,3]` identity dump)
  reads channel **X (Red)** with no resource-level swizzle. Not a decompiler error: the texture's
  actual import setting is `CompressionSettings=TC_Grayscale` (confirmed by loading the texture
  asset directly and reading `UTexture.CompressionSettings`) — a grayscale texture has R≡G≡B≡A by
  construction, so the compiler's substitution of `.R` for the authored `.A` is a legitimate,
  provably-safe optimization. The decompiler's output (showing the *actual* compiled read) is more
  precise than the naive graph-literal reading, not less.
- **`M_FN_Character_MASTER`**: confirmed a scalar parameter (`Rim_Light_Overall_Boost`) is real and
  used, but only in the High-quality shader map (bug #7 above — index 30 means `DistanceRim_Power`
  in Low, `Rim_Light_Overall_Boost` in High). Confirmed a different scalar
  (`EQ_MaxIntensityTreble`, likely part of an audio-reactive cosmetic effect) is genuinely **dead**
  in this compiled permutation — checked against every pixel shader pin (`Normal`/`Metallic`/
  `Specular`/`Roughness`/`Base Color`/`Ambient Occlusion`/`Emissive Color`) in both quality levels,
  and every other shader stage (`ShaderStages`; the Vertex Shaders group reports
  `bindsMaterial=False` — the compiled vertex shader doesn't even bind the Material constant
  buffer). Almost certainly a Static Switch–gated feature that's off by default in this master
  material; not a decompiler gap. Also where the texture-resolution fallback (point 5 above) was
  confirmed firing correctly rather than guessing.

## Known limitations / explicitly out of scope

- **UE 4.25+ preshader bytecode** (`FMaterialPreshaderData`, used by `LoadedShaderMap` instead of
  `LoadedShaderMapLegacy`) is not decoded by either layer. See Layer 1's note above for why the
  sibling project's decoder for it wasn't a shortcut here either.
- **DXIL/UE5 SM6 path** (`MaterialDxil.cs`, `AnalyzeDxil`) was ported but never exercised.
- **Shader-permutation selection is not domain-aware.** `AnalyzeLegacy`'s candidate selection picks
  a "representative" base-pass-shaped shader rather than the permutation actually used for the
  material's real render path (e.g. `M_UI_Line-V` is an `MD_UI`/Slate material, but the shader
  analyzed was `TBasePassPS...Skylight`, a deferred-mesh-rendering permutation it also happens to
  compile). In every case checked this session the underlying material graph math was identical
  across permutations (generated from the same HLSL template), so this hasn't caused wrong output
  — but the printed `ShaderTypeName` header may not describe the shader actually used when the
  asset renders in its intended context.
- **Texture resolution doesn't recurse into `MaterialFunctionCall`** — see "Texture identification"
  point 5. Falls back honestly, never guesses.
- **No control-flow reconstruction beyond if/else.** Loops/switches in the source DXBC are not
  restructured into higher-level pseudocode; the ported analyzer's control-flow-depth tracking only
  affects indentation in the raw disassembly text, not `PinExpressions`.
- **`MaterialPixelShaderAnalyzer`'s own documented caveats** (unchanged from the ported code):
  dynamically-indexed constant buffers/indexable temps become explicit `opaque` leaves rather than
  guesses; UV/coordinate provenance feeding a texture sample is not chased in the disassembly
  slice; more than one untraceable source merging into one value becomes an opaque "Pixel Shader
  Math" combiner rather than invented arithmetic; large shaders cap at 20,000 disassembly lines /
  2,500 expression-DAG nodes.

## Pre-4.20-era (UE4.19) support

Added in a later session to support an older Fortnite build. **Layer 1 works. Layer 2 (DXBC) does
not, and is a substantially larger undertaking than it first appears — read "Layer 2 status" before
picking this back up.**

### Environment

- Build: `V:\.builds\1.10\FortniteGame\Content\Paks`
- Game version: `EGame.GAME_UE4_19`
- AES key: `0x79323938716A53623131354E71513341676164333044576E3251597254493843`
- Target asset used throughout:
  `FortniteGame/Content/Athena/Prototype/Terrain/M_Athena_Fortress_Skybox_LF_Spinning_2` (a
  `MaterialInstanceConstant`; its base material is `M_Athena_Fortress_Skybox_LF_Spinning`)
- Engine source for ground-truth checks: `D:\Unreal Engine\UE_4.19\Engine\Source` — a **real, exact**
  match for this build's engine version (unlike the 10.40/4.22-vs-4.23 approximation above), though
  Fortnite's internal branch still shows small deviations from it in a couple of places (see
  "unresolved" below) — likely Epic's own fork carrying unreleased/backported changes.

### Why this needed a whole separate code path

Everything in "Architecture" above (both layers) was written against confirmed UE 4.22/4.23 formats.
At 4.19, almost nothing has the same byte layout — not because the *concepts* differ, but because
the exact struct fields serialized changed release to release, and this reader has to match the
byte layout exactly or it misreads garbage:

- **The `.uasset`/`.uexp` wrapper is different**: pre-~4.22, `FMaterialResource::SerializeInlineShaderMap`
  has no `FMaterialResourceProxyReader`-style local name-map/locs preamble at all — `ReadFName`/
  `ReadFString` go straight to the archive's own (global, per-package) name table. See
  `FMaterialResourceProxyReader.CreatePassthrough` / `IsPassthrough` — a second constructor path
  added specifically for this, selected automatically as a fallback (see "Safe fallback" below).
- **`FMaterialShaderMapId::Serialize` at 4.19 is enormous** compared to the simplified cooked form
  used from ~4.22 on (just QualityLevel+FeatureLevel+Hash) — it inlines a full `FStaticParameterSet`,
  `ReferencedFunctions`, `ReferencedParameterCollections`, and three dependency arrays, none of which
  are modeled. Instead of modeling them, `FMaterialShaderMapIdLegacy.DeserializeVeryLegacy` skips the
  fixed-size fields it knows (Usage/BaseMaterialId/QualityLevel/FeatureLevel) then anchor-scans
  forward for the `ShaderPlatform`(int32)+`FriendlyName`(FString) pair that always follows — using an
  **exact match** against the expected FriendlyName (not a generic "looks like a string" heuristic)
  to make a false-positive practically impossible. See `TryScanForShaderPlatformAndFriendlyName`.
  - **The expected FriendlyName is the root material's name, not the instance's own name.**
    `FMaterialResource::GetFriendlyName()` (`MaterialShared.cpp:1073`) returns `GetNameSafe(Material)`
    — for a `UMaterialInstance`'s own static-permutation resource, `Material` is the root `UMaterial`
    it was compiled from, walked through however many levels of instancing. See the `Parent`-chain
    walk added in `UMaterialInstance.Deserialize` right before the passthrough retry.
- **`FUniformExpressionSet::Serialize` (the actual material-graph uniform expression tree) has a
  different array set** at 4.19 (`MaterialUniformExpressions.cpp:102`): no volume-texture array, no
  reserved "2D texture array" slot (both added later), and four now-removed "PerFrame"/"PerFramePrev"
  arrays trail ParameterCollections. New `ELegacyShaderMapProfile.UE4_19` branch in
  `FUniformExpressionSetLegacy`'s constructor.
- **`FMaterialCompilationOutput::Serialize`** is nine flat 4-byte bools with no leading
  `UsedSceneTextures`/estimate fields (`MaterialShared.cpp:346`) — but real cooked data only has 8
  fields' worth of bytes before `DebugDescription`, one short of the 9 in source (see "unresolved,
  not fatal" below). Handled by reading the first few reliably-positioned bools, then reusing the
  **same anchor-scan technique** as `SkipToDebugDescription` (searching for `"Compiling <name>: "`)
  instead of trying to guess the exact remaining byte count.
- **Individual uniform expression types can carry undocumented extra bytes.** Two structurally
  identical `FMaterialUniformExpressionVectorParameter` instances (both named `SelectionColor`) were
  observed with *different* trailing byte counts (0 extra vs. 5 extra) — see "unresolved" below. Not
  a fixed struct-layout difference; something conditionally present that isn't modeled. Solved
  generically rather than precisely: `ResyncToNextExpressionAnchor` runs between every array element
  for the UE4_19 profile, checking whether the current position is already a valid, registered
  `FMaterialUniformExpressionType` name (8-byte FName, number 0, name starting with
  `"FMaterialUniformExpression"`); if not, it scans forward a bounded window (64 bytes) for the next
  position that is, and resyncs there. This is the same "validate, then anchor-scan if wrong"
  philosophy as the rest of this format's handling — it doesn't require knowing *what* the extra
  bytes are, only that a genuine anchor exists to recover from them.
- **Custom-version-based Game fallback tables were wrong for this build's `GAME_UE4_19` bucket.**
  `FRenderingObjectVersion.Get`/`FReleaseObjectVersion.Get`'s per-Game guess tables (used when a
  package carries no explicit custom-version entry — true here, since Fortnite packages are
  "unversioned" in the `bUnversioned` sense and never carry real per-package custom-version lists
  regardless of era) both guessed a value *too high* for `GAME_UE4_19`, making
  `UMaterialInstance.Deserialize`'s native `FStaticParameterSet` block get skipped entirely (wrong
  `FRenderingObjectVersion` bucket) and then misread `FStaticMaterialLayersParameter` data that
  doesn't exist yet at this patch (wrong `FReleaseObjectVersion` bucket). Both fixed by changing
  **only** the `GAME_UE4_19` bucket (i.e. `< EGame.GAME_UE4_20`) to the same value as the `< GAME_UE4_19`
  bucket — deliberately not touching any other bucket, since other titles/patches tagged
  `GAME_UE4_19` were not investigated and might genuinely need the higher value. This is the one
  compatibility-relevant change outside the passthrough-gated code paths; it's still zero-impact for
  10.40 (`GAME_UE4_23`, a completely different bucket).
- **There is no per-shader end-offset in `TShaderMap::SerializeInline` at 4.19** (`Shader.h:1841-1846`:
  `Ar << Type; Shader = SerializeShaderForLoad(...)` — no skip value at all). The int64
  "relative-to-`OffsetToFirstResource`" end-offset this reader relies on for 4.23-era unknown-shader
  recovery **does not exist** in this format; it was added later specifically so unknown shader types
  could be skipped safely. See "Layer 2 status" below — this is the crux of why Layer 2 doesn't work.

### Safe fallback design (compatibility with 10.40 and everything else)

Every new UE4.19 code path is reached *only* on the passthrough reader (`FMaterialResourceProxyReader
.IsPassthrough`) or the new `ELegacyShaderMapProfile.UE4_19` enum value, both of which are only ever
selected after the *existing* (4.22+/4.23) parse attempt throws — i.e. this is a pure fallback, never
taken for a title whose current format already works. `UMaterial.Deserialize`/
`UMaterialInstance.Deserialize` try the current format first, unchanged; only on exception do they
retry from the same saved position with `usePassthrough: true`, clearing `LoadedMaterialResources`
first to avoid partial contamination. Verified: 10.40's `M_FN_Character_MASTER` still decompiles
identically after all these changes (see test harness section).

### Layer 2 status: fully working for all 6 quality/feature-level resources of the test asset

Once Layer 1 (`MaterialCompilationOutput`, including the real uniform expression tree) parses
successfully, `FMaterialShaderMapLegacy.Deserialize` moves on to `Shaders = SerializeInline(Ar)` — the
compiled-shader (DXBC) section, needed for Layer 2 pixel-shader reconstruction. This was chased over
several long sessions across two root causes (below) and now produces full, richly-detailed
reconstructed pixel shaders — real texture sampling, UV-animation math, distance-based contrast
blending — for **all 6** of `M_Athena_Fortress_Skybox_LF_Spinning`'s quality/feature-level resources
(Low/SM5, Epic/SM5, High/SM4_REMOVED, ×2 for the base material and the instance), not just the
trivial Low-quality fallback (`Emissive_Color = max((Color(0.6, 0.6, 0.6, 0.6) + SelectionColor.rgb),
Const(0, 0, 0, 0));`) originally found. Every named parameter's value and the overall expression shape
matches the 10.40 (4.23-era) decompile of the same asset exactly (period-appropriate constant
differences aside — e.g. Low quality's `0.6` here vs. `1` on 10.40, confirmed a genuine earlier-build
value via Layer 1's own independent uniform-expression dump, not a parsing error). In order of
discovery:

1. **Missing per-shader end offset (the actual root cause of most of what follows).** UE_4.19's
   `TShaderMap::SerializeInline` DOES write 4 bytes between the type name and every shader's own fields
   — it was missed on first pass because the wrapper visible at `Shader.h:1841-1846` only shows
   `Ar << Type; Shader = SerializeShaderForLoad(...)`, making it look like there's no skip mechanism at
   all. The offset is written one level down, inside `SerializeShaderForLoad`/`SerializeShaderForSaving`
   (`Shader.h:1713-1772`): `int32 SkipOffset = Ar.Tell(); Ar << SkipOffset; ...
   CurrentShader->SerializeBase(Ar, ...); int32 EndOffset = Ar.Tell(); Ar.Seek(SkipOffset); Ar <<
   EndOffset;` — the same "placeholder overwritten with `Ar.Tell()` at save time" pattern already known
   from `FVertexFactoryParameterRef`'s own skip offset. Missing this field meant *every* UE4_19 shader's
   own fields (starting with `FMaterialShader::Serialize`'s `MaterialUniformBuffer`, the very first
   thing read) were being read 4 bytes early — garbage `BaseIndex` values, or an outright "Invalid bool
   value" throw when the misread bits didn't decode as 0/1. This one fix cascaded into correcting two
   things that had previously been (wrongly) explained as independent findings:
   - The "`FDebugUniformExpressionSet` is 22 bytes not 24" empirical patch (previously point 6 here) was
     never a real field-size difference — it was compensating for accumulated drift caused by this same
     missing 4 bytes interacting with `ParameterCollectionUniformBuffers`'s own (also-misread) count.
     Once the real bug was fixed, the 22-byte skip started overshooting into the *next* shader's data;
     reverted to the vanilla 24-byte, 6×`int32` model.
   - The "`InstanceCount`/`InstanceOffset`/`VertexOffset` don't exist" finding (previously point 7)
     turned out to be a *separate, still-real* finding — re-confirmed after the above two fixes:
     restoring those 18 bytes still overshoots `VertexFactoryTypeName` by exactly their own length, so
     this Fortnite branch genuinely predates them.
   The raw offset value read here does not reproduce a usable position for this cook (tried both
   absolute and `OffsetToFirstResource`-relative interpretations; neither lands anywhere near the real
   shader boundary, independently confirmed via the anchor scan below finding the true end thousands of
   bytes earlier) — so the 4 bytes are consumed (fixing the alignment) but not trusted for jumping.
   Recovery for shader types this reader can't parse directly still goes through the anchor scan:
   `ReadUnknownTypeUnbounded` scans forward (bounded to 128KB) for the shader's own type name
   reappearing 76 bytes before a tail that deserializes cleanly (same anchor idea as
   `ReadUnknownTypeFromTail`, just without a known end position to bound the scan or prove
   uniqueness — accepts the first candidate whose tail fully validates).
2. **`FMaterialShader::Serialize` (`ShaderBaseClasses.cpp:447-487`) is a substantially different,
   larger structure at 4.19** than the 4.23-era one this reader already modeled for `TBasePassPS*`.
   Implemented field-by-field in `DeserializeMaterialShaderFront_UE4_19`, confirmed against every
   referenced struct's own `operator<<`/class declaration in engine source:
   `MaterialUniformBuffer`(6B) → `ParameterCollectionUniformBuffers`(array) →
   `FDeferredPixelShaderParameters` (**146 bytes fixed**: `FSceneTextureShaderParameters`'s 14
   `FShaderResourceParameter`s = 56B + `GBufferResources` 6B + **21** more `FShaderResourceParameter`s
   = 84B — the class's own field list reads as 20 at a glance because `CustomStencilTexture` trails on
   its own line in the header; recount from the `operator<<` body, not the field list, for any class
   like this) → `SceneColorCopyTexture(Sampler)`(8B) → `FDebugUniformExpressionSet` (24B, 6×`int32`,
   non-declaration field *order* — see `MaterialShader.h:90-99`) → inline `FRHIUniformBufferLayout`
   (`LayoutName` FName + `ConstantBufferSize` uint32 + a **single** `ResourceOffset` uint32, NOT an
   array — `RHIResources.h:196-203` — + `Resources` `TArray<uint8>`) → `DebugDescription` →
   `EyeAdaptation`(4B) → four "PerFrame"/"PerFramePrev" `TArray<FShaderParameter>`s →
   `InstanceCount`/`InstanceOffset`/`VertexOffset`(6B each). New primitives added:
   `FShaderResourceParameterLegacy` (4B: `BaseIndex`+`NumResources`, both `uint16`) and
   `FShaderParameterLegacy` (6B: `BaseIndex`+`NumBytes`+`BufferIndex`, all `uint16`).
3. **`FShaderResource::Serialize` at 4.19 (`Shader.cpp:480-499`) has no `FShaderParameterMapInfo` at
   all** — that's a later addition alongside the reflection-based parameter-binding system. Instead it
   reads a plain `uint32 NumTextureSamplers` in that exact spot. Confirmed by the function's own body
   containing no reference to a parameter map whatsoever.
4. **`FShader::SerializeBase` ends immediately after the inline `FShaderResource::Serialize` call**
   (`Shader.cpp:1011-1041`) — there is no trailing `FShaderParameterBindings` block (9 arrays +
   `RootParameterBufferIndex`) at 4.19 at all; that whole reflection-based system is a later addition.
   `DeserializeBaseTail` now skips this entire tail for the `UE4_19` profile.
5. **`ShaderResourceCodeSharing` is enabled for this build** (`FRenderingObjectVersion` index 15) even
   though the blanket `GAME_UE4_19` fallback used elsewhere (`VolumetricLightmaps`, index 20 — needed
   for the `FStaticParameterSet`/`MaterialAttributeLayerParameters` gating described above) sits above
   it — a single substitute value can't correctly gate both checks, so `FShaderResourceLegacy` now
   special-cases `UE4_19` directly at both `ShaderResourceCodeSharing` gates: skip the first inline
   `Code` read, then read `bCodeShared` (bool) and the *real* `Code` array after
   `uncompressedCodeSize` — confirmed correct by finding a real zlib-header-prefixed
   (`789C`) compressed bytecode blob exactly where this predicts it, whose length prefix (1193 bytes)
   exactly matched the gap to the next shader's anchor.

With all five fixed, **all material shaders of the test asset now parse correctly**, including real
decompressed DXBC bytecode. Mesh shaders (`TBasePassPS*` — the shader that actually matters for the
material's own per-pixel graph) needed several more rounds:

6. See point 1 above — the real `FDebugUniformExpressionSet` size is the vanilla 24 bytes; an earlier
   "22 bytes" reading was a byproduct of the missing end-offset, not a genuine field-size difference.
7. See point 1 above — `InstanceCount`/`InstanceOffset`/`VertexOffset` (18 bytes, the very end of
   `FMaterialShader::Serialize`) genuinely don't exist on this build, re-confirmed after point 1's fix.
   This Fortnite branch's `FMaterialShader::Serialize` predates GPU-instancing support for material
   shaders (these three fields feed `DrawIndexedInstanced` args in `FMeshMaterialShader::SetMesh`), so
   an early 1.10-era snapshot simply doesn't have them yet.
8. **`FVertexFactoryParameterRef`'s stored skip offset is never followed when the VF type resolves.**
   The write side (`VertexFactory.cpp:336-393`) always records `Ar.Tell()` there, but the *read* side
   only seeks to it as a fallback when `FindVertexFactoryType(name)` returns null; when the type
   resolves — as `FLocalVertexFactory` (by far the most common VF for static-mesh materials) always
   does — the engine instead reads that type's own `FVertexFactoryShaderParameters` subclass in place
   and never looks at the stored offset at all. Chasing the "absolute vs. `OffsetToFirstResource`-relative"
   interpretation of that offset was a dead end for exactly this reason — neither interpretation was
   ever going to be right for a resolved VF type. `FLocalVertexFactoryShaderParameters::Serialize`
   (`LocalVertexFactory.cpp:44-53`) is now read directly: `bAnySpeedTreeParamIsBound` (bool, 4B) +
   `LODParameter` (6B) + `VertexFetch_VertexFetchParameters` (6B) + four
   `VertexFetch_*BufferParameter`s (4B each) = 32B fixed. Unrecognized VF types still fall back to the
   two skip-offset interpretations (tried in order, each validated against `DeserializeBaseTail`'s own
   `TypeName`/`Target.Frequency` anchor) since a VF this reader doesn't special-case can't be read any
   other way yet.
9. **The four param-struct byte counts (134/66/70/78, all derived field-by-field from engine source and
   individually correct per struct — see point 2's sibling analysis) summed to 30 bytes more than what
   was actually on disk before point 1's fix.** `DeserializeTBasePassPS_UE4_19` resyncs onto
   `DeserializeBaseTail`'s own `TypeName`/`Target.Frequency` anchor within a small window (±64 bytes)
   around the computed tail position rather than trusting the raw sum, exactly like the unknown-type
   recovery paths already do — this safety net is left in place (see below) since the four struct sizes
   still aren't independently byte-verified, but with point 1 fixed the resync's own delta shrank
   drastically (no longer re-measured precisely after the fix; treat the old "consistently -30" figure
   as describing the pre-fix state only, not a currently-accurate number).

**`TBasePassPS*` now fully parses end-to-end AND produces a real, correctly-structured reconstructed
pixel shader body**, confirmed against `M_Athena_Fortress_Skybox_LF_Spinning`'s
`TBasePassPSFNoLightMapPolicy` (Quality=Low):
`Emissive_Color = max((Color(0.6, 0.6, 0.6, 0.6) + SelectionColor.rrr), Const(0, 0, 0, 0));` — matching
the "else" branch of the same shader/asset's 10.40 (4.23-era) decompile
(`max((Color(1, 1, 1, 1) + SelectionColor.rgb), Const(0, 0, 0, 0))`) with a plausible period-correct
constant difference (this is genuinely an earlier build of the same material, not a parsing artifact —
confirmed by Layer 1's own uniform-expression dump independently showing the same `0.6` constant).
Getting here also fixed, as a side effect, an earlier-documented cosmetic gap where `cb0[2]`-style
constant-buffer reads printed as a bare `0` comment instead of resolving to the named uniform
expression at that slot: that was downstream of `MaterialUniformBuffer` (and therefore the whole
constant-buffer-to-parameter mapping) reading garbage due to point 1's bug, not a separate bug in
`MapSinksToPins`/`PinSources`/`PinExpressions` resolution as previously suspected. The genuinely
separate, already-fixed `PixelShaderDecompiler.DecompileOneResource` issue (iterating only
`PinSources.Keys` instead of the union of `PinSources`/`PinExpressions`/`PinDisassembly` keys, so a pin
resolved only in `PinExpressions` was silently never printed) remains fixed and is unrelated to point 1.

**Safety net (still needed — several of the fixes above are validated only via anchor/position
resync, not exact byte modeling, so a shader this reader hasn't seen yet can still fail):** the
material-shaders array, the `MeshShaderMaps` loop, and the per-resource loop in
`UMaterialInterface.DeserializeInlineShaderMaps` all catch a `UE4_19`-profile parse failure
non-fatally. `SerializeInline`'s own per-shader loop (used by both of the above) now also catches a
mid-array failure and throws a `PartialShaderArrayException` carrying whatever shaders it parsed
before the bad one, so a single unrecognized permutation in a 7-shader VS+PS array doesn't lose the
other 4+ that parsed fine (an earlier version of this fix instead silently `break`-ed and let the
caller keep reading `numPipelines` etc. from the now-garbage position, which corrupted the rest of the
parse worse than not catching at all — the exception-with-partial-payload approach avoids that by
still stopping the caller at exactly the point of failure). `MeshShaderMaps` is built as a `List` and
only converted to an array at the end specifically so a partial failure never leaves `null` entries for
callers to trip over.

### If picking Layer 2 back up

Known open gaps:

1. **The reconstructed `Emissive_Color` is missing the outer conditional present in 10.40's decompile**
   (`_0.z ? _7.xyz : _6.xyz` there — an `OutOfBoundsMask`/debug-selection-color branch — vs. just the
   "else" side, `_6`, here). Not yet root-caused whether this is a genuine difference in this older
   build's compiled shader (plausible — different engine era, different codegen, possibly this branch
   simply didn't exist yet) or a remaining gap in this reader's own instruction decoding/branch
   reconstruction. Compare `NumInstructions` between the two builds' equivalent shader and/or dump the
   raw DXBC disassembly (`PixelShaderDecompiler.AnalyzeForDiagnostics`'s `Wiring.PinDisassembly`, already
   exposed by the test harness) to see whether the branch instructions are present in the bytecode at
   all before assuming either explanation.
2. **The material's other quality-level resources (2nd through 6th of 6) were being dropped entirely,
   and it was briefly — wrongly — reported that the higher quality level didn't exist in this cook at
   all. It does; this was a real bug, now fixed, plus one more real bug found immediately downstream.**
   - Root cause #1: `FMaterial::SerializeInlineShaderMap` (confirmed against `MaterialShared.cpp:712-760`)
     is `bCooked; if(bCooked){ bValid; if(bValid){ shader map } }` per resource, with **no per-resource
     end offset at all** — so once one resource's own total byte count is even slightly off (this
     reader's UE4_19 byte layout still isn't fully verified everywhere), every resource after it reads
     from an unrecoverable position, and the old code just gave up on the whole rest of the array
     (`UMaterialInterface.DeserializeInlineShaderMaps`, passthrough branch). Fixed by adding a resync:
     every resource of the same `UMaterial` shares an identical, verbatim-repeated `BaseMaterialId`
     GUID (previously read and discarded in `FMaterialShaderMapIdLegacy.DeserializeVeryLegacy` — now
     captured as `BaseMaterialIdFromVeryLegacyScan`), so on a per-resource parse failure,
     `UMaterialInterface.TryResyncViaBaseMaterialId` scans forward for that exact 16-byte sequence
     reappearing and resumes from there instead of giving up on every remaining resource.
   - Root cause #2, found immediately after #1 via that exact resync: the backward offset from the
     `BaseMaterialId` match to the resource's own start was wrong by 4 bytes — `candidateStart = matchPos
     - 8` assumed only `bCooked(4)+bValid(4)` precede the GUID, forgetting the `Usage(4)` field that
     `FMaterialShaderMapIdLegacy.DeserializeVeryLegacy` also reads (and discards) immediately before it.
     This meant every "successful" resync was landing 4 bytes late — reading the resource's real
     `bValid` flag as if it were `bCooked` (which happened to decode as a plausible value most of the
     time), and `Usage` (always observed as 0) as if it were `bValid` — so recovery always "succeeded"
     into a fake, empty resource instead of the real one right next to it. This is what produced the
     mistaken "confirmed: this quality level doesn't exist in the cook" conclusion — the resync was
     firing and reporting success, just onto the wrong 4-byte-shifted position every time. Fixed:
     `candidateStart = matchPos - 12`.
   - With both fixed, the material's other resources are real, `bValid=true`, cooked shader maps that
     start parsing correctly (matching `QualityLevel`/`FeatureLevel`, resolving the `FriendlyName`
     anchor, resolving several real `FMaterialUniformExpressionVectorParameter`/`ScalarParameter`
     nodes) — confirming the higher quality level genuinely is present in this 1.10 cook, exactly as
     expected from comparing against the 10.40 reference for the same asset.
   - **A third, separate bug was found and fixed: `FMaterialParameterInfo` itself predates the vanilla
     4.19-onward layout in this Fortnite branch.** The vanilla struct (`FName Name(8B) + TEnumAsByte
     Association(1B) + int32 Index(4B)`, 13B total, confirmed against `MaterialUniformExpressions.h:
     208-227`/`MaterialLayersFunctions.h:56-60`) does not match this build at all. Root-caused
     conclusively by cross-referencing the 10.40 (4.23-era) JSON dump of this exact asset: every single
     scalar/vector parameter there has `ParameterAssociation=2` (`GlobalParameter`) and
     `ParameterIndex=-1` - i.e. this era's materials never actually use per-layer indexing, so those two
     fields are meaningless overhead in the cooked data - and then finding each parameter's own real
     default value (exact bit-for-bit float matches - `Speed_1`=`1.368582`, `DistanceFunction`=`80000`,
     `Speed_2`=`7.003327`, etc.) sitting exactly **8 bytes** after that parameter's own `FName`, not 13.
     The real layout is just a plain `FName` (8B: name index + number - e.g. `Speed_2` is stored as base
     name `"Speed"` with `Number` encoding the `_2` suffix per `FName`'s own "stored as 1 more than
     actual" convention, `FName.cs:20-23`) with **no separate Association or Index field at all**;
     `ReadParameterInfo` now branches on `UE4_19` to read just `Ar.ReadFName().Text` and hardcode
     `ParameterAssociation = 2` / `ParameterIndex = -1` to match every real instance observed. Reading
     the vanilla 13-byte layout was overshooting by 5 bytes into the very next array element every
     time, which is exactly why every *other* scalar/vector parameter appeared to silently vanish from
     the array (`ReadExpressionArray`'s own resync recovered by skipping an entire extra element to find
     the next valid type name, rather than realigning within the current one) - and it explains, in
     hindsight, the session's own much earlier, never-resolved "`FMaterialUniformExpressionVectorParameter`
     sometimes has 0 extra bytes, sometimes 5" note: 5 bytes is exactly the vanilla-vs-real difference
     for this struct, so that was this exact bug being seen for the first time and dismissed as noise.
     Confirmed fixed end-to-end: `M_Athena_Fortress_Skybox_LF_Spinning`'s Epic and High-quality
     resources now produce full, richly-detailed reconstructed pixel shaders (real texture sampling,
     UV-animation math, distance-based contrast blending - not just the trivial "else"-branch constant
     Low quality falls back to), matching the shape and every named-parameter value of the 10.40
     reference for the same asset. Two related fixes were added while chasing this and remain useful
     independent of this specific bug: (a) the trailing-bytes resync also now runs *between sibling
     operands* inside a compound expression node (`FoldedMath`/`TrigMath`/`Min`/`Max`/`Clamp`/
     `AppendVector`/`Fmod`'s "A"/"B" or "X"/"Y" pairs, via
     `FMaterialUniformExpressionLegacy.ResyncBetweenOperands`) rather than only between top-level array
     elements, since a leaf parameter nested as an operand can carry the same kind of quirk; (b) a
     per-element `Log.Verbose` trace (type name + parameter name + position) remains in
     `ReadExpressionArray` as a low-cost breadcrumb for any future investigation of this kind.

### Additional VF types (found testing against `CharacterShader`, a skeletal-mesh master material)

`M_Athena_Fortress_Skybox_LF_Spinning`'s only VF type is `FLocalVertexFactory` (static mesh). Testing
against `FortniteGame/Content/Packages/Fortress_SharedMaterials/Base_Material/CharacterShader` (a
much larger, widely-shared skeletal character material) immediately hit several VF types that reader
had no case for at all, each one throwing and dropping the rest of that resource's material-shaders
array via the existing per-shader safety net. All confirmed and fixed by reading the actual
`ConstructShaderParameters`/`Serialize` bodies in `GPUSkinVertexFactory.cpp`:

- **`TGPUSkinVertexFactory<bool>`** (plain skeletal mesh, type name observed as
  `TGPUSkinVertexFactorytrue`/`...false` — the template bool is appended to the type name) uses
  `FGPUSkinVertexFactoryShaderParameters::Serialize` (`GPUSkinVertexFactory.cpp:496-501`): `PerBoneMotionBlur`
  (`FShaderParameter`, 6B) + `BoneMatrices` + `PreviousBoneMatrices` (2× `FShaderResourceParameter`, 4B
  each) = 14B fixed, no arrays.
- **`TGPUSkinMorphVertexFactory<bool>`** (morph-target skeletal mesh) — confirmed via
  `TGPUSkinMorphVertexFactory::ConstructShaderParameters` (`GPUSkinVertexFactory.cpp:744-748`) to
  construct the exact same `FGPUSkinVertexFactoryShaderParameters` as the plain variant, byte-for-byte
  identical — no separate case needed beyond matching the type-name prefix too.
- **`TGPUSkinAPEXClothVertexFactory<bool>`** (cloth-simulated skeletal mesh) extends the base with 6
  more fields (`GPUSkinVertexFactory.cpp:780-789`, field types confirmed from the class's own member
  declarations at `:854-859`): `ClothSimulVertsPositionsNormalsParameter` + `PreviousClothSimulVertsPositionsNormalsParameter`
  (`FShaderResourceParameter`, 4B each) + `ClothLocalToWorldParameter` + `ClothBlendWeightParameter`
  (`FShaderParameter`, 6B each) + `GPUSkinApexClothParameter` (`FShaderResourceParameter`, 4B) +
  `GPUSkinApexClothStartIndexOffsetParameter` (`FShaderParameter`, 6B) = 30B on top of the base 14B,
  44B total.
- **`FMeshParticleVertexFactory`** (mesh particles) was also encountered in this same asset and is
  *not* yet modeled — it still falls back to the generic two-skip-offset guess (untested, unlikely to
  work) and gets dropped by the safety net like any other unrecognized type. This did not block
  `CharacterShader`'s own `TBasePassPSFNoLightMapPolicy` reconstruction (a `FLocalVertexFactory`/GPUSkin
  permutation earlier in the same 33-shader array already succeeded), so it wasn't chased further, but
  add it the same way if a mesh-particle-specific permutation is ever the one that's needed.

Also discovered and fixed in the same pass: **`FMaterialUniformExpressionTime`/`RealTime`** (a
"Time"/"RealTime" material-graph node) were two more previously-unmodeled uniform expression types —
both are genuine no-op leaf nodes at this engine version (`MaterialUniformExpressions.h:62-116`, empty
`Serialize` bodies, zero fields) - trivial to add once identified as the actual cause (rather than a
byte-alignment bug) by the "unknown expression type" diagnostic already in place.

Beyond all of the above, `LegacyShaderMap.cs`'s light-map-policy byte layout is still only confirmed
for the plain `TUniformLightMapPolicy`-based policies (`BasePassPixelPolicyParamCountsUE4_19`, all
mapped to 1 field) — the three `FSelfShadowed*` policies use a different `PixelParametersType` not yet
confirmed for 4.19, and would need the same kind of investigation if one turns up as the shader a
future asset actually needs reconstructed.

## Test harness

`ShaderDecompileTest/` (standalone console project, `Program.cs` 193 lines) — mounts the real
Fortnite 10.40 build directly (paks path + AES key hardcoded at the top of `Program.cs`), loads a
named asset, and runs both decompiler layers plus several diagnostics: sample-node `ChannelMap`
dump, `PinSources` breakdown, per-pin/per-quality scalar-index reachability (including a
per-resource sweep to check whether a specific named parameter's index is reachable from a
specific pin, used for the `M_FN_Character_MASTER` investigation), raw DXBC disassembly per pin,
`ShaderStages` dump (other compiled stages + their `BindsMaterial`/`OutputValues`), and texture
pixel-format/name lookup by filename search. Built this way specifically so decompiler changes can
be verified against real game data without needing the full WPF app running or the user in the
loop for every check.

To run against a different asset: edit `assetPath` (and, for the parameter-tracing diagnostics, the
hardcoded parameter names / texture search terms) near the top of `Program.cs`, then:

```
cd D:\build\FModel\FModel
dotnet build ShaderDecompileTest/ShaderDecompileTest.csproj -c Debug
dotnet run --project ShaderDecompileTest/ShaderDecompileTest.csproj -c Debug --no-build
```

Note: the main FModel app and this harness both lock the same build output directories if run
concurrently against overlapping targets — not an issue in practice since they're separate
projects, but if `FModel.csproj` build fails with `MSB3027`/file-lock errors, it's because the WPF
app is currently running and needs to be closed first.

## Files touched this session (for a diff/review pass)

New:
- `CUE4Parse/CUE4Parse/UE4/Assets/Exports/Material/MaterialShaderDecompiler.cs` (342 lines)
- `CUE4Parse/CUE4Parse/UE4/Assets/Exports/Material/MaterialParameterCollectionResolver.cs`
- `CUE4Parse/CUE4Parse/UE4/Shaders/FLegacyShaderCodeArchive.cs` (57 lines)
- `FModel/ViewModels/PixelShaderDecompiler.cs` (536 lines, since grown further for MPC support)
- `FModel/ViewModels/MaterialPixelShaderAnalyzer.cs` (3189 lines, ported verbatim)
- `FModel/ViewModels/MaterialDxil.cs` (1056 lines, ported verbatim)
- `ShaderDecompileTest/` (whole project — `ShaderDecompileTest.csproj`, `Program.cs`)
- `Data/With-Editor-Data/`, `Data/Without-Editor-Data/` (ground-truth JSON, provided by user)
- `SHADER_DECOMPILER_NOTES.md` (this file)

Modified:
- `CUE4Parse/CUE4Parse/UE4/Shaders/FShaderCodeArchive.cs` (wired the dead legacy-archive branch)
- `CUE4Parse/CUE4Parse/UE4/Shaders/FIoStoreShaderCodeArchive.cs`,
  `FSerializedShaderArchive.cs`, `FShaderTypeHashes.cs` (sync fixes, not required for target build)
- `FModel/FModel.csproj` (new source files)
- `FModel/ViewModels/CUE4ParseViewModel.cs` (`DecompileShader` method)
- `FModel/ViewModels/Commands/RightClickMenuCommand.cs` (new trigger/action)
- `FModel/Views/Resources/Controls/ContextMenus/FileContextMenu.xaml`,
  `FModel/Views/SearchView.xaml` (new menu item, both locations)
