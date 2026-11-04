using Fade.MonoGame.Contracts;
using Fade.MonoGame.Core;   // Game1, GameReloader
using Fade.MonoGame.Fink;   // FinkSystem
using FadeBasic.Launch;     // GeneratedFade (made by FadeBasic.Build)

namespace FinkSample;

/// <summary>
/// The engine's Game1 runs the window, the frame loop and the program; this hands it the program
/// and the FinkSystem, which plays the Fink animations the program asks for.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
#if FADE_CONTENT_HOTRELOAD
        // Debug: the program is compiled from source at runtime and watched, so an edit to a
        // .fbasic shows the reload border, and F1 (restart) / F2 (keep state) swap it in.
        var projectDir = GameReloader.GetRoot();
        var csprojPath = string.IsNullOrEmpty(projectDir) || !Directory.Exists(projectDir)
            ? null
            : Directory.GetFiles(projectDir, "*.csproj").FirstOrDefault();
        if (!string.IsNullOrEmpty(csprojPath))
        {
            var commands = new GeneratedFade().CommandCollection;
            GameReloader.WatchFiles(csprojPath, commands);
            using var devGame = new Game1(GameReloader.LatestBuild ?? new GeneratedFade(), windowWidth: 1280, windowHeight: 720);
            devGame.Services.AddService(typeof(Fade.MonoGame.Content.IContentBuilder), new Fade.MonoGame.Content.FadeContentBuilder());
            devGame.Services.AddService(typeof(IFadeHostSystem), new FinkSystem());
            devGame.Run();
            return 0;
        }
#endif
        using var game = new Game1(new GeneratedFade(), windowWidth: 1280, windowHeight: 720);
        game.Services.AddService(typeof(IFadeHostSystem), new FinkSystem());
        game.Run();
        return 0;
    }
}
