using Fink.Core;
using Fink.Editor;
using ImGuiNET;
using Microsoft.Xna.Framework.Graphics;
using Num = System.Numerics;

namespace Fink.App;

/// <summary>
/// The Atlas panel: the packed atlas, live, as you draw. The current asset's pictures are
/// outlined; clicking a picture jumps to where it's used. Also the atlas's settings, and the
/// palette variant picker the palette panel uses.
/// </summary>
public sealed partial class FinkGame
{
    private AtlasBuild? _atlasBuild;
    private long _atlasStamp = long.MinValue;
    private double _atlasChangedAt = -1;
    private Texture2D? _atlasTexture;
    private IntPtr _atlasTextureId;
    private int _atlasZoom = 2;
    private bool _atlasSettingsDirty;
    private string _atlasRename = "";

    /// <summary>Repacks when something changed and has settled for a moment (not on every pixel).</summary>
    private void UpdateAtlasPreview()
    {
        if (!Ui.IsOpen("Atlas")) return;
        var config = Ed.CurrentAtlas;
        long stamp = AtlasStamp() + (_atlasSettingsDirty ? 1 : 0);
        if (stamp != _atlasStamp)
        {
            if (_atlasChangedAt < 0) _atlasChangedAt = Ed.Time;
            bool settled = Ed.Time - _atlasChangedAt > 0.25 || _atlasBuild == null || _atlasSettingsDirty;
            if (!settled || Ed.Stroking) return;
            _atlasSettingsDirty = false;
            _atlasBuild = Ed.BuildAtlas(config);
            _atlasStamp = AtlasStamp(); // building may have touched the project
            _atlasChangedAt = -1;
            UploadAtlas();
        }
    }

    /// <summary>Changes when anything the atlas shows could have: content, colors, which atlas, which project.</summary>
    private long AtlasStamp() =>
        Ed.ChangeStamp * 31 + Ed.ViewVariant * 7 + Ed.AtlasIndex * 131
        + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Ed.Project) * 1_000_003L;

    private void UploadAtlas()
    {
        var b = _atlasBuild;
        if (b == null || b.Error != null) return;
        if (_atlasTexture == null || _atlasTexture.Width != b.Width || _atlasTexture.Height != b.Height)
        {
            if (_atlasTexture != null) { _imgui.UnbindTexture(_atlasTextureId); _atlasTexture.Dispose(); }
            _atlasTexture = new Texture2D(GraphicsDevice, b.Width, b.Height, false, SurfaceFormat.Color);
            _atlasTextureId = _imgui.BindTexture(_atlasTexture);
        }
        _atlasTexture.SetData(AtlasExport.Colorize(b, Ed.ViewColors));
    }

    private void DrawAtlasPanel()
    {
        var project = Ed.Project;
        var config = Ed.CurrentAtlas;
        float sc = Ui.UiScale;

        // ---- which atlas
        ImGui.SetNextItemWidth(160 * sc);
        if (ImGui.BeginCombo("##atlas", config.Name))
        {
            for (int i = 0; i < project.Atlases.Count; i++)
                if (ImGui.Selectable($"{project.Atlases[i].Name}##{i}", i == Ed.AtlasIndex)) { Ed.AtlasIndex = i; _atlasSettingsDirty = true; }
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        if (ImGui.Button("+##atlas")) { Ed.AddAtlas("atlas"); _atlasSettingsDirty = true; }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Another atlas (e.g. characters and UI apart)");
        ImGui.SameLine();
        CommandButton("file.export", "Export");
        ImGui.SameLine();
        CommandButton("atlas.repack", "Repack");
        if (ImGui.IsItemClicked()) _atlasSettingsDirty = true;

        // ---- status
        var b = _atlasBuild;
        if (b?.Error is { } err)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Num.Vector4(1f, 0.45f, 0.45f, 1));
            ImGui.TextWrapped(err);
            ImGui.PopStyleColor();
        }
        else if (b != null)
        {
            float fill = b.Width * b.Height == 0 ? 0 : 100f * b.UsedPixels / (b.Width * b.Height);
            ImGui.TextDisabled($"{b.Width}x{b.Height}  {b.Cells.Count} pictures  {b.Assets.Count} assets  {b.Clips.Count} clips  {fill:0}% used");
        }
        if (Ed.LastAtlasExport is { } msg) ImGui.TextDisabled(msg);

        // ---- settings
        if (ImGui.CollapsingHeader("Settings"))
        {
            if (ImGui.IsWindowAppearing() || _atlasRename.Length == 0) _atlasRename = config.Name;
            ImGui.SetNextItemWidth(180 * sc);
            ImGui.InputText("Name", ref _atlasRename, 64);
            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                var n = new string(_atlasRename.Trim().Where(c => char.IsLetterOrDigit(c) || c is '_' or '-').ToArray());
                if (n.Length > 0 && !project.Atlases.Any(a => a != config && a.Name.Equals(n, StringComparison.OrdinalIgnoreCase)))
                {
                    config.Name = n;
                    project.Touch();
                }
                _atlasRename = config.Name;
            }

            var folder = config.OutputFolder;
            ImGui.SetNextItemWidth(-80 * sc);
            if (ImGui.InputTextWithHint("Folder", "the game's Assets/Textures folder", ref folder, 512)) { config.OutputFolder = folder; project.Touch(); }
            var code = config.CodeFolder;
            ImGui.SetNextItemWidth(-80 * sc);
            if (ImGui.InputTextWithHint("Code", "where fink_<atlas>.fbasic goes (default: Folder)", ref code, 512)) { config.CodeFolder = code; project.Touch(); }
            var content = config.ContentPath;
            ImGui.SetNextItemWidth(-80 * sc);
            if (ImGui.InputTextWithHint("Load as", config.EffectiveContentPath, ref content, 256)) { config.ContentPath = content; project.Touch(); }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("The path the game loads it by: relative to its content root, no extension.");

            int max = config.MaxSize;
            ImGui.SetNextItemWidth(110 * sc);
            if (ImGui.BeginCombo("Max size", $"{max}"))
            {
                foreach (var m in new[] { 256, 512, 1024, 2048, 4096, 8192 })
                    if (ImGui.Selectable($"{m}", m == max)) { config.MaxSize = m; project.Touch(); _atlasSettingsDirty = true; }
                ImGui.EndCombo();
            }
            ImGui.SameLine();
            bool pow2 = config.PowerOfTwo;
            if (ImGui.Checkbox("Power of two", ref pow2)) { config.PowerOfTwo = pow2; config.LastWidth = config.LastHeight = 0; project.Touch(); _atlasSettingsDirty = true; }
            int pad = config.Padding;
            ImGui.SetNextItemWidth(110 * sc);
            if (ImGui.InputInt("Padding", ref pad)) { config.Padding = Math.Clamp(pad, 0, 16); project.Touch(); _atlasSettingsDirty = true; }

            bool onSave = project.ExportOnSave;
            if (ImGui.Checkbox("Export when saving", ref onSave)) { project.ExportOnSave = onSave; project.Touch(); }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Ctrl+S also exports every atlas that has a folder, so the game always has the saved art.");
            bool all = config.IncludeAll;
            if (ImGui.Checkbox("Every asset", ref all)) { config.IncludeAll = all; if (!all && config.AssetIds.Count == 0) config.AssetIds.AddRange(project.Assets.Select(a => a.Id)); project.Touch(); _atlasSettingsDirty = true; }
            if (!config.IncludeAll)
                foreach (var asset in project.Assets)
                {
                    bool on = config.AssetIds.Contains(asset.Id);
                    if (ImGui.Checkbox($"{asset.Name}##inc{asset.Id}", ref on))
                    {
                        if (on) config.AssetIds.Add(asset.Id); else config.AssetIds.Remove(asset.Id);
                        project.Touch();
                        _atlasSettingsDirty = true;
                    }
                }
            DrawMapsEditor(config);
            if (project.Atlases.Count > 1 && ImGui.SmallButton("Delete this atlas"))
            {
                Ed.RemoveAtlas(config);
                _atlasSettingsDirty = true;
            }
        }

        // ---- the picture
        DrawVariantPicker();
        ImGui.SameLine();
        ImGui.SetNextItemWidth(100 * sc);
        ImGui.SliderInt("##zoom", ref _atlasZoom, 1, 8, "zoom %dx");
        if (b == null || b.Error != null || _atlasTexture == null) return;

        if (ImGui.BeginChild("atlasview", Num.Vector2.Zero, ImGuiChildFlags.Borders, ImGuiWindowFlags.HorizontalScrollbar))
        {
            var p0 = ImGui.GetCursorScreenPos();
            var size = new Num.Vector2(b.Width, b.Height) * _atlasZoom;
            var dl = ImGui.GetWindowDrawList();
            // a checkerboard shows what's empty
            dl.AddRectFilled(p0, p0 + size, 0xFF2A2630u);
            ImGui.Image(_atlasTextureId, size);
            bool hovered = ImGui.IsItemHovered();
            var mouse = (ImGui.GetMousePos() - p0) / _atlasZoom;
            Ed.Document.Owner ??= project;
            b.CellsOf.TryGetValue(Ed.Document, out var mine);
            for (int i = 0; i < b.Cells.Count; i++)
            {
                var c = b.Cells[i];
                var a = p0 + new Num.Vector2(c.X, c.Y) * _atlasZoom;
                var e = a + new Num.Vector2(c.W, c.H) * _atlasZoom;
                bool isMine = mine?.Contains(i) == true;
                bool over = hovered && mouse.X >= c.X && mouse.Y >= c.Y && mouse.X < c.X + c.W && mouse.Y < c.Y + c.H;
                if (isMine) dl.AddRect(a - Num.Vector2.One, e + Num.Vector2.One, 0xFF40D0FFu);
                else dl.AddRect(a, e, 0x30FFFFFFu);
                if (!over) continue;
                dl.AddRect(a - Num.Vector2.One * 2, e + Num.Vector2.One * 2, 0xFFFFFFFFu, 0, ImDrawFlags.None, 2);
                var uses = b.Clips.Where(cl => cl.Name == "").SelectMany(cl => cl.Frames.Where(f => f.Cell == i).Select(f => (cl.Asset, f))).ToList();
                ImGui.SetTooltip($"{c.W}x{c.H} at {c.X},{c.Y}\n" + string.Join("\n", uses.Take(6).Select(u =>
                    $"{u.Asset.Name}, frame {u.f.TimelineFrom + 1}" + (u.f.TimelineTo > u.f.TimelineFrom ? $"-{u.f.TimelineTo + 1}" : ""))) +
                    "\nClick to go there.");
                if (ImGui.IsItemClicked() && uses.Count > 0)
                {
                    var (asset, f) = uses.FirstOrDefault(u => u.Asset == Ed.Document) is { Asset: not null } here ? here : uses[0];
                    Ed.SwitchAsset(asset);
                    Ed.FrameIndex = f.TimelineFrom;
                }
            }
        }
        ImGui.EndChild();
    }

    /// <summary>
    /// Data maps: each a texture with the atlas's layout, and which channel goes in each of
    /// R, G, B, A. Exported as &lt;atlas&gt;.&lt;map&gt;.png; MAP_ constants in the game.
    /// </summary>
    private void DrawMapsEditor(AtlasConfig config)
    {
        var project = Ed.Project;
        float sc = Ui.UiScale;
        ImGui.SeparatorText("Data maps");
        var options = new List<(string slot, string label)> { ("", "0"), ("255", "255"), ("128", "128") };
        foreach (var ch in project.Channels)
        {
            if (ch.Kind == ChannelKind.Value) options.Add((ch.Name, ch.Name));
            else foreach (var c in new[] { "x", "y", "z" }) options.Add(($"{ch.Name}.{c}", $"{ch.Name}.{c}"));
        }
        if (project.Channels.Count == 0) ImGui.TextDisabled("No data channels yet: add one in the Tools panel's Paint section.");

        foreach (var map in config.Maps.ToList())
        {
            ImGui.PushID(map.GetHashCode());
            var name = map.Name;
            ImGui.SetNextItemWidth(90 * sc);
            if (ImGui.InputText("##name", ref name, 32) && name.Trim().Length > 0) { map.Name = name.Trim(); project.Touch(); }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip($"Exported as {config.Name}.{map.Name}.png");
            string[] labels = { "R", "G", "B", "A" };
            for (int k = 0; k < 4; k++)
            {
                ImGui.SameLine();
                var current = map.Slots[k];
                ImGui.SetNextItemWidth(78 * sc);
                if (ImGui.BeginCombo($"##{labels[k]}", $"{labels[k]}: {(current.Length == 0 ? "0" : current)}"))
                {
                    foreach (var (slot, label) in options)
                        if (ImGui.Selectable(label, slot == current))
                        {
                            switch (k) { case 0: map.R = slot; break; case 1: map.G = slot; break; case 2: map.B = slot; break; default: map.A = slot; break; }
                            project.Touch();
                        }
                    ImGui.EndCombo();
                }
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("x")) { config.Maps.Remove(map); project.Touch(); }
            if (map.A.Length > 0 && map.A != "255" && !int.TryParse(map.A, out _))
                ImGui.TextColored(new Num.Vector4(1f, 0.8f, 0.4f, 1), "  Data in A: the game's content build premultiplies alpha, which changes R, G, B. Turn premultiply off for this texture, or keep A at 255.");
            ImGui.PopID();
        }
        if (ImGui.SmallButton("+ Map"))
        {
            var normal = project.Channels.FirstOrDefault(c => c.Kind == ChannelKind.Normal);
            var value = project.Channels.FirstOrDefault(c => c.Kind == ChannelKind.Value);
            config.Maps.Add(config.Maps.Count == 0 && normal != null
                ? new MapConfig { Name = "normal", R = $"{normal.Name}.x", G = $"{normal.Name}.y", B = $"{normal.Name}.z", A = "255" }
                : new MapConfig { Name = value?.Name ?? $"map{config.Maps.Count + 1}", R = value?.Name ?? "", A = "255" });
            project.Touch();
        }
    }

    /// <summary>Which colors to show: the base palette or a variant. Also adds, renames, deletes variants.</summary>
    private void DrawVariantPicker()
    {
        var pal = Ed.Project.Palette;
        ImGui.PushID("variants");
        ImGui.SetNextItemWidth(140 * Ui.UiScale);
        string current = Ed.ViewVariant == 0 ? "Base colors" : pal.Variants[Ed.ViewVariant - 1].Name;
        if (ImGui.BeginCombo("##variant", current))
        {
            if (ImGui.Selectable("Base colors", Ed.ViewVariant == 0)) Ed.ViewVariant = 0;
            for (int i = 0; i < pal.Variants.Count; i++)
                if (ImGui.Selectable($"{pal.Variants[i].Name}##v{i}", Ed.ViewVariant == i + 1)) Ed.ViewVariant = i + 1;
            ImGui.Separator();
            if (ImGui.Selectable("+ New variant"))
            {
                int n = pal.Variants.Count + 1;
                while (pal.Variants.Any(v => v.Name == $"variant {n}")) n++;
                pal.AddVariant($"variant {n}");
                Ed.ViewVariant = pal.Variants.Count;
            }
            ImGui.EndCombo();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Palette variants: the same entries in other colors (a team, night). Each is a row of the exported palette image; a game swaps rows in its shader.\nRight-click to rename or delete.");
        if (Ed.ViewVariant > 0 && ImGui.BeginPopupContextItem("variantmenu"))
        {
            var v = pal.Variants[Ed.ViewVariant - 1];
            var name = v.Name;
            ImGui.SetNextItemWidth(160 * Ui.UiScale);
            if (ImGui.InputText("Name", ref name, 48) && name.Trim().Length > 0) { v.Name = name.Trim(); pal.Touch(); }
            if (ImGui.MenuItem("Copy base colors into it"))
            {
                for (int i = 1; i < pal.Count; i++) v.Entries[i] = pal.Entries[i];
                pal.Touch();
            }
            if (ImGui.MenuItem("Delete variant"))
            {
                pal.Variants.Remove(v);
                pal.Touch();
                Ed.ViewVariant = 0;
            }
            ImGui.EndPopup();
        }
        ImGui.PopID();
    }
}
