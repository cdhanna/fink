using System.Runtime.InteropServices;

namespace Fink.App;

/// <summary>The Windows open/save file dialogs (comdlg32). Return null when cancelled.</summary>
internal static class FileDialogs
{
    private const string Filter = "Fink project (*.fink)\0*.fink\0All files (*.*)\0*.*\0\0";

    public static string? Open(string? initialDir) => Show(save: false, initialDir, null);

    public static string? Save(string? initialDir, string? fileName) => Show(save: true, initialDir, fileName);

    private static string? Show(bool save, string? initialDir, string? fileName)
    {
        if (!OperatingSystem.IsWindows()) return null;
        const int max = 4096;
        var buffer = Marshal.AllocHGlobal(max * 2);
        try
        {
            var initial = (fileName ?? "") + "\0";
            Marshal.Copy(initial.ToCharArray(), 0, buffer, Math.Min(initial.Length, max - 1));
            var ofn = new OpenFileName
            {
                lStructSize = Marshal.SizeOf<OpenFileName>(),
                hwndOwner = GetActiveWindow(),
                lpstrFilter = Filter,
                nFilterIndex = 1,
                lpstrFile = buffer,
                nMaxFile = max,
                lpstrInitialDir = initialDir,
                lpstrTitle = save ? "Save Fink project" : "Open Fink project",
                lpstrDefExt = "fink",
                Flags = OfnExplorer | OfnNoChangeDir | OfnPathMustExist | (save ? OfnOverwritePrompt : OfnFileMustExist),
            };
            bool ok = save ? GetSaveFileNameW(ref ofn) : GetOpenFileNameW(ref ofn);
            return ok ? Marshal.PtrToStringUni(buffer) : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private const int OfnOverwritePrompt = 0x2, OfnNoChangeDir = 0x8, OfnPathMustExist = 0x800,
        OfnFileMustExist = 0x1000, OfnExplorer = 0x80000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenFileName
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public string lpstrFilter;
        public string? lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public IntPtr lpstrFile;
        public int nMaxFile;
        public string? lpstrFileTitle;
        public int nMaxFileTitle;
        public string? lpstrInitialDir;
        public string? lpstrTitle;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        public string? lpstrDefExt;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public string? lpTemplateName;
        public IntPtr pvReserved;
        public int dwReserved;
        public int FlagsEx;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetOpenFileNameW(ref OpenFileName ofn);

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetSaveFileNameW(ref OpenFileName ofn);

    [DllImport("user32.dll")]
    private static extern IntPtr GetActiveWindow();
}
