using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

// FinkLauncher [file.fink ...]
//
// Finds the newest build of Fink under <repo>/src/Fink.App/bin, copies it (once per build) to
// %LOCALAPPDATA%\Fink\builds\<build time>, and runs that copy with the same arguments. The repo
// is read from launcher.json next to this exe ({"repo": "C:/proj/fink"}), or FINK_REPO.

const int KeepBuilds = 3;

try
{
    var repo = FindRepo() ?? throw new InvalidOperationException(
        "Don't know where the Fink repo is. Run tools/install-launcher.ps1 again, or set FINK_REPO.");
    var source = NewestBuild(repo) ?? throw new InvalidOperationException(
        $"No build of Fink under {Path.Combine(repo, "src", "Fink.App", "bin")}. Build it first:\n\ndotnet build {Path.Combine(repo, "src", "Fink.App")}");

    var buildsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Fink", "builds");
    var target = Path.Combine(buildsDir, BuildTime(source).ToString("yyyyMMdd-HHmmss"));
    if (!File.Exists(Path.Combine(target, ".complete")))
    {
        WaitUntilSettled(source);
        var temp = target + ".copying-" + Environment.ProcessId;
        CopyBuild(source, temp);
        File.WriteAllText(Path.Combine(temp, ".complete"), source);
        if (Directory.Exists(target)) TryDelete(target); // a half copy from a crash
        Directory.Move(temp, target);
    }
    Prune(buildsDir, target);

    var start = new ProcessStartInfo(Path.Combine(target, "Fink.exe")) { UseShellExecute = false };
    foreach (var a in args) start.ArgumentList.Add(a);
    var file = args.FirstOrDefault(a => !a.StartsWith("--") && File.Exists(a));
    start.WorkingDirectory = file != null ? Path.GetDirectoryName(Path.GetFullPath(file))! : target;
    Process.Start(start);
    return 0;
}
catch (Exception e)
{
    MessageBoxW(IntPtr.Zero, e.Message, "Fink launcher", 0x10);
    return 1;
}

static string? FindRepo()
{
    var env = Environment.GetEnvironmentVariable("FINK_REPO");
    if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env)) return env;
    var config = Path.Combine(AppContext.BaseDirectory, "launcher.json");
    if (!File.Exists(config)) return null;
    using var json = JsonDocument.Parse(File.ReadAllText(config));
    return json.RootElement.TryGetProperty("repo", out var r) && Directory.Exists(r.GetString()) ? r.GetString() : null;
}

// Every folder under bin that holds a Fink.exe (Debug, Release...); the one built last.
static string? NewestBuild(string repo)
{
    var bin = Path.Combine(repo, "src", "Fink.App", "bin");
    if (!Directory.Exists(bin)) return null;
    return Directory.EnumerateFiles(bin, "Fink.exe", SearchOption.AllDirectories)
        .Select(Path.GetDirectoryName)
        .OfType<string>()
        .Where(d => File.Exists(Path.Combine(d, "Fink.dll")))
        .OrderByDescending(BuildTime)
        .FirstOrDefault();
}

// When a build last changed: its newest dll (a change only to Fink.Core leaves Fink.dll alone).
static DateTime BuildTime(string dir) =>
    Directory.EnumerateFiles(dir, "*.dll").Select(File.GetLastWriteTime).DefaultIfEmpty(DateTime.MinValue).Max();

// Don't copy a build that's still being written: wait (a little) until nothing has changed for 2s.
static void WaitUntilSettled(string dir)
{
    for (int i = 0; i < 30; i++)
    {
        if (DateTime.Now - BuildTime(dir) > TimeSpan.FromSeconds(2)) return;
        Thread.Sleep(500);
    }
}

// Everything but other platforms' native libraries (most of the size).
static void CopyBuild(string from, string to)
{
    foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
    {
        var rel = Path.GetRelativePath(from, file);
        var parts = rel.Split(Path.DirectorySeparatorChar);
        if (parts.Length > 2 && parts[0] == "runtimes" && !parts[1].Equals("win-x64", StringComparison.OrdinalIgnoreCase)) continue;
        var dest = Path.Combine(to, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Copy(file, dest, overwrite: true);
    }
}

// Keep the newest few copies. One that's running can't be deleted, and stays until next time.
static void Prune(string buildsDir, string keep)
{
    var old = new DirectoryInfo(buildsDir).GetDirectories()
        .OrderByDescending(d => d.Name)
        .Where(d => !d.FullName.Equals(keep, StringComparison.OrdinalIgnoreCase))
        .Skip(KeepBuilds - 1);
    foreach (var d in old) TryDelete(d.FullName);
}

static void TryDelete(string dir)
{
    try { Directory.Delete(dir, recursive: true); } catch { /* in use: next time */ }
}

[DllImport("user32.dll", CharSet = CharSet.Unicode)]
static extern int MessageBoxW(IntPtr hwnd, string text, string caption, uint type);
