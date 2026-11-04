using System.Reflection;
using Fink.Core;
using Fink.Editor;
using ImGuiNET;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Content;
using Microsoft.Xna.Framework.Content.Pipeline;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Graphics.Fade;
using MonoGame.Framework.Content.Pipeline.Builder;
using Num = System.Numerics;
using FadeSpriteBatch = Microsoft.Xna.Framework.Graphics.Fade.SpriteBatch;

namespace Fink.App;

/// <summary>
/// The shader preview: the current frame drawn the way a fade.mono game draws a sprite, through
/// the engine's own FadeSpriteBatch and a game .fx file, with an input for every parameter.
/// Shaders are compiled in-process with MonoGame's content pipeline for DesktopGL (what Fink
/// runs on) and recompiled whenever the file, or anything next to it, is saved.
/// </summary>
public sealed partial class FinkGame
{
    private ShaderPreviewState Shader => _session.Shader;

    private FadeSpriteEffect? _fadeBaseEffect;
    private FadeSpriteBatch? _fadeBatch;
    private Effect? _previewEffect;
    private ContentManager? _previewContent;
    private string _previewEffectFile = "";
    private RenderTarget2D? _previewTarget;
    private IntPtr _previewTargetId;
    private Texture2D? _previewSprite;
    private uint[] _previewPixels = Array.Empty<uint>();
    private readonly Dictionary<string, Texture2D?> _previewTextures = new();

    private Task<CompileResult>? _compile;
    private string _compilingFile = "";
    private DateTime _compiledStamp;
    private double _nextPoll;
    private int _compileSerial;
    private readonly string _shaderWork = Path.Combine(Path.GetTempPath(), "fink-shaders", Environment.ProcessId.ToString());

    private List<string> _fxFiles = new();
    private string _fxFilesFolder = "\0";
    private double _previewStart = -1;

    private sealed record CompileResult(string File, DateTime Stamp, string? OutDir, string? Name, string? Error,
        Dictionary<string, float[]>? Defaults = null);

    /// <summary>Initial values written in the shader source (`float Amount = 0.5;`), by parameter name.</summary>
    private Dictionary<string, float[]> _shaderDefaults = new();

    /// <summary>Fink's own sample shaders, shipped next to the exe.</summary>
    private static readonly string SamplesFolder = Path.Combine(AppContext.BaseDirectory, "shaders");

    private static readonly System.Text.RegularExpressions.Regex DeclRx = new(
        @"^(?:uniform\s+|static\s+const\s+)?(float[1-4]?|int|bool)\s+(\w+)\s*=\s*([^;]+);",
        System.Text.RegularExpressions.RegexOptions.Multiline);
    private static readonly System.Text.RegularExpressions.Regex NumberRx = new(@"-?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?");
    private static readonly System.Text.RegularExpressions.Regex IncludeRx = new(@"^\s*#include\s+""([^""]+)""",
        System.Text.RegularExpressions.RegexOptions.Multiline);

    /// <summary>
    /// MonoGame keeps a shader's initial values only on some backends, so the preview reads them
    /// from the source: top-level declarations with an "=" in the file and the files it includes.
    /// </summary>
    private static Dictionary<string, float[]> ReadDefaults(string fx)
    {
        var result = new Dictionary<string, float[]>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Read(string path)
        {
            if (!seen.Add(Path.GetFullPath(path)) || !File.Exists(path)) return;
            var text = File.ReadAllText(path);
            foreach (System.Text.RegularExpressions.Match inc in IncludeRx.Matches(text))
                Read(Path.Combine(Path.GetDirectoryName(path)!, inc.Groups[1].Value));
            foreach (System.Text.RegularExpressions.Match m in DeclRx.Matches(text))
            {
                var expr = m.Groups[3].Value.Trim();
                var v = new float[4];
                if (expr == "true") v[0] = 1;
                else
                {
                    var nums = NumberRx.Matches(expr.Replace("float4", "").Replace("float3", "").Replace("float2", ""))
                        .Select(n => float.Parse(n.Value, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                    if (nums.Length == 0) continue;
                    for (int i = 0; i < 4; i++) v[i] = nums[Math.Min(i, nums.Length - 1)];
                    if (nums.Length == 1 && m.Groups[1].Value is "float2" or "float3" or "float4") { }
                    else if (nums.Length < 4) for (int i = nums.Length; i < 4; i++) v[i] = 0;
                }
                result[m.Groups[2].Value] = v;
            }
        }
        try { Read(fx); } catch { /* defaults are a nicety */ }
        return result;
    }

    // ---------------------------------------------------------------- compiling

    /// <summary>Newest write time of the shader and the files beside it (its #includes).</summary>
    private static DateTime ShaderStamp(string fx)
    {
        try
        {
            var dir = Path.GetDirectoryName(fx)!;
            return Directory.EnumerateFiles(dir, "*.fx*").Select(File.GetLastWriteTimeUtc).DefaultIfEmpty().Max();
        }
        catch { return default; }
    }

    private static CompileResult Compile(string fx, string work, int serial)
    {
        var stamp = ShaderStamp(fx);
        try
        {
            var outDir = Path.Combine(work, "out");
            var obj = Path.Combine(work, "obj");
            Directory.CreateDirectory(outDir);
            var logger = new PreviewLogger();
            var pm = new PipelineManager(Path.GetDirectoryName(fx)!, outDir, obj)
            {
                Platform = TargetPlatform.DesktopGL,
                Profile = GraphicsProfile.HiDef,
                RethrowExceptions = true,
                CompressContent = false,
                Logger = logger,
            };
            var name = $"preview{serial}";
            pm.BuildContent(fx, Path.Combine(outDir, name + ".xnb"), "EffectImporter", "EffectProcessor");
            return new CompileResult(fx, stamp, outDir, name, null, ReadDefaults(fx));
        }
        catch (Exception e)
        {
            var msg = e is InvalidContentException ice && ice.ContentIdentity?.SourceFilename is { } src
                ? $"{Path.GetFileName(src)} {ice.ContentIdentity.FragmentIdentifier}: {ice.Message}"
                : e.InnerException?.Message ?? e.Message;
            // the compiler escapes its paths; show them short
            msg = msg.Replace(@"\\", @"\");
            var dir = Path.GetDirectoryName(Path.GetFullPath(fx))!;
            msg = msg.Replace(dir + Path.DirectorySeparatorChar, "", StringComparison.OrdinalIgnoreCase)
                     .Replace(dir.Replace('\\', '/') + "/", "", StringComparison.OrdinalIgnoreCase);
            msg = msg.Replace($"{Path.GetFileName(fx)} : ", "");
            return new CompileResult(fx, stamp, null, null, msg.Trim());
        }
    }

#pragma warning disable CS0672 // the pipeline still calls these
    private sealed class PreviewLogger : ContentBuildLogger
    {
        public override void LogMessage(string message, params object[] messageArgs) { }
        public override void LogImportantMessage(string message, params object[] messageArgs) { }
        public override void LogWarning(string helpLink, ContentIdentity contentIdentity, string message, params object[] messageArgs) { }
    }
#pragma warning restore CS0672

    /// <summary>Starts a compile when the file changed, and swaps in finished ones. Main thread.</summary>
    private void PumpShaderCompile()
    {
        var file = Shader.File;
        if (_compile is { IsCompleted: true })
        {
            var r = _compile.Result;
            _compile = null;
            if (r.Error != null)
            {
                Shader.Status = r.Error;
            }
            else
            {
                try
                {
                    var content = new ContentManager(Services, r.OutDir!);
                    var effect = content.Load<Effect>(r.Name!);
                    _previewContent?.Dispose();
                    _previewContent = content;
                    _previewEffect = effect;
                    _previewEffectFile = r.File;
                    _shaderDefaults = r.Defaults ?? new();
                    Shader.Status = "ok";
                }
                catch (Exception e)
                {
                    Shader.Status = $"Loading the compiled shader failed: {e.Message}";
                }
            }
            _compiledStamp = r.Stamp;
            _compilingFile = r.File;
        }

        if (string.IsNullOrEmpty(file))
        {
            if (_previewEffect != null) { _previewContent?.Dispose(); _previewContent = null; _previewEffect = null; _previewEffectFile = ""; }
            Shader.Status = "";
            return;
        }
        if (_compile != null) return;
        bool changedFile = file != _compilingFile;
        if (!changedFile && Ed.Time < _nextPoll) return;
        _nextPoll = Ed.Time + 0.5;
        if (!File.Exists(file)) { Shader.Status = $"Not found: {file}"; _compilingFile = file; return; }
        if (!changedFile && ShaderStamp(file) == _compiledStamp) return;

        if (changedFile) { _previewContent?.Dispose(); _previewContent = null; _previewEffect = null; }
        Shader.Status = "compiling";
        _compilingFile = file;
        int serial = ++_compileSerial;
        var work = _shaderWork;
        _compile = Task.Run(() => Compile(file, work, serial));
    }

    /// <summary>For scripts: true once the current shader has finished compiling (or failed).</summary>
    private bool ShaderSettled => _compile == null &&
        (string.IsNullOrEmpty(Shader.File) || (_compilingFile == Shader.File && Shader.Status != "compiling"));

    // ---------------------------------------------------------------- drawing

    private void LoadFadeSpriteBatch()
    {
        // the engine's baseline sprite shader is baked into Fade.MonoGame.Game as a resource
        var asm = typeof(FadeSpriteBatch).Assembly;
        var resource = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("FadeSpriteBatchEffect.ogl.xnb", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Fade.MonoGame.Game has no baked FadeSpriteBatchEffect.ogl.xnb");
        var content = new EmbeddedContent(Services, asm, resource);
        _fadeBaseEffect = new FadeSpriteEffect(content.Load<Effect>("FadeSpriteBatchEffect"));
        _fadeBatch = new FadeSpriteBatch(GraphicsDevice, _fadeBaseEffect);
    }

    private sealed class EmbeddedContent : ContentManager
    {
        private readonly Assembly _asm;
        private readonly string _resource;
        public EmbeddedContent(IServiceProvider services, Assembly asm, string resource) : base(services)
        {
            _asm = asm;
            _resource = resource;
        }
        protected override Stream OpenStream(string assetName) =>
            _asm.GetManifestResourceStream(_resource) ?? throw new ContentLoadException(_resource);
    }

    /// <summary>Renders the preview into its target. Before ImGui's frame, while the panel is open.</summary>
    private void RenderShaderPreview()
    {
        PumpShaderCompile();
        if (!Ui.IsOpen("Shader") && _script == null) return;
        if (_previewStart < 0) _previewStart = Ed.Time;
        if (_fadeBatch == null)
        {
            try { LoadFadeSpriteBatch(); }
            catch (Exception e) { Shader.Status = $"Couldn't load the engine's sprite shader: {e.Message}"; return; }
        }

        var doc = Ed.Document;
        int zoom = Math.Clamp(Shader.Zoom, 1, 16), margin = Math.Clamp(Shader.Margin, 0, 64);
        int tw = (doc.Width + margin * 2) * zoom, th = (doc.Height + margin * 2) * zoom;
        if (_previewTarget == null || _previewTarget.Width != tw || _previewTarget.Height != th)
        {
            if (_previewTarget != null) { _imgui.UnbindTexture(_previewTargetId); _previewTarget.Dispose(); }
            _previewTarget = new RenderTarget2D(GraphicsDevice, tw, th);
            _previewTargetId = _imgui.BindTexture(_previewTarget);
        }
        if (_previewSprite == null || _previewSprite.Width != doc.Width || _previewSprite.Height != doc.Height)
        {
            _previewSprite?.Dispose();
            _previewSprite = new Texture2D(GraphicsDevice, doc.Width, doc.Height, false, SurfaceFormat.Color);
            _previewPixels = new uint[doc.Width * doc.Height];
        }

        // the frame as the game would load it: palette resolved, alpha premultiplied
        var layers = doc.Layers.Where(l => l.Visible && l.Role != LayerRole.Reference).ToList();
        Exporter.Flatten(doc, layers, Ed.FrameIndex, _previewPixels);
        for (int i = 0; i < _previewPixels.Length; i++)
        {
            uint p = _previewPixels[i], a = p >> 24;
            if (a == 255 || a == 0) { if (a == 0) _previewPixels[i] = 0; continue; }
            uint r = (p & 0xFF) * a / 255, g = ((p >> 8) & 0xFF) * a / 255, b = ((p >> 16) & 0xFF) * a / 255;
            _previewPixels[i] = r | (g << 8) | (b << 16) | (a << 24);
        }
        _previewSprite.SetData(_previewPixels);

        var effect = _previewEffect != null && _previewEffectFile == Shader.File ? _previewEffect : null;
        if (effect != null) ApplyShaderParams(effect, _previewSprite);

        var bg = Shader.Background;
        GraphicsDevice.SetRenderTarget(_previewTarget);
        GraphicsDevice.Clear(new Color(bg[0], bg[1], bg[2]));
        try
        {
            var vc = Shader.VertexColor;
            _fadeBatch!.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend,
                Shader.LinearSampling ? SamplerState.LinearClamp : SamplerState.PointClamp, effect: effect);
            _fadeBatch.Draw(_previewSprite, new Vector2(margin * zoom), null,
                new Color(vc[0], vc[1], vc[2], vc[3]), 0, Vector2.Zero, new Vector2(zoom), SpriteEffects.None, 0,
                new SpriteTexCoord1(V4(Shader.TexCoord1)), new SpriteTexCoord1(V4(Shader.TexCoord2)));
            _fadeBatch.End();
        }
        catch (Exception e)
        {
            Shader.Status = $"Drawing failed: {e.Message}";
        }
        GraphicsDevice.SetRenderTarget(null);
    }

    private static Vector4 V4(float[] v) => new(v[0], v[1], v[2], v[3]);

    /// <summary>Is this a parameter the sprite batch fills in itself?</summary>
    private static bool IsAutomatic(EffectParameter p) =>
        p.Name == "MatrixTransform" || (IsTexture(p) && p.Name is "SpriteTexture" or "Texture");

    private static bool IsTexture(EffectParameter p) =>
        p.ParameterType is EffectParameterType.Texture or EffectParameterType.Texture2D;

    private static bool IsEditable(EffectParameter p) =>
        p.Elements.Count == 0 && (IsTexture(p) ||
            (p.ParameterClass is EffectParameterClass.Scalar or EffectParameterClass.Vector && p.RowCount <= 1 &&
             p.ParameterType is EffectParameterType.Single or EffectParameterType.Int32 or EffectParameterType.Bool));

    /// <summary>The stored setting for a parameter, made on first sight with a guess from its name.</summary>
    private ShaderParamState ParamState(EffectParameter p)
    {
        var all = Shader.ParamsFor(Shader.File);
        if (all.TryGetValue(p.Name, out var s)) return s;
        s = new ShaderParamState();
        var lower = p.Name.ToLowerInvariant();
        if (IsTexture(p)) s.Texture = "";
        else
        {
            try
            {
                // MonoGame keeps initial values only on some backends; zeros otherwise
                switch (p.ColumnCount)
                {
                    case 1 when p.ParameterType == EffectParameterType.Single: s.Values[0] = p.GetValueSingle(); break;
                    case 2: var v2 = p.GetValueVector2(); s.Values = new[] { v2.X, v2.Y, 0f, 0f }; break;
                    case 3: var v3 = p.GetValueVector3(); s.Values = new[] { v3.X, v3.Y, v3.Z, 0f }; break;
                    case 4: var v4 = p.GetValueVector4(); s.Values = new[] { v4.X, v4.Y, v4.Z, v4.W }; break;
                }
            }
            catch { /* unreadable: stays zero */ }
            if (s.Values.All(x => x == 0) && _shaderDefaults.TryGetValue(p.Name, out var d)) s.Values = (float[])d.Clone();
            if (lower.Contains("time")) s.Source = ParamSource.Time;
            else if (lower.Contains("texel")) s.Source = ParamSource.TexelSize;
            else if (lower.Contains("texturesize") || lower.Contains("spritesize")) s.Source = ParamSource.TextureSize;
            s.AsColor = p.ColumnCount >= 3 && (lower.Contains("color") || lower.Contains("colour") || lower.Contains("tint"));
            if (s.AsColor && s.Values.All(v => v == 0) && !_shaderDefaults.ContainsKey(p.Name)) s.Values = new[] { 1f, 1f, 1f, 1f };
        }
        all[p.Name] = s;
        return s;
    }

    private void ApplyShaderParams(Effect effect, Texture2D sprite)
    {
        float time = (float)(Ed.Time - _previewStart);
        foreach (var p in effect.Parameters)
        {
            if (p.Name == "MatrixTransform") continue;
            if (IsTexture(p))
            {
                if (IsAutomatic(p)) { p.SetValue(sprite); continue; }
                var s = ParamState(p);
                p.SetValue(s.Texture == "sprite" ? sprite : LoadPreviewTexture(s.Texture));
                continue;
            }
            if (!IsEditable(p)) continue;
            var st = ParamState(p);
            var v = st.Source switch
            {
                ParamSource.Time => new[] { time, time, time, time },
                ParamSource.TexelSize => new[] { 1f / sprite.Width, 1f / sprite.Height, 0, 0 },
                ParamSource.TextureSize => new[] { (float)sprite.Width, sprite.Height, 0, 0 },
                _ => st.Values,
            };
            try
            {
                switch (p.ParameterType, p.ColumnCount)
                {
                    case (EffectParameterType.Bool, _): p.SetValue(v[0] != 0); break;
                    case (EffectParameterType.Int32, _): p.SetValue((int)v[0]); break;
                    case (_, 1): p.SetValue(v[0]); break;
                    case (_, 2): p.SetValue(new Vector2(v[0], v[1])); break;
                    case (_, 3): p.SetValue(new Vector3(v[0], v[1], v[2])); break;
                    case (_, 4): p.SetValue(new Vector4(v[0], v[1], v[2], v[3])); break;
                }
            }
            catch { /* a type we guessed wrong: leave it */ }
        }
    }

    private Texture2D? LoadPreviewTexture(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (_previewTextures.TryGetValue(path, out var t)) return t;
        try { t = Texture2D.FromFile(GraphicsDevice, path); }
        catch { t = null; }
        _previewTextures[path] = t;
        return t;
    }

    // ---------------------------------------------------------------- the panel

    private void DrawShaderPanel()
    {
        var s = Shader;
        float sc = Ui.UiScale;

        // ---- which shader
        var folder = s.Folder;
        ImGui.SetNextItemWidth(-90 * sc);
        if (ImGui.InputTextWithHint("##folder", "Shader folder, e.g. C:/proj/mygame/Assets", ref folder, 512)) s.Folder = folder;
        ImGui.SameLine();
        if (ImGui.Button("Rescan") || _fxFilesFolder != s.Folder) ScanFxFiles();
        ImGui.SetNextItemWidth(-90 * sc);
        string current = string.IsNullOrEmpty(s.File) ? "(engine sprite shader)"
            : s.File.StartsWith(SamplesFolder, StringComparison.OrdinalIgnoreCase) ? $"sample: {Path.GetFileNameWithoutExtension(s.File)}"
            : RelativeTo(s.Folder, s.File);
        if (ImGui.BeginCombo("##fx", current, ImGuiComboFlags.HeightLarge))
        {
            if (ImGui.Selectable("(engine sprite shader)", string.IsNullOrEmpty(s.File))) s.File = "";
            if (_fxFiles.Count > 0 || !string.IsNullOrWhiteSpace(s.Folder)) ImGui.SeparatorText("Shader folder");
            foreach (var f in _fxFiles)
                if (ImGui.Selectable(RelativeTo(s.Folder, f), string.Equals(f, s.File, StringComparison.OrdinalIgnoreCase))) s.File = f;
            if (_fxFiles.Count == 0 && !string.IsNullOrWhiteSpace(s.Folder)) ImGui.TextDisabled("No .fx files in that folder.");
            ImGui.SeparatorText("Fink samples");
            foreach (var f in SampleFiles())
                if (ImGui.Selectable($"{Path.GetFileNameWithoutExtension(f)}##sample", string.Equals(f, s.File, StringComparison.OrdinalIgnoreCase))) s.File = f;
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        ImGui.TextDisabled("shader");

        // ---- status
        if (s.Status == "compiling") ImGui.TextColored(new Num.Vector4(1f, 0.85f, 0.4f, 1), "Compiling...");
        else if (s.Status == "ok") ImGui.TextDisabled("Compiled. Recompiles when you save it.");
        else if (s.Status.Length > 0)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Num.Vector4(1f, 0.45f, 0.45f, 1));
            ImGui.TextWrapped(s.Status);
            ImGui.PopStyleColor();
        }
        else ImGui.TextDisabled("The engine's plain sprite shader. Pick a game shader above.");

        // ---- view options
        int zoom = s.Zoom;
        ImGui.SetNextItemWidth(110 * sc);
        if (ImGui.SliderInt("##zoom", ref zoom, 1, 16, "zoom %dx")) s.Zoom = zoom;
        ImGui.SameLine();
        int margin = s.Margin;
        ImGui.SetNextItemWidth(110 * sc);
        if (ImGui.SliderInt("##margin", ref margin, 0, 32, "margin %d px")) s.Margin = margin;
        ImGui.SameLine();
        var bg = new Num.Vector3(s.Background[0], s.Background[1], s.Background[2]);
        if (ImGui.ColorEdit3("##bg", ref bg, ImGuiColorEditFlags.NoInputs)) s.Background = new[] { bg.X, bg.Y, bg.Z };
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Background");
        ImGui.SameLine();
        bool linear = s.LinearSampling;
        if (ImGui.Checkbox("Linear", ref linear)) s.LinearSampling = linear;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Linear texture filtering instead of point (nearest).");
        ImGui.SameLine();
        if (ImGui.Button("Restart time")) _previewStart = Ed.Time;

        // ---- the picture
        if (_previewTarget != null)
        {
            var size = new Num.Vector2(_previewTarget.Width, _previewTarget.Height);
            float avail = ImGui.GetContentRegionAvail().X;
            if (size.X > avail && avail > 32) size *= avail / size.X;
            ImGui.Image(_previewTargetId, size);
        }

        // ---- inputs
        if (ImGui.CollapsingHeader("Per-sprite values", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.TextDisabled("What the game sets per sprite: its color, and texcoord1 / texcoord2.");
            var vc = new Num.Vector4(s.VertexColor[0], s.VertexColor[1], s.VertexColor[2], s.VertexColor[3]);
            if (ImGui.ColorEdit4("color", ref vc, ImGuiColorEditFlags.AlphaPreviewHalf)) s.VertexColor = new[] { vc.X, vc.Y, vc.Z, vc.W };
            var t1 = new Num.Vector4(s.TexCoord1[0], s.TexCoord1[1], s.TexCoord1[2], s.TexCoord1[3]);
            if (ImGui.DragFloat4("texcoord1", ref t1, 0.01f)) s.TexCoord1 = new[] { t1.X, t1.Y, t1.Z, t1.W };
            var t2 = new Num.Vector4(s.TexCoord2[0], s.TexCoord2[1], s.TexCoord2[2], s.TexCoord2[3]);
            if (ImGui.DragFloat4("texcoord2", ref t2, 0.01f)) s.TexCoord2 = new[] { t2.X, t2.Y, t2.Z, t2.W };
        }

        var effect = _previewEffect != null && _previewEffectFile == s.File ? _previewEffect : null;
        if (effect != null && ImGui.CollapsingHeader("Shader parameters", ImGuiTreeNodeFlags.DefaultOpen))
        {
            if (ImGui.SmallButton("Reset all")) s.ParamsFor(s.File).Clear();
            if (ImGui.BeginTable("params", 2, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg))
            {
                ImGui.TableSetupColumn("name", ImGuiTableColumnFlags.WidthStretch, 0.38f);
                ImGui.TableSetupColumn("value", ImGuiTableColumnFlags.WidthStretch, 0.62f);
                foreach (var p in effect.Parameters) DrawParam(p);
                ImGui.EndTable();
            }
        }
    }

    private void DrawParam(EffectParameter p)
    {
        ImGui.PushID(p.Name);
        string type = IsTexture(p) ? "texture"
            : p.ParameterType == EffectParameterType.Bool ? "bool"
            : p.ParameterType == EffectParameterType.Int32 ? "int"
            : p.ParameterClass == EffectParameterClass.Matrix ? $"float{p.RowCount}x{p.ColumnCount}"
            : p.ColumnCount > 1 ? $"float{p.ColumnCount}" : "float";
        if (p.Elements.Count > 0) type += $"[{p.Elements.Count}]";

        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        if (IsAutomatic(p) || !IsEditable(p))
        {
            ImGui.TextDisabled(p.Name);
            ImGui.TableSetColumnIndex(1);
            ImGui.TextDisabled(IsAutomatic(p) ? $"{type}, set by the sprite batch" : $"{type}, not editable here");
            ImGui.PopID();
            return;
        }

        var st = ParamState(p);
        float sc = Ui.UiScale;
        int cols = p.ColumnCount;
        bool isFloat = p.ParameterType == EffectParameterType.Single;
        ImGui.Text(p.Name);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(cols >= 3 && isFloat ? $"{type}. Right-click: show as {(st.AsColor ? "numbers" : "a color")}" : type);
        if (cols >= 3 && isFloat && ImGui.IsItemClicked(ImGuiMouseButton.Right)) st.AsColor = !st.AsColor;
        ImGui.TableSetColumnIndex(1);

        if (IsTexture(p))
        {
            bool sprite = st.Texture == "sprite";
            if (ImGui.Checkbox("sprite##sprite", ref sprite)) st.Texture = sprite ? "sprite" : "";
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Use this animation's frame; otherwise a PNG file.");
            if (!sprite)
            {
                ImGui.SameLine();
                var path = st.Texture;
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputTextWithHint("##tex", "a PNG file", ref path, 512)) { st.Texture = path; _previewTextures.Remove(path); }
            }
            ImGui.PopID();
            return;
        }

        // where the value comes from
        if (isFloat)
        {
            ImGui.SetNextItemWidth(62 * sc);
            var src = st.Source;
            var label = src switch { ParamSource.Time => "time", ParamSource.TexelSize => "1/size", ParamSource.TextureSize => "size", _ => "value" };
            if (ImGui.BeginCombo("##src", label))
            {
                if (ImGui.Selectable("value", src == ParamSource.Value)) st.Source = ParamSource.Value;
                if (ImGui.Selectable("time (seconds)", src == ParamSource.Time)) st.Source = ParamSource.Time;
                if (cols >= 2 && ImGui.Selectable("1 / sprite size", src == ParamSource.TexelSize)) st.Source = ParamSource.TexelSize;
                if (cols >= 2 && ImGui.Selectable("sprite size", src == ParamSource.TextureSize)) st.Source = ParamSource.TextureSize;
                ImGui.EndCombo();
            }
            ImGui.SameLine();
        }

        ImGui.SetNextItemWidth(-1);
        var v = st.Values;
        if (st.Source != ParamSource.Value && isFloat)
        {
            ImGui.TextDisabled(st.Source == ParamSource.Time ? "seconds, running" : "from the sprite");
        }
        else if (p.ParameterType == EffectParameterType.Bool)
        {
            bool b = v[0] != 0;
            if (ImGui.Checkbox("##b", ref b)) v[0] = b ? 1 : 0;
        }
        else if (p.ParameterType == EffectParameterType.Int32)
        {
            int i = (int)v[0];
            if (ImGui.DragInt("##i", ref i)) v[0] = i;
        }
        else if (st.AsColor && cols == 3)
        {
            var c = new Num.Vector3(v[0], v[1], v[2]);
            if (ImGui.ColorEdit3("##c", ref c, ImGuiColorEditFlags.Float)) { v[0] = c.X; v[1] = c.Y; v[2] = c.Z; }
        }
        else if (st.AsColor && cols == 4)
        {
            var c = new Num.Vector4(v[0], v[1], v[2], v[3]);
            if (ImGui.ColorEdit4("##c", ref c, ImGuiColorEditFlags.Float | ImGuiColorEditFlags.AlphaPreviewHalf)) { v[0] = c.X; v[1] = c.Y; v[2] = c.Z; v[3] = c.W; }
        }
        else
        {
            switch (cols)
            {
                case 1: ImGui.DragFloat("##f", ref v[0], 0.01f); break;
                case 2: { var x = new Num.Vector2(v[0], v[1]); if (ImGui.DragFloat2("##f", ref x, 0.01f)) { v[0] = x.X; v[1] = x.Y; } break; }
                case 3: { var x = new Num.Vector3(v[0], v[1], v[2]); if (ImGui.DragFloat3("##f", ref x, 0.01f)) { v[0] = x.X; v[1] = x.Y; v[2] = x.Z; } break; }
                default: { var x = new Num.Vector4(v[0], v[1], v[2], v[3]); if (ImGui.DragFloat4("##f", ref x, 0.01f)) { v[0] = x.X; v[1] = x.Y; v[2] = x.Z; v[3] = x.W; } break; }
            }
        }
        ImGui.PopID();
    }

    private static IEnumerable<string> SampleFiles()
    {
        try { return Directory.Exists(SamplesFolder) ? Directory.GetFiles(SamplesFolder, "*.fx").OrderBy(f => f) : Enumerable.Empty<string>(); }
        catch { return Enumerable.Empty<string>(); }
    }

    private void ScanFxFiles()
    {
        _fxFilesFolder = Shader.Folder;
        _fxFiles = new List<string>();
        try
        {
            if (Directory.Exists(Shader.Folder))
                _fxFiles = Directory.EnumerateFiles(Shader.Folder, "*.fx", SearchOption.AllDirectories)
                    .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") &&
                                !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                    .Select(Path.GetFullPath)
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToList();
        }
        catch { /* unreadable folder: empty list */ }
    }

    private static string RelativeTo(string folder, string file)
    {
        try { return Directory.Exists(folder) ? Path.GetRelativePath(folder, file) : file; }
        catch { return file; }
    }
}
