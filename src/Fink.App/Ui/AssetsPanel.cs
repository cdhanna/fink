using Fink.Core;
using Fink.Editor;
using ImGuiNET;
using Microsoft.Xna.Framework.Graphics;
using Num = System.Numerics;

namespace Fink.App;

/// <summary>
/// The asset library: an Assets panel (search, folders, thumbnails) for projects with many
/// assets, and a slim bar above the canvas with the current asset, a searchable picker (Ctrl+P)
/// and previous / next. A "/" in an asset's name puts it in a folder: "enemies/slime".
/// </summary>
public sealed partial class FinkGame
{
    private string _assetSearch = "";
    private string _pickerSearch = "";
    private int _pickerIndex;
    private bool _pickerFocus;

    private sealed class Thumb
    {
        public Texture2D? Texture;
        public IntPtr Id;
        public long Stamp = long.MinValue;
    }
    private readonly Dictionary<Document, Thumb> _thumbs = new();

    /// <summary>The asset's first frame (its current one, for the asset being edited), as a texture.</summary>
    private IntPtr ThumbFor(Document asset)
    {
        if (!_thumbs.TryGetValue(asset, out var t)) _thumbs[asset] = t = new Thumb();
        bool current = asset == Ed.Document;
        long stamp = asset.Palette.Version * 7919L + Ed.ViewVariant * 31
                     + (current ? Ed.ChangeStamp * 131 + Ed.FrameIndex : 0) + asset.Width * 3 + asset.Height;
        if (t.Texture != null && t.Stamp == stamp) return t.Id;
        if (t.Texture == null || t.Texture.Width != asset.Width || t.Texture.Height != asset.Height)
        {
            if (t.Texture != null) { _imgui.UnbindTexture(t.Id); t.Texture.Dispose(); }
            t.Texture = new Texture2D(GraphicsDevice, asset.Width, asset.Height, false, SurfaceFormat.Color);
            t.Id = _imgui.BindTexture(t.Texture);
        }
        var px = new uint[asset.Width * asset.Height];
        Compositor.Render(asset, current ? Ed.FrameIndex : 0, px, null, checker: false, colors: Ed.ViewColors);
        t.Texture.SetData(px);
        t.Stamp = stamp;
        return t.Id;
    }

    /// <summary>Drops thumbnails of assets that are gone (another project, deleted).</summary>
    private void PruneThumbs()
    {
        foreach (var gone in _thumbs.Keys.Where(a => !Ed.Project.Assets.Contains(a)).ToList())
        {
            var t = _thumbs[gone];
            if (t.Texture != null) { _imgui.UnbindTexture(t.Id); t.Texture.Dispose(); }
            _thumbs.Remove(gone);
        }
    }

    private static bool Matches(Document a, string search) =>
        search.Length == 0 || search.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .All(word => a.Name.Contains(word, StringComparison.OrdinalIgnoreCase));

    // ---------------------------------------------------------------- the bar above the canvas

    private void DrawAssetBar()
    {
        float sc = Ui.UiScale;
        ImGui.SetCursorPos(ImGui.GetCursorPos() + new Num.Vector2(6 * sc, 4 * sc));
        CommandButton("asset.previous", "<");
        ImGui.SameLine();
        var label = $"{Ed.Document.Name}{(Ed.IsDirty ? " *" : "")}  ";
        if (ImGui.Button($"{label}##assetpick")) Ui.ShowAssetPicker = true;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"{Ed.Document.Width}x{Ed.Document.Height}, {Ed.Document.Frames.Count} frames. Click (or Ctrl+P) to go to another asset.");
        ImGui.SameLine();
        CommandButton("asset.next", ">");
        ImGui.SameLine();
        CommandButton("asset.new", "+");
        ImGui.SameLine();
        ImGui.TextDisabled($"{Ed.Project.Assets.IndexOf(Ed.Document) + 1} of {Ed.Project.Assets.Count}");
        DrawAssetPicker();
    }

    /// <summary>Ctrl+P: type to find an asset, Enter to open it.</summary>
    private void DrawAssetPicker()
    {
        if (Ui.ShowAssetPicker)
        {
            Ui.ShowAssetPicker = false;
            _pickerSearch = "";
            _pickerIndex = 0;
            _pickerFocus = true;
            ImGui.OpenPopup("assetpicker");
        }
        ImGui.SetNextWindowSizeConstraints(new Num.Vector2(320, 0) * Ui.UiScale, new Num.Vector2(520, 480) * Ui.UiScale);
        if (!ImGui.BeginPopup("assetpicker")) return;
        if (_pickerFocus) { ImGui.SetKeyboardFocusHere(); _pickerFocus = false; }
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextWithHint("##find", "find an asset...", ref _pickerSearch, 128)) _pickerIndex = 0;
        var found = Ed.Project.Assets.Where(a => Matches(a, _pickerSearch)).ToList();
        if (ImGui.IsKeyPressed(ImGuiKey.DownArrow)) _pickerIndex = Math.Min(_pickerIndex + 1, found.Count - 1);
        if (ImGui.IsKeyPressed(ImGuiKey.UpArrow)) _pickerIndex = Math.Max(_pickerIndex - 1, 0);
        bool enter = ImGui.IsKeyPressed(ImGuiKey.Enter) || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter);
        if (ImGui.IsKeyPressed(ImGuiKey.Escape)) ImGui.CloseCurrentPopup();
        if (ImGui.BeginChild("list", new Num.Vector2(0, Math.Min(found.Count, 12) * (ImGui.GetTextLineHeightWithSpacing() + 6 * Ui.UiScale) + 8)))
        {
            for (int i = 0; i < found.Count; i++)
            {
                var a = found[i];
                bool sel = i == _pickerIndex;
                if (ImGui.Selectable($"{a.Name}##{a.GetHashCode()}", sel) || (sel && enter))
                {
                    Ed.SwitchAsset(a);
                    ImGui.CloseCurrentPopup();
                }
                if (sel) ImGui.SetScrollHereY();
                ImGui.SameLine(ImGui.GetContentRegionAvail().X - 90 * Ui.UiScale);
                ImGui.TextDisabled($"{a.Width}x{a.Height}  {a.Frames.Count}f");
            }
            if (found.Count == 0) ImGui.TextDisabled("Nothing matches.");
        }
        ImGui.EndChild();
        ImGui.EndPopup();
    }

    // ---------------------------------------------------------------- the Assets panel

    private void DrawAssetsPanel()
    {
        PruneThumbs();
        float sc = Ui.UiScale;
        CommandButton("asset.new", "+ New");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##search", "search", ref _assetSearch, 128);

        if (!ImGui.BeginChild("assets")) { ImGui.EndChild(); return; }
        var assets = Ed.Project.Assets;
        if (_assetSearch.Length > 0)
        {
            foreach (var a in assets.Where(a => Matches(a, _assetSearch))) DrawAssetRow(a, a.Name);
        }
        else
        {
            // folders from the "/" in names, then the assets in each, in project order
            DrawFolder("", assets.ToList());
        }
        ImGui.EndChild();
    }

    private void DrawFolder(string prefix, List<Document> assets)
    {
        var here = assets.Where(a => !a.Name[prefix.Length..].Contains('/')).ToList();
        var folders = assets.Except(here)
            .Select(a => a.Name[prefix.Length..].Split('/')[0])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders)
        {
            var inside = assets.Where(a => a.Name[prefix.Length..].StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase)).ToList();
            bool holdsCurrent = inside.Contains(Ed.Document);
            if (holdsCurrent) ImGui.SetNextItemOpen(true, ImGuiCond.Once);
            if (ImGui.TreeNodeEx($"{folder}  ({inside.Count})##{prefix}{folder}", ImGuiTreeNodeFlags.SpanAvailWidth))
            {
                DrawFolder(prefix + folder + "/", inside);
                ImGui.TreePop();
            }
        }
        foreach (var a in here) DrawAssetRow(a, a.Name[prefix.Length..]);
    }

    private void DrawAssetRow(Document a, string label)
    {
        float sc = Ui.UiScale;
        float rowH = 40 * sc;
        ImGui.PushID(a.GetHashCode());
        var start = ImGui.GetCursorScreenPos();
        bool current = a == Ed.Document;
        if (ImGui.Selectable("##row", current, ImGuiSelectableFlags.None, new Num.Vector2(0, rowH))) Ed.SwitchAsset(a);
        if (ImGui.BeginPopupContextItem("menu"))
        {
            Ed.SwitchAsset(a);
            MenuCommand("asset.rename");
            MenuCommand("asset.nineSlice");
            MenuCommand("asset.duplicate");
            MenuCommand("asset.delete");
            ImGui.EndPopup();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip($"{a.Name}\n{a.Width}x{a.Height}, {a.Frames.Count} frames, {a.Tags.Count} tags\nRight-click to rename (a / in the name makes a folder).");

        // only visible rows get thumbnails, so a long list stays quick
        if (ImGui.IsRectVisible(start, start + new Num.Vector2(rowH, rowH)))
        {
            var dl = ImGui.GetWindowDrawList();
            float s = rowH - 4 * sc;
            float scale = Math.Min(s / a.Width, s / a.Height);
            var size = new Num.Vector2(a.Width, a.Height) * scale;
            var p = start + new Num.Vector2(2 * sc + (s - size.X) / 2, 2 * sc + (s - size.Y) / 2);
            dl.AddRectFilled(start + Num.Vector2.One * 2 * sc, start + Num.Vector2.One * (2 * sc + s), 0x40FFFFFFu, 3 * sc);
            dl.AddImage(ThumbFor(a), p, p + size);
            var text = ImGui.GetStyle().Colors[(int)ImGuiCol.Text];
            var dim = ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled];
            float x = start.X + rowH + 6 * sc;
            dl.AddText(new Num.Vector2(x, start.Y + 3 * sc), ImGui.GetColorU32(text), label + (current && Ed.IsDirty ? " *" : ""));
            dl.AddText(new Num.Vector2(x, start.Y + 3 * sc + ImGui.GetTextLineHeight()), ImGui.GetColorU32(dim),
                $"{a.Width}x{a.Height}   {a.Frames.Count} frames" + (a.Tags.Count > 0 ? $"   {string.Join(", ", a.Tags.Select(t => t.Name))}" : ""));
        }
        ImGui.PopID();
    }
}
