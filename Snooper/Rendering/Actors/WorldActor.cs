using System.Numerics;
using CUE4Parse_Conversion;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Actor;
using CUE4Parse.UE4.Assets.Exports.Engine;
using CUE4Parse.UE4.Assets.Exports.WorldPartition;
using CUE4Parse.UE4.Assets.Exports.WorldPartition.DataLayer;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.UObject;
using Serilog;
using Snooper.Rendering.Components.Transforms;
using ImGuiNET;

namespace Snooper.Rendering.Actors;

public class WorldActor : Actor
{
    private readonly Dictionary<string, string> _dataLayers = new();
    private readonly HashSet<string> _visibleDataLayers = [];
    private readonly HashSet<string> _compositionTiles = new(StringComparer.OrdinalIgnoreCase);

    public WorldActor(UWorld world, IProgress<string>? progress = null, CancellationToken ct = default) : base(world)
    {
        Components.Add(new SpatialComponent(null, "WorldRoot"));

        var level = world.PersistentLevel.Load<ULevel>();
        if (level == null) return;

        ProcessDataLayers();
        ProcessWorldPartition();

        var parents = new Dictionary<FPackageIndex, SpatialComponent>();
        var created = new List<LevelActor>();
        var length = level.Actors.Length;
        for (var i = 0; i < length; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (level.Actors[i] is not { } ptr || ptr.IsNull || !ptr.TryLoad<UObject>(out var data))
            {
                continue;
            }

            progress?.Report($"{world.Name} ... Actors {i + 1}/{length}");
            var a = new LevelActor(data, parents);
            if (a.RootComponent is not null)
            {
                created.Add(a);
            }
        }

        length = created.Count;
        for (var i = 0; i < length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var actor = created[i];
            progress?.Report($"{world.Name} ... Actor Hierarchy {i + 1}/{length}");

            var parent = actor.ProcessEnqueuedComponents(parents);
            if (parent is { IsNull: false })
            {
                if (parents.TryGetValue(parent, out var root))
                {
                    root.Actor?.Children.Add(actor);
                }
                else
                {
                    throw new Exception("Parent actor not found");
                }
            }
            else
            {
                Children.Add(actor);
            }
        }

        created.Clear();
        parents.Clear();

        DiscoverComposition(world, level, progress, ct);

        length = world.StreamingLevels?.Length ?? 0;
        for (var i = 0; i < length; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"{world.Name} ... Streaming Levels {i + 1}/{length}");
            Process(world.StreamingLevels![i]); // TODO: these fucking streaming levels can reference each other
        }

        void ProcessDataLayers()
        {
            if (!level.WorldDataLayers.TryLoad<AWorldDataLayers>(out var worldDataLayers)) return;

            progress?.Report($"{world.Name} ... Processing Data Layers");
            foreach (var instancePtr in worldDataLayers.DataLayerInstances)
            {
                if (!instancePtr.TryLoad<UDataLayerInstance>(out var instance)) continue;

                switch (instance)
                {
                    case UDataLayerInstanceWithAsset withAsset when withAsset.DataLayerAsset.TryLoad<UDataLayerAsset>(out var dataLayer):
                        _dataLayers[withAsset.Name] = dataLayer.Name;
                        if (withAsset.InitialRuntimeState is EDataLayerRuntimeState.Activated)
                            _visibleDataLayers.Add(withAsset.Name);
                        break;
                }
            }
        }
        void ProcessWorldPartition()
        {
            if (!level.WorldSettings.TryLoad<AWorldSettings>(out var worldSettings) ||
                worldSettings.WorldPartition == null || !worldSettings.WorldPartition.TryLoad<UWorldPartition>(out var worldPartition))
                return;

            progress?.Report($"{world.Name} ... Processing World Partition");
            Children.Add(new PartitionActor(worldPartition, _visibleDataLayers));
        }
    }

    private readonly record struct CompositionTile(string PackageName, FWorldTileInfo Info, ULevelStreaming? Streaming);

    private void DiscoverComposition(UWorld world, ULevel level, IProgress<string>? progress, CancellationToken ct)
    {
        if (world.Owner is not { Provider: { } provider } owner || !IsWorldCompositionEnabled(world, level))
            return;

        progress?.Report($"{world.Name} ... World Composition");

        var persistent = NormalizePackageName(owner.Name, provider);
        var tiles = new Dictionary<string, CompositionTile>(StringComparer.OrdinalIgnoreCase);
        var order = new List<CompositionTile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var streamingLevels = world.StreamingLevels;
        if (streamingLevels != null)
        {
            for (var i = 0; i < streamingLevels.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (streamingLevels[i] is not { } index || !index.TryLoad<ULevelStreaming>(out var streaming))
                    continue;

                Consider(StreamingPackageSource(streaming), streaming);
            }
        }

        var packageFile = owner.Name.Replace('\\', '/');
        var folder = packageFile.LastIndexOf('/');
        if (folder >= 0)
        {
            var prefix = packageFile[..(folder + 1)];
            foreach (var file in provider.Files)
            {
                ct.ThrowIfCancellationRequested();
                var path = file.Key;
                if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                    !path.EndsWith(".umap", StringComparison.OrdinalIgnoreCase))
                    continue;

                Consider(path, null);
            }
        }

        if (order.Count == 0) return;

        var composition = new WorldCompositionActor();
        foreach (var tile in order)
        {
            ct.ThrowIfCancellationRequested();
            var absolute = AbsoluteTilePosition(tile.PackageName, tiles, provider);
            var transform = new Transform(new FVector(absolute.X, absolute.Y, absolute.Z), FQuat.Identity, FVector.OneVector);
            var softWorld = CellActor.SoftWorldPath(tile.PackageName);
            composition.Children.Add(tile.Streaming is { } streaming
                ? new CellActor(streaming, tile.PackageName, transform)
                : new CellActor(ShortPackageName(tile.PackageName), softWorld, transform));
        }

        Children.Add(composition);

        void Consider(string? rawName, ULevelStreaming? streaming)
        {
            if (string.IsNullOrWhiteSpace(rawName)) return;

            var packageName = NormalizePackageName(rawName, provider);
            if (packageName == null) return;
            if (persistent != null && packageName.Equals(persistent, StringComparison.OrdinalIgnoreCase)) return;
            if (!seen.Add(packageName)) return;

            FWorldTileInfo? info;
            try
            {
                info = FWorldTileInfo.ReadUmap(provider, rawName);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Log.Warning(exception, "World composition tile {Package} could not be read", packageName);
                return;
            }

            if (info == null || IsExcludedLod(packageName, tiles)) return;

            var tile = new CompositionTile(packageName, info, streaming);
            tiles.Add(packageName, tile);
            order.Add(tile);
            _compositionTiles.Add(packageName);
        }
    }

    private static bool IsWorldCompositionEnabled(UWorld world, ULevel level)
    {
        if (world.TryGetValue<FPackageIndex>(out var composition, "WorldComposition") && composition is { IsNull: false })
            return true;

        if (world.Owner is Package package)
        {
            foreach (var export in package.ExportMap)
            {
                if (IsWorldCompositionClass(export.ClassName)) return true;
            }
        }

        return level.WorldSettings.TryLoad<AWorldSettings>(out var settings) &&
               settings.GetOrDefault<bool>("bEnableWorldComposition");
    }

    private static bool IsWorldCompositionClass(string? className)
    {
        if (string.IsNullOrEmpty(className)) return false;

        var slash = className.LastIndexOf('/');
        var dot = className.LastIndexOf('.');
        var name = className[(Math.Max(slash, dot) + 1)..];
        return name.Equals("WorldComposition", StringComparison.OrdinalIgnoreCase)
            || name.Equals("UWorldComposition", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsCompositionPackage(ULevelStreaming streaming)
    {
        if (_compositionTiles.Count == 0 || streaming.Owner?.Provider is not { } provider)
            return false;

        var packageName = NormalizePackageName(StreamingPackageSource(streaming), provider);
        if (packageName == null) return false;
        if (_compositionTiles.Contains(packageName)) return true;
        if (!TrySplitLodSuffix(packageName, out var baseName) || !_compositionTiles.Contains(baseName))
            return false;

        return IsSameOrNestedDirectory(PackageDirectory(packageName), PackageDirectory(baseName));
    }

    private static string? StreamingPackageSource(ULevelStreaming streaming)
    {
        if (streaming.WorldAsset is { } asset)
        {
            var text = asset.AssetPathName.Text;
            if (!string.IsNullOrEmpty(text) && !text.Equals("None", StringComparison.OrdinalIgnoreCase))
                return text;
        }

        var packageName = streaming.GetOrDefault<FName>("PackageNameToLoad");
        var packageText = packageName.Text;
        return string.IsNullOrEmpty(packageText) || packageText.Equals("None", StringComparison.OrdinalIgnoreCase) ? null : packageText;
    }

    private static FIntVector AbsoluteTilePosition(string packageName, Dictionary<string, CompositionTile> tiles, IFileProvider provider)
    {
        var absolute = new FIntVector(0, 0, 0);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = packageName;
        while (current != null)
        {
            if (!visited.Add(current)) break;
            if (!tiles.TryGetValue(current, out var tile)) break;

            absolute += tile.Info.Position;
            current = NormalizePackageName(tile.Info.ParentTilePackageName, provider);
        }

        return absolute;
    }

    private static bool IsExcludedLod(string packageName, Dictionary<string, CompositionTile> tiles)
    {
        if (!TrySplitLodSuffix(packageName, out var baseName)) return false;
        if (!tiles.TryGetValue(baseName, out var baseTile)) return true;

        return IsSameOrNestedDirectory(PackageDirectory(packageName), PackageDirectory(baseTile.PackageName));
    }

    private static bool TrySplitLodSuffix(string packageName, out string baseName)
    {
        for (var lod = 1; lod <= 4; lod++)
        {
            var suffix = "_LOD" + lod;
            if (!packageName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;

            baseName = packageName[..^suffix.Length];
            return true;
        }

        baseName = "";
        return false;
    }

    private static bool IsSameOrNestedDirectory(string packageDirectory, string tileDirectory)
    {
        if (packageDirectory.Equals(tileDirectory, StringComparison.OrdinalIgnoreCase)) return true;
        if (tileDirectory.Length == 0) return false;
        return packageDirectory.StartsWith(tileDirectory + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static string PackageDirectory(string packageName)
    {
        var slash = packageName.LastIndexOf('/');
        return slash >= 0 ? packageName[..slash] : "";
    }

    private static string ShortPackageName(string packageName)
    {
        var slash = packageName.LastIndexOf('/');
        return slash >= 0 ? packageName[(slash + 1)..] : packageName;
    }

    private static string? NormalizePackageName(string? name, IFileProvider provider)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        var path = name.Replace('\\', '/').Trim();
        if (path.Equals("None", StringComparison.OrdinalIgnoreCase)) return null;

        var colon = path.IndexOf(':');
        if (colon >= 0) path = path[..colon];

        path = StripPackageSuffix(path).TrimEnd('/');
        if (path.Length == 0 || path.Equals("None", StringComparison.OrdinalIgnoreCase)) return null;

        path = ToLongPackageName(path, provider);
        return path.Length == 0 || path.Equals("None", StringComparison.OrdinalIgnoreCase) ? null : path;
    }

    private static string StripPackageSuffix(string path)
    {
        var slash = path.LastIndexOf('/');
        var dot = path.LastIndexOf('.');
        if (dot <= slash) return path;

        var tail = path[(dot + 1)..];
        if (tail.Equals("umap", StringComparison.OrdinalIgnoreCase) || tail.Equals("uasset", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..dot];
            slash = path.LastIndexOf('/');
            dot = path.LastIndexOf('.');
            if (dot <= slash) return path;
        }

        return path[..dot];
    }

    private static string ToLongPackageName(string path, IFileProvider provider)
    {
        if (path.StartsWith('/')) return path;
        if (TryUncookedLongName(path, out var uncooked)) return uncooked;
        if (TryStripContentRoot(path, "Engine/Content/", "/Engine/", out var engine)) return engine;

        var project = provider.ProjectName;
        if (!string.IsNullOrEmpty(project) && TryStripContentRoot(path, project + "/Content/", "/Game/", out var game))
            return game;

        string? mount = null;
        var mountLength = -1;
        foreach (var (key, directory) in provider.VirtualPaths)
        {
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(directory)) continue;

            var prefix = directory.Replace('\\', '/').TrimEnd('/') + "/Content/";
            if (prefix.Length <= mountLength || !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

            mount = key;
            mountLength = prefix.Length;
        }

        if (mount != null)
            return "/" + mount + "/" + path[mountLength..];

        if (!string.IsNullOrEmpty(project) && project.Equals("FortniteGame", StringComparison.OrdinalIgnoreCase))
        {
            var marker = project + "/Plugins/GameFeatures/";
            if (path.StartsWith(marker, StringComparison.OrdinalIgnoreCase))
            {
                var tail = path[marker.Length..];
                var content = tail.IndexOf("/Content/", StringComparison.OrdinalIgnoreCase);
                if (content > 0)
                    return "/" + tail[..content] + "/" + tail[(content + "/Content/".Length)..];
            }
        }

        return path;
    }

    private static bool TryUncookedLongName(string path, out string longName)
    {
        longName = "";
        var slash = path.IndexOf('/');
        if (slash <= 0) return false;

        var root = path[..slash];
        if (!root.Equals("Game", StringComparison.OrdinalIgnoreCase) && !root.Equals("Engine", StringComparison.OrdinalIgnoreCase))
            return false;

        var rest = path[(slash + 1)..];
        if (rest.StartsWith("Content/", StringComparison.OrdinalIgnoreCase) ||
            rest.StartsWith("Plugins/", StringComparison.OrdinalIgnoreCase) ||
            rest.StartsWith("Config/", StringComparison.OrdinalIgnoreCase))
            return false;

        longName = "/" + path;
        return true;
    }

    private static bool TryStripContentRoot(string path, string filePrefix, string longPrefix, out string longName)
    {
        if (!path.StartsWith(filePrefix, StringComparison.OrdinalIgnoreCase))
        {
            longName = "";
            return false;
        }

        longName = longPrefix + path[filePrefix.Length..];
        return true;
    }

    private void Process(FPackageIndex? ptr)
    {
        switch (ptr?.Load())
        {
            case UWorldPartition partition:
            {
                Process(partition.RuntimeHash); // UWorldPartitionRuntimeHash
                break;
            }
            case UWorldPartitionRuntimeHashSet set:
            {
                var hlod = set.RuntimeStreamingData.OrderBy(x => x.LoadingRange).ElementAt(1);
                for (var i = 0; i < hlod.SpatiallyLoadedCells.Length; i++)
                {
                    Process(hlod.SpatiallyLoadedCells[i]); // UWorldPartitionRuntimeLevelStreamingCell
                }
                break;
            }
            case UWorldPartitionRuntimeSpatialHash spatial when spatial.StreamingGrids[0].GridLevels.Length > 0:
            {
                for (var i = 0; i < spatial.StreamingGrids[0].GridLevels[0].LayerCells.Length; i++)
                {
                    Process(spatial.StreamingGrids[0].GridLevels[0].LayerCells[i].GridCells[0]); // UWorldPartitionRuntimeLevelStreamingCell
                }
                break;
            }
            case UWorldPartitionRuntimeLevelStreamingCell cell:
            {
                Process(cell.LevelStreaming); // UWorldPartitionLevelStreamingDynamic
                break;
            }
            case ULevelStreaming streaming:
            {
                if (IsCompositionPackage(streaming))
                    break;

                if (streaming.WorldAsset is { } worldAsset && worldAsset.TryLoad<UWorld>(out var world))
                {
                    Children.Add(new CellActor(worldAsset, world, streaming is ULevelStreamingAlwaysLoaded or ULevelStreamingPersistent, streaming.LevelTransform));
                }
                break;
            }
        }
    }

    public override void Export(ExportSession session, CancellationToken ct = default)
    {
        if (ActorManager is not { } manager || string.IsNullOrEmpty(Path))
            return;

        try
        {
            session.Add(manager.FileProvider.LoadPackageObject(Path, Name));
        }
        catch (Exception e)
        {
            Log.Warning(e, "{World} could not be queued for export", Name);
        }

        ExportLoaded(this);

        void ExportLoaded(Actor actor)
        {
            foreach (var child in actor.Children)
            {
                ct.ThrowIfCancellationRequested();
                if (child is StreamableActor { IsLoaded: true }) child.Export(session, ct);
                else ExportLoaded(child);
            }
        }
    }

    public override string Icon => Settings.EarthEuropeIcon;

    public override void DrawControls()
    {
        base.DrawControls();

        if (_dataLayers.Count == 0)
        {
            ImGui.TextUnformatted("No data layers found");
            return;
        }

        if (ImGui.BeginListBox("##DataLayers", new Vector2(ImGui.GetContentRegionAvail().X, 0)))
        {
            var partition = Children.OfType<PartitionActor>().FirstOrDefault();
            if (partition != null)
            {
                foreach (var (instanceName, layerName) in _dataLayers)
                {
                    var isVisible = _visibleDataLayers.Contains(instanceName);
                    if (ImGui.Checkbox(layerName, ref isVisible))
                    {
                        if (isVisible)
                        {
                            _visibleDataLayers.Add(instanceName);
                        }
                        else
                        {
                            _visibleDataLayers.Remove(instanceName);
                        }

                        partition.SetDataLayerEnabled(instanceName, isVisible);
                    }
                }
            }
            ImGui.EndListBox();
        }
    }
}
