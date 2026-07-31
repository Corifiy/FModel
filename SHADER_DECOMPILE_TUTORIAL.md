# How to Decompile a Shader in FModel (Beginner Guide)

This guide shows you how to take a material out of a game and turn it into something you can
actually **read** — a rough "recipe" of what the material does, in code form.

You don't need to know shader programming. You need FModel, a game loaded, and about 5 minutes.

**Short version:**

1. Find a Material in the file list
2. Right click → **Decompile Shader**
3. Copy the result and ask an AI to rewrite it as simple, commented C++

---

## Before you start (one-time setup)

Open **Settings** (top left) → **General** tab, and turn ON these two toggles:

| Setting | Why |
|---|---|
| **Serialize Inlined Shader Maps** | This is the actual shader data. Without it, there is nothing to decompile. |
| **Decompile Blueprint to Pseudo C++** | This is what makes the **Decompile Shader** right-click option appear. |

Click **OK / Save**, then reload the game files (re-select your game or restart FModel) so the
materials get loaded again with shader data included.

> If you loaded the game *before* flipping "Serialize Inlined Shader Maps", the materials already in
> memory were read without their shader data. Reload, or you'll just get an empty result.

---

## Step 1 — Find a Material

In the folder tree or the search bar, look for a **Material** or **Material Instance**. They have the
material icon (a little sphere-ish icon), and their names usually start with:

- `M_` → a Material (the "master" one, e.g. `M_FN_Character_MASTER`)
- `MI_` / `MIC_` / a skin name → a Material Instance (a preset of a master material, e.g. `F_MED_Body_Grave`)

Both work. Anything else (meshes, textures, material **functions**, parameter collections) will not
work — see [Limitations](#limitations-read-this-before-you-get-confused).

**Tip:** if you're hunting for the material used by a character or weapon, open the mesh first, look
at its material slots, then go find that material by name.

---

## Step 2 — Decompile the Shader

**Right click the material → "Decompile Shader".**

That's the whole step. FModel opens a new tab named after the asset with **"Decompiled Shader"** next
to it, and shows C++-looking pseudocode.

If the option is **greyed out**, the file you selected isn't a Material or Material Instance.
If the option is **missing entirely**, go back and turn on "Decompile Blueprint to Pseudo C++".

To get the text out: click inside the code view, press **Ctrl+A** then **Ctrl+C**. Paste it into a
text file (or straight into an AI chat).

### What you're looking at

Roughly, from top to bottom, you'll see:

- A header line naming the shader and the quality level (you may see the **same material twice** —
  once for Low quality, once for High. That's normal, they're two versions of the same thing.)
- A long list of `var _0 = ...;` lines. These are intermediate steps. The `_0`, `_1`, `_2` names are
  **made up by FModel** to keep the code short — they are not the original node names.
- Named things where FModel could figure out the real name, for example:
  - `Diffuse`, `Normals`, `SpecularMasks` — texture parameters
  - `sample(...) /* T_F_MED_Grave_Body_D [Texture 0] */` — the actual texture asset being read
  - `[instance: ...]` — the value this **material instance** overrides on top of its parent
  - `CameraVector`, `PixelNormalWS`, `Tangent`, `Bitangent`, `VertexNormal` — recognized standard
    material inputs
  - `lerp(a, b, t)` — a blend, same as the Lerp node in the material editor
- At the bottom, the **output pins** — the same ones you'd see on the material node in Unreal:
  `Base Color`, `Metallic`, `Specular`, `Roughness`, `Normal`, `Emissive Color`,
  `Ambient Occlusion`. **These are the interesting lines.** Everything above them is just the math
  that feeds them.
- A second, shorter section listing "uniform" values — parameters and constant math the game
  computes on the CPU instead of in the shader. It's a cross-check, and often mostly engine
  plumbing you can ignore.

---

## Step 3 — Ask an AI to make it readable

The raw output is correct but dense. AI is very good at the next part: turning it into readable,
commented code that explains itself.

Copy the whole decompiled text and use a prompt like this:

```text
Below is decompiled pseudocode from an Unreal Engine material (extracted with FModel).
It came from compiled shader bytecode, so it's flat and machine-generated.

Please rewrite it as simple, readable C++-style code:
- Give the temp variables (_0, _1, ...) meaningful names based on what they do
- Add short comments explaining each block in plain English
- Group the code by which output it feeds (Base Color, Normal, Roughness, Emissive, etc.)
- At the end, summarize in a few sentences what this material actually looks like /
  does visually, and which parameters and textures control it

Important: do not invent math that isn't there. If something is unclear or unresolved
(like a bare cb0[66] or Texture[9]), say so instead of guessing.

<paste the decompiled shader here>
```

Good follow-up questions to ask it:

- "Which parameters would I change to alter the color / roughness / glow?"
- "Draw this as a node graph description, like the Unreal material editor would show it."
- "Is there a masking or blend step here, and what drives it?"
- "Explain the Normal output line by line."

**One warning:** AI will happily fill in gaps that don't exist. It's renaming and explaining, not
adding information. If it tells you something that isn't in the decompiled text, treat it as a guess.
When in doubt, `Ctrl+F` the original output for whatever it claims is there.

---

## Limitations (read this before you get confused)

The decompiled output is **not** the original material graph. It's the *compiled* shader, read back.
Some things are permanently gone, and that's not a bug in FModel — the game files genuinely don't
contain them.

### Everything is inlined — there are no functions

The biggest one. When Unreal compiles a material, it flattens the entire graph into one long stream
of math. So:

- **Material Functions are pasted in place.** If the artist used a function 5 times, its math appears
  5 times, and there's no marker showing where a function started or ended.
- There are **no function calls** in the output, ever. It's one flat blob per output pin.
- **Comment boxes, node names, node positions, and the layout of the graph are gone.** Nothing about
  how the artist organized their work survives compilation.
- Because of the flattening, a small graph can decompile into a surprisingly long wall of math.

### Only Materials and Material Instances have shader bytecode

- **Material** (`M_...`) ✅
- **Material Instance** (`MI_...`) ✅ — shows its parent's compiled shader, plus its own overridden
  values marked `[instance: ...]`
- **Material Function** ❌ — **material functions contain no shader bytecode at all.** They're just
  reusable recipes; they only ever get compiled *inside* a material that uses them. FModel greys the
  option out for them. If you want to see a function's math, decompile a material that uses it and
  look for the math inline.
- Material Parameter Collections, material editor data, meshes, textures ❌

### Things that got optimized away

The shader you're reading is what the compiler *actually produced*, after optimization:

- **Constants are pre-computed.** `2 * 3 + 1` was folded into `7` long before you saw it.
- **Static switches are baked in.** A feature toggled off by default is simply not in the code. So if
  you know a parameter exists but can't find it in the output — it's probably disabled in this
  variant, not missing from the decompile.
- **Unused parameters vanish.** A parameter nothing reads is dead code and gets removed.
- Some math gets rearranged into an equivalent-but-different form. It computes the same result, it
  just may not look like how the artist wired it.

### Things FModel honestly can't resolve

When it doesn't know, it says so instead of guessing. You may see:

- `Texture[9]` — a texture slot it couldn't map to a name (happens when the index depends on a
  material function's own textures)
- `/* FViewUniformShaderParameters cb0[66] */` or `View.Row137` — a value that comes from the engine
  (camera, time, screen size…) rather than the material
- `opaque` / "Pixel Shader Math" — a value with too many untraceable sources to reconstruct honestly
- Loops and switches are not rebuilt into nice code; only simple `if/else` becomes `(cond ? a : b)`

### Game / engine version support

- Games cooked on **older Unreal versions (roughly UE4.19 – UE4.24)** are the supported path.
- **UE 4.25+ and UE5** use a newer format that isn't decompiled yet. On those you'll get:
  `// No legacy (pre-4.25) shader data found for this asset.`
- That message can also mean the asset isn't a material, or that you forgot to enable
  **Serialize Inlined Shader Maps** and reload.

### And finally

This is **pseudocode for reading**, not source code. You can't paste it back into Unreal, compile it,
or rebuild the original material from it. It's for understanding how something was made — which
textures, which parameters, what math, in what order.

---

## Troubleshooting

| Problem | Fix |
|---|---|
| No "Decompile Shader" in the right-click menu | Settings → General → enable **Decompile Blueprint to Pseudo C++** |
| Option is greyed out | The selected file isn't a Material or Material Instance |
| "No legacy (pre-4.25) shader data found" | Enable **Serialize Inlined Shader Maps** and reload the game — or the game is too new (4.25+/UE5) |
| Output is tiny / only a few uniform lines | The compiled pixel shader wasn't reachable; you're only seeing the CPU-side values |
| Same material appears twice | Two quality levels (Low/High). Compare them — sometimes a feature only exists in High. |
| Wall of unreadable math | That's the inlining. Go to Step 3 and let an AI name and comment it. |
