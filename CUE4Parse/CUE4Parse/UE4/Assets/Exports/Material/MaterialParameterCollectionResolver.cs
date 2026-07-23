using System;
using System.Collections.Generic;
using System.Linq;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Objects.UObject;

namespace CUE4Parse.UE4.Assets.Exports.Material;

/// <summary>
/// Resolves which Material Parameter Collection(s) a material's compiled shader map references,
/// and what each packed constant-buffer row in them means.
///
/// MPC parameter reads never appear in an FMaterialUniformExpression tree or preshader bytecode at
/// all - the engine compiles them as a direct HLSL read from a dedicated
/// "MaterialCollection0"/"MaterialCollection1" uniform buffer
/// (HLSLMaterialTranslator.h AccessCollectionParameter), entirely bypassing the uniform expression
/// system. FUniformExpressionSet(Legacy).ParameterCollections stores only the referenced
/// collection's UMaterialParameterCollection::StateId
/// (MaterialUniformExpressions.cpp SetParameterCollections, verified directly against the engine
/// source) - a bare GUID with no path back to the actual asset.
///
/// The only surviving link from that GUID back to a real asset is the editor-authored
/// MaterialExpressionCollectionParameter node itself: even in a cooked (non-editor) build, that
/// node's own Collection/ParameterName/ParameterId properties survive intact - the same
/// "properties survive, wiring doesn't" pattern already relied on for texture identification
/// (confirmed empirically: FN_Char_RimColor_v3's cooked CollectionParameter exports still carry a
/// fully resolvable Collection reference). Those nodes can live either directly in the material's
/// own package or inside a Material Function it calls (MaterialExpressionMaterialFunctionCall), so
/// this walks both, matching each discovered collection's own StateId against the shader map's
/// ParameterCollections entries.
///
/// Neither CUE4Parse's MaterialExpressionCollectionParameter/MaterialFunctionCall/
/// MaterialParameterCollection classes are dedicated C# types (all resolve to a generic UObject
/// backed by the same Properties bag), so every read below goes through the property-holder
/// GetOrDefault API rather than typed members.
/// </summary>
public static class MaterialParameterCollectionResolver
{
    /// <summary>One packed row of a collection's compiled uniform buffer (16 bytes / one float4).</summary>
    public sealed class CollectionSlot
    {
        /// <summary>Set when this whole row is one vector parameter's value.</summary>
        public string? VectorName;
        /// <summary>Set per-component (0=x, 1=y, 2=z, 3=w) when this row packs up to 4 scalar parameters.</summary>
        public readonly string?[] ScalarNames = new string?[4];
    }

    public sealed class ResolvedCollection
    {
        public required string Name;
        public required FGuid StateId;
        /// <summary>Keyed by row index (matches "MaterialCollectionN.Vectors[row]"/the DXBC constant-buffer row read).</summary>
        public required IReadOnlyDictionary<int, CollectionSlot> Slots;
    }

    /// <summary>
    /// Finds every Material Parameter Collection reachable from a material's own package or any
    /// Material Function it calls (bounded depth, cycle-safe), deduplicated by the collection's own
    /// StateId, each with a full row map built from ITS OWN complete parameter lists - not just
    /// whichever parameters the material happens to reference by name.
    /// </summary>
    public static IReadOnlyList<ResolvedCollection> FindReferencedCollections(UMaterialInterface material)
    {
        UMaterialInterface current = material;
        var guard = 0;
        while (current is UMaterialInstance instance && instance.Parent is UMaterialInterface parent && ++guard < 16)
            current = parent;
        if (current is not UMaterial baseMaterial || baseMaterial.Owner is not { } ownerPackage)
            return [];

        var found = new Dictionary<FGuid, ResolvedCollection>();
        var visitedPackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visitedCollectionPackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ScanPackage(ownerPackage, found, visitedPackages, visitedCollectionPackages, depth: 0);
        return found.Values.ToList();
    }

    /// <summary>Convenience: the one collection matching a specific "MaterialCollectionN" StateId from the shader map, or null.</summary>
    public static ResolvedCollection? Resolve(UMaterialInterface material, FGuid stateId)
        => FindReferencedCollections(material).FirstOrDefault(c => c.StateId == stateId);

    private static void ScanPackage(IPackage package, Dictionary<FGuid, ResolvedCollection> found,
        HashSet<string> visitedPackages, HashSet<string> visitedCollectionPackages, int depth)
    {
        if (depth > 8 || !visitedPackages.Add(package.Name)) return;

        for (var i = 0; i < package.ExportMapLength; i++)
        {
            if (new FPackageIndex(package, i + 1).ResolvedObject?.Object?.Value is not { } export) continue;

            if (export.ExportType.Contains("CollectionParameter", StringComparison.Ordinal))
            {
                if (export.GetOrDefault<FPackageIndex>("Collection").ResolvedObject?.Object?.Value is { } collectionObj)
                    AddCollection(collectionObj, found, visitedCollectionPackages);
            }
            else if (export.ExportType.Contains("MaterialFunctionCall", StringComparison.Ordinal))
            {
                if (export.GetOrDefault<FPackageIndex>("MaterialFunction").ResolvedObject?.Object?.Value is { Owner: { } functionPackage })
                    ScanPackage(functionPackage, found, visitedPackages, visitedCollectionPackages, depth + 1);
            }
        }
    }

    private static void AddCollection(UObject collectionObj, Dictionary<FGuid, ResolvedCollection> found, HashSet<string> visitedCollectionPackages)
    {
        if (collectionObj.Owner is not { } collectionPackage || !visitedCollectionPackages.Add(collectionPackage.Name))
            return; // already resolved this exact collection asset

        var stateId = collectionObj.GetOrDefault<FGuid>("StateId");
        if (found.ContainsKey(stateId)) return;

        var scalars = collectionObj.GetOrDefault<FStructFallback[]>("ScalarParameters", []);
        var vectors = collectionObj.GetOrDefault<FStructFallback[]>("VectorParameters", []);

        var slots = new Dictionary<int, CollectionSlot>();
        CollectionSlot GetSlot(int row) => slots.TryGetValue(row, out var s) ? s : slots[row] = new CollectionSlot();

        // Packing order verified against UMaterialParameterCollection::GetParameterIndex
        // (ParameterCollection.cpp:303): scalars pack 4-to-a-row in declaration order, vectors
        // start immediately after the last (possibly partial) scalar row.
        for (var i = 0; i < scalars.Length; i++)
        {
            var name = scalars[i].GetOrDefault<FName>("ParameterName").Text;
            GetSlot(i / 4).ScalarNames[i % 4] = name;
        }

        var vectorBase = (scalars.Length + 3) / 4;
        for (var i = 0; i < vectors.Length; i++)
        {
            var name = vectors[i].GetOrDefault<FName>("ParameterName").Text;
            GetSlot(vectorBase + i).VectorName = name;
        }

        found[stateId] = new ResolvedCollection { Name = collectionObj.Name, StateId = stateId, Slots = slots };
    }
}
