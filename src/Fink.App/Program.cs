using Fink.App;

// Fink.exe                                  the editor
// Fink.exe --script <file.fink-test>        run an automation script in the window, then exit
//          [--out <dir>]                    where screenshots and report.txt go (default: <script dir>/out/<script name>)
//          [--settings-dir <dir>]           settings folder (default for scripts: a fresh temp folder,
//                                           so runs never see or change your own layout and keymap)
//          [--keep-open]                    stay open after the script finishes
var options = LaunchOptions.Parse(args);
using var game = new FinkGame(options);
game.Run();
return Environment.ExitCode;

namespace Fink.App
{
    public sealed class LaunchOptions
    {
        public string? ScriptPath { get; private set; }
        public string? OutDir { get; private set; }
        public string? SettingsDir { get; private set; }
        public bool KeepOpen { get; private set; }
        /// <summary>A .fink file to open (Fink.exe hero.fink).</summary>
        public string? OpenPath { get; private set; }

        public static LaunchOptions Parse(string[] args)
        {
            var o = new LaunchOptions();
            for (int i = 0; i < args.Length; i++)
            {
                string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
                switch (args[i])
                {
                    case "--script": o.ScriptPath = Path.GetFullPath(Next()); break;
                    case "--out": o.OutDir = Path.GetFullPath(Next()); break;
                    case "--settings-dir": o.SettingsDir = Path.GetFullPath(Next()); break;
                    case "--keep-open": o.KeepOpen = true; break;
                    default:
                        if (args[i].StartsWith("--")) throw new ArgumentException($"unknown option {args[i]}");
                        o.OpenPath = Path.GetFullPath(args[i]);
                        break;
                }
            }
            if (o.ScriptPath != null)
            {
                o.OutDir ??= Path.Combine(Path.GetDirectoryName(o.ScriptPath)!, "out", Path.GetFileNameWithoutExtension(o.ScriptPath));
                o.SettingsDir ??= Path.Combine(Path.GetTempPath(), "fink-script-" + Guid.NewGuid().ToString("N")[..8]);
            }
            return o;
        }
    }
}
