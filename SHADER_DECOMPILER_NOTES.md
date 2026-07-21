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
