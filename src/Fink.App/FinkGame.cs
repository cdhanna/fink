using System.Runtime.InteropServices;
using Fade.MonoGame.Core;
using Fink.Core;
using Fink.Editor;
using ImGuiNET;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Fink.App;

/// <summary>
/// The editor's MonoGame host. Fink does not use the engine's Game1 (which runs a FadeBasic
/// program); it borrows the engine's ImGuiRenderer and draws everything as docked ImGui panels.
/// All behavior lives in <see cref="Session"/> (Fink.Editor); this class draws it and feeds it
/// input.
/// </summary>
public sealed partial class FinkGame : Game
{
    private readonly GraphicsDeviceManager _graphics;
    private readonly LaunchOptions _options;
    private ImGuiRenderer _imgui = null!;
    private readonly Session _session;
    private EditorState Ed => _session.Editor;
    private UiState Ui => _session.Ui;

    // the flattened current frame, shown in the Canvas panel
    private Texture2D? _canvasTexture;
    private IntPtr _canvasTextureId;
    private uint[] _canvasPixels = Array.Empty<uint>();
    private Document? _canvasDocument;

    private IntPtr _iniPathPtr;


    // automation
    private ScriptRunner? _script;
    private int _scriptWait;
    private string? _shotName;
    private bool _shotCanvasOnly;

    public FinkGame(LaunchOptions options)
    {
        _options = options;
        _graphics = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = 1600,
            PreferredBackBufferHeight = 950,
            SynchronizeWithVerticalRetrace = true,
            HardwareModeSwitch = false,
        };
        IsMouseVisible = true;
        Window.AllowUserResizing = true;
        Window.Title = "Fink";

        _session = new Session(new SettingsPaths(options.SettingsDir));
        foreach (var problem in _session.Load()) Console.WriteLine($"settings: {problem}");

        if (options.ScriptPath != null)
        {
            _script = new ScriptRunner(_session, File.ReadAllText(options.ScriptPath), Path.GetFileName(options.ScriptPath))
            {
                ShaderReady = () => ShaderSettled,
            };
            Directory.CreateDirectory(options.OutDir!);
        }
    }

    protected override unsafe void Initialize()
    {
        base.Initialize();
        _imgui = new ImGuiRenderer(this);

        // ImGui's layout file lives with the other settings, not next to the exe
        Directory.CreateDirectory(_session.Paths.Root);
        bool hadLayout = File.Exists(_session.Paths.LayoutIni);
        _iniPathPtr = Marshal.StringToHGlobalAnsi(_session.Paths.LayoutIni);
        ImGui.GetIO().NativePtr->IniFilename = (byte*)_iniPathPtr;
        if (!hadLayout) Ui.PendingWorkspace = Ui.CurrentWorkspace; // first run: lay out the panels

        if (_options.OpenPath != null) OpenFile(_options.OpenPath);
        else if (_script == null && _session.Leftovers.Count > 0) Ui.ShowRecovery = true;

        ApplyUiScale();
    }

    protected override void Update(GameTime gameTime)
    {
        Ed.Time = gameTime.TotalGameTime.TotalSeconds;
        Ed.Advance(gameTime.ElapsedGameTime.TotalMilliseconds);

        if (Ui.Fullscreen != _graphics.IsFullScreen)
        {
            _graphics.IsFullScreen = Ui.Fullscreen;
            _graphics.ApplyChanges();
        }
        UpdateProject();
        UpdatePen();
        base.Update(gameTime);
    }

    protected override void Draw(GameTime gameTime)
    {
        // between frames: things ImGui doesn't allow mid-frame
        if (Ui.UiScaleDirty) ApplyUiScale();
        BeginPendingWorkspace();

        Ed.Clamp();
        UpdateCanvasTexture();
        RenderShaderPreview();
        UpdateAtlasPreview();

        GraphicsDevice.Clear(new Color(24, 22, 28));

        _imgui.BeforeLayout(gameTime);
        PollHotkeys();
        StepScript();
        DrawUi();
        // The renderer leaves the sampler alone, so this keeps zoomed pixels crisp.
        GraphicsDevice.SamplerStates[0] = SamplerState.PointClamp;
        _imgui.AfterLayout();

        CaptureScreenshotIfDue();

        base.Draw(gameTime);
    }

    private void UpdateCanvasTexture()
    {
        var doc = Ed.Document;
        if (_canvasTexture == null || _canvasDocument != doc)
        {
            if (_canvasTexture != null)
            {
                _imgui.UnbindTexture(_canvasTextureId);
                _canvasTexture.Dispose();
            }
            _canvasTexture = new Texture2D(GraphicsDevice, doc.Width, doc.Height, false, SurfaceFormat.Color);
            _canvasTextureId = _imgui.BindTexture(_canvasTexture);
            _canvasPixels = new uint[doc.Width * doc.Height];
            _canvasDocument = doc;
        }
        // Recomposited every frame: strokes change pixels before they reach the history, and a
        // few hundred thousand pixels is cheap. Dirty-tile updates can come later.
        Compositor.Render(doc, Ed.FrameIndex, _canvasPixels, Ed.Playing ? null : Ed.Onion, overlays: Ed.Selection.Overlays(), colors: Ed.ViewColors);
        if (Ed.ActiveChannel is { } channel) ChannelView.Overlay(doc, Ed.FrameIndex, channel, _canvasPixels);
        _canvasTexture.SetData(_canvasPixels);
    }

    private bool _penErasing;

    /// <summary>The pen's eraser end: a momentary Eraser while the pen is flipped.</summary>
    private void UpdatePen()
    {
        if (!PenInput.Hooked && Ed.Time > 0.5) PenInput.Hook(); // once the window exists
        bool want = Ui.PenEraser && PenInput.Eraser;
        if (want == _penErasing || Ed.Stroking) return;
        _penErasing = want;
        if (want) Ed.BeginMomentary(Momentary.Eraser);
        else Ed.EndMomentary(Momentary.Eraser);
    }

    protected override void OnExiting(object sender, ExitingEventArgs args)
    {
        if (InterceptExit())
        {
            args.Cancel = true;
            return;
        }
        // a clean exit: whatever was unsaved was saved or let go on purpose
        if (_script == null) _session.Recovery.Clear();
        if (_script == null)
        {
            try { _session.Save(); }
            catch (Exception e) { Console.WriteLine($"couldn't save settings: {e.Message}"); }
        }
        base.OnExiting(sender, args);
    }

    // ------------------------------------------------------------------ automation

    private void StepScript()
    {
        if (_script == null || _shotName != null) return;
        if (_scriptWait > 0) { _scriptWait--; return; }

        var y = _script.Next();
        switch (y.Kind)
        {
            case YieldKind.Wait:
                _scriptWait = y.Frames;
                break;
            case YieldKind.Shot:
                _shotName = y.ShotName;
                _shotCanvasOnly = y.CanvasOnly;
                _scriptWait = 3; // let layout, docking and fonts settle
                break;
            case YieldKind.Done:
                var report = _script.Report();
                Console.Write(report);
                File.WriteAllText(Path.Combine(_options.OutDir!, "report.txt"), report);
                Environment.ExitCode = _script.Passed ? 0 : 1;
                _script = null;
                if (!_options.KeepOpen)
                {
                    _exitConfirmed = true; // an automation run never asks about unsaved changes
                    Exit();
                }
                break;
        }
    }

    private void CaptureScreenshotIfDue()
    {
        if (_shotName == null) return;
        if (_scriptWait > 0) { _scriptWait--; return; }

        var path = Path.Combine(_options.OutDir!, _shotName + ".png");
        if (_shotCanvasOnly)
        {
            // the flattened frame, scaled up with whole pixels so it is easy to look at
            var doc = Ed.Document;
            int scale = Math.Max(1, 512 / Math.Max(doc.Width, doc.Height));
            int w = doc.Width * scale, h = doc.Height * scale;
            var big = new uint[w * h];
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                big[y * w + x] = _canvasPixels[(y / scale) * doc.Width + x / scale];
            SavePng(path, w, h, big);
        }
        else
        {
            var pp = GraphicsDevice.PresentationParameters;
            var data = new uint[pp.BackBufferWidth * pp.BackBufferHeight];
            GraphicsDevice.GetBackBufferData(data);
            for (int i = 0; i < data.Length; i++) data[i] |= 0xFF000000; // opaque
            SavePng(path, pp.BackBufferWidth, pp.BackBufferHeight, data);
        }
        Console.WriteLine($"  shot {path}");
        _shotName = null;
    }

    private void SavePng(string path, int w, int h, uint[] pixels)
    {
        using var tex = new Texture2D(GraphicsDevice, w, h, false, SurfaceFormat.Color);
        tex.SetData(pixels);
        using var fs = File.Create(path);
        tex.SaveAsPng(fs, w, h);
    }
}
