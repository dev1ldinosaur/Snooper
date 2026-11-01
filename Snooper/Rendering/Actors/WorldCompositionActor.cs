using Snooper.Rendering.Components.Transforms;
using Snooper.UI;

namespace Snooper.Rendering.Actors;

public class WorldCompositionActor : Actor
{
    public WorldCompositionActor() : base("WorldComposition")
    {
        Components.Add(new SpatialComponent(null, "CompositionRoot"));
    }

    public override void DrawControls()
    {
        base.DrawControls();

        var total = 0;
        var loaded = 0;
        var failed = 0;
        foreach (var child in Children)
        {
            if (child is not CellActor cell)
                continue;

            total++;
            if (cell.IsLoaded)
                loaded++;
            else if (cell.State == EStreamingState.Failed)
                failed++;
        }

        EditorUI.PropertyValueTable("Tiles", () =>
        {
            EditorUI.Text("Total", $"{total:N0}");
            EditorUI.Text("Loaded", $"{loaded:N0}");
            EditorUI.Text("Failed", $"{failed:N0}");
        });
    }
}
