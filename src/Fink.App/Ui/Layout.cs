using System.Numerics;
using System.Runtime.InteropServices;
using Fink.Editor;
using ImGuiNET;

namespace Fink.App;

/// <summary>
/// ImGui's DockBuilder API. ImGui.NET doesn't wrap it (it lives in imgui_internal.h), but the
/// cimgui.dll it ships exports it, so it is called directly.
/// </summary>
internal static unsafe class DockBuilder
{
    private const string Lib = "cimgui";

    /// <summary>ImGuiDockNodeFlags_DockSpace, from imgui_internal.h.</summary>
    public const int DockSpaceFlag = 1 << 10;

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "igDockBuilderAddNode")]
    public static extern uint AddNode(uint nodeId, int flags);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "igDockBuilderRemoveNode")]
    public static extern void RemoveNode(uint nodeId);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "igDockBuilderSetNodePos")]
    public static extern void SetNodePos(uint nodeId, Vector2 pos);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "igDockBuilderSetNodeSize")]
    public static extern void SetNodeSize(uint nodeId, Vector2 size);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "igDockBuilderSplitNode")]
    private static extern uint SplitNode(uint nodeId, ImGuiDir dir, float ratio, uint* outAtDir, uint* outOpposite);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "igDockBuilderDockWindow")]
    private static extern void DockWindow(byte* windowName, uint nodeId);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "igDockBuilderFinish")]
    public static extern void Finish(uint nodeId);

    /// <summary>Splits a node; returns (the new node on <paramref name="dir"/>, the rest).</summary>
    public static (uint atDir, uint rest) Split(uint node, ImGuiDir dir, float ratio)
    {
        uint a, b;
        SplitNode(node, dir, ratio, &a, &b);
        return (a, b);
    }

    public static void Dock(string window, uint node)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(window + "\0");
        fixed (byte* p = bytes) DockWindow(p, node);
    }
}

public sealed partial class FinkGame
{
    private const string DockspaceName = "FinkDockspace";
    private string? _presetToBuild;

    /// <summary>
    /// Before the frame: a saved workspace is loaded from its ini; a built-in one is built
    /// inside the frame with DockBuilder (see <see cref="DrawDockspace"/>).
    /// </summary>
    private void BeginPendingWorkspace()
    {
        var name = Ui.PendingWorkspace;
        if (name == null) return;
        Ui.PendingWorkspace = null;

        if (Workspaces.BuiltIn.Contains(name))
        {
            _presetToBuild = name;
        }
        else
        {
            var saved = _session.LoadWorkspace(name);
            if (saved == null) { Ed.SetStatus($"No workspace \"{name}\""); return; }
            ImGui.LoadIniSettingsFromMemory(saved.Ini);
            foreach (var (k, v) in saved.Panels) if (Ui.Panels.ContainsKey(k)) Ui.Panels[k] = v;
        }
        Ui.CurrentWorkspace = name;
    }

    private void DrawDockspace()
    {
        var viewport = ImGui.GetMainViewport();
        uint id = ImGui.GetID(DockspaceName);
        if (_presetToBuild != null)
        {
            BuildPreset(_presetToBuild, id, viewport.WorkPos, viewport.WorkSize);
            _presetToBuild = null;
        }
        ImGui.DockSpaceOverViewport(id, viewport);
    }

    /// <summary>The built-in workspaces. Window names must match the panels' Begin() names.</summary>
    private void BuildPreset(string name, uint root, Vector2 pos, Vector2 size)
    {
        DockBuilder.RemoveNode(root);
        DockBuilder.AddNode(root, DockBuilder.DockSpaceFlag);
        DockBuilder.SetNodePos(root, pos);
        DockBuilder.SetNodeSize(root, size);

        switch (name)
        {
            case Workspaces.Animate:
            {
                // a tall timeline; layers and history share the right column as tabs
                var (bottom, top) = DockBuilder.Split(root, ImGuiDir.Down, 0.28f);
                var (left, rest) = DockBuilder.Split(top, ImGuiDir.Left, 0.16f);
                var (right, center) = DockBuilder.Split(rest, ImGuiDir.Right, 0.2f);
                var (leftBottom, leftTop) = DockBuilder.Split(left, ImGuiDir.Down, 0.4f);
                DockBuilder.Dock("Tools", leftTop);
                DockBuilder.Dock("Palette", leftBottom);
                DockBuilder.Dock("Assets", right);
                DockBuilder.Dock("Layers", right);
                DockBuilder.Dock("History", right);
                DockBuilder.Dock("Shader", right);
                DockBuilder.Dock("Atlas", right);
                DockBuilder.Dock(UiState.Canvas, center);
                DockBuilder.Dock("Timeline", bottom);
                SetPanels(true, true, true, true, true, shader: true);
                break;
            }
            case Workspaces.Minimal:
            {
                // just the canvas, colors and the frame strip
                var (left, rest) = DockBuilder.Split(root, ImGuiDir.Left, 0.14f);
                var (bottom, center) = DockBuilder.Split(rest, ImGuiDir.Down, 0.18f);
                DockBuilder.Dock("Palette", left);
                DockBuilder.Dock("Tools", left);
                DockBuilder.Dock("Assets", left);
                DockBuilder.Dock("Layers", left);
                DockBuilder.Dock("History", left);
                DockBuilder.Dock("Shader", left);
                DockBuilder.Dock("Atlas", left);
                DockBuilder.Dock(UiState.Canvas, center);
                DockBuilder.Dock("Timeline", bottom);
                SetPanels(tools: false, palette: true, layers: false, frames: true, history: false);
                break;
            }
            default: // Draw
            {
                var (left, rest) = DockBuilder.Split(root, ImGuiDir.Left, 0.16f);
                var (right, center) = DockBuilder.Split(rest, ImGuiDir.Right, 0.2f);
                var (bottom, middle) = DockBuilder.Split(center, ImGuiDir.Down, 0.2f);
                var (leftBottom, leftTop) = DockBuilder.Split(left, ImGuiDir.Down, 0.38f);
                var (rightBottom, rightTop) = DockBuilder.Split(right, ImGuiDir.Down, 0.55f);
                DockBuilder.Dock("Tools", leftTop);
                DockBuilder.Dock("Palette", leftBottom);
                DockBuilder.Dock("Assets", rightTop);
                DockBuilder.Dock("Layers", rightTop);
                DockBuilder.Dock("History", rightBottom);
                DockBuilder.Dock("Shader", rightBottom);
                DockBuilder.Dock("Atlas", rightBottom);
                DockBuilder.Dock(UiState.Canvas, middle);
                DockBuilder.Dock("Timeline", bottom);
                SetPanels(true, true, true, true, true);
                break;
            }
        }
        DockBuilder.Finish(root);
        Ed.View.FitRequested = true;
    }

    private void SetPanels(bool tools, bool palette, bool layers, bool frames, bool history, bool shader = false)
    {
        Ui.Panels["Shader"] = shader;
        Ui.Panels["Atlas"] = shader;
        Ui.Panels["Assets"] = layers;
        Ui.Panels["Tools"] = tools;
        Ui.Panels["Palette"] = palette;
        Ui.Panels["Layers"] = layers;
        Ui.Panels["Timeline"] = frames;
        Ui.Panels["History"] = history;
    }

    // ------------------------------------------------------------------ UI scale

    private float _appliedScale = -1;

    /// <summary>Rebuilds the font at the UI scale and scales every style size. Between frames only.</summary>
    private void ApplyUiScale()
    {
        Ui.UiScaleDirty = false;
        float scale = Ui.UiScale;
        if (Math.Abs(scale - _appliedScale) < 0.001f) return;
        _appliedScale = scale;

        var io = ImGui.GetIO();
        io.Fonts.Clear();
        var segoe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "segoeui.ttf");
        if (File.Exists(segoe)) io.Fonts.AddFontFromFileTTF(segoe, MathF.Round(17 * scale));
        else io.Fonts.AddFontDefault();
        _imgui.RebuildFontAtlas();

        ImGui.StyleColorsDark();
        var s = ImGui.GetStyle();
        s.WindowPadding = new Vector2(8, 8) * scale;
        s.FramePadding = new Vector2(6, 4) * scale;
        s.CellPadding = new Vector2(6, 3) * scale;
        s.ItemSpacing = new Vector2(8, 5) * scale;
        s.ItemInnerSpacing = new Vector2(5, 4) * scale;
        s.IndentSpacing = 18 * scale;
        s.ScrollbarSize = 14 * scale;
        s.GrabMinSize = 10 * scale;
        s.TabBarBorderSize = 1;
        s.WindowRounding = 4 * scale;
        s.ChildRounding = 4 * scale;
        s.FrameRounding = 3 * scale;
        s.PopupRounding = 4 * scale;
        s.ScrollbarRounding = 6 * scale;
        s.GrabRounding = 3 * scale;
        s.TabRounding = 4 * scale;
        s.WindowMenuButtonPosition = ImGuiDir.None;

        var c = s.Colors;
        c[(int)ImGuiCol.WindowBg] = new Vector4(0.13f, 0.12f, 0.15f, 1f);
        c[(int)ImGuiCol.ChildBg] = new Vector4(0.11f, 0.10f, 0.13f, 1f);
        c[(int)ImGuiCol.PopupBg] = new Vector4(0.15f, 0.14f, 0.18f, 0.98f);
        c[(int)ImGuiCol.TitleBg] = new Vector4(0.10f, 0.09f, 0.12f, 1f);
        c[(int)ImGuiCol.TitleBgActive] = new Vector4(0.16f, 0.14f, 0.21f, 1f);
        c[(int)ImGuiCol.MenuBarBg] = new Vector4(0.10f, 0.09f, 0.12f, 1f);
        c[(int)ImGuiCol.Header] = new Vector4(0.33f, 0.27f, 0.50f, 0.55f);
        c[(int)ImGuiCol.HeaderHovered] = new Vector4(0.40f, 0.33f, 0.62f, 0.80f);
        c[(int)ImGuiCol.HeaderActive] = new Vector4(0.45f, 0.37f, 0.70f, 1f);
        c[(int)ImGuiCol.Button] = new Vector4(0.24f, 0.22f, 0.30f, 1f);
        c[(int)ImGuiCol.ButtonHovered] = new Vector4(0.36f, 0.30f, 0.52f, 1f);
        c[(int)ImGuiCol.ButtonActive] = new Vector4(0.46f, 0.37f, 0.72f, 1f);
        c[(int)ImGuiCol.FrameBg] = new Vector4(0.19f, 0.18f, 0.24f, 1f);
        c[(int)ImGuiCol.FrameBgHovered] = new Vector4(0.25f, 0.23f, 0.32f, 1f);
        c[(int)ImGuiCol.FrameBgActive] = new Vector4(0.30f, 0.27f, 0.40f, 1f);
        c[(int)ImGuiCol.Tab] = new Vector4(0.16f, 0.14f, 0.20f, 1f);
        c[(int)ImGuiCol.TabHovered] = new Vector4(0.40f, 0.33f, 0.62f, 1f);
        c[(int)ImGuiCol.TabSelected] = new Vector4(0.30f, 0.25f, 0.45f, 1f);
        c[(int)ImGuiCol.TabDimmed] = new Vector4(0.13f, 0.12f, 0.16f, 1f);
        c[(int)ImGuiCol.TabDimmedSelected] = new Vector4(0.22f, 0.19f, 0.30f, 1f);
        c[(int)ImGuiCol.SliderGrab] = new Vector4(0.55f, 0.45f, 0.85f, 1f);
        c[(int)ImGuiCol.SliderGrabActive] = new Vector4(0.65f, 0.55f, 0.95f, 1f);
        c[(int)ImGuiCol.CheckMark] = new Vector4(0.70f, 0.60f, 1.00f, 1f);
        c[(int)ImGuiCol.DockingPreview] = new Vector4(0.55f, 0.45f, 0.85f, 0.6f);
        c[(int)ImGuiCol.Separator] = new Vector4(0.28f, 0.26f, 0.34f, 1f);
    }
}
