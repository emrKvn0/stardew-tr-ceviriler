using System.Globalization;
using System.Text;
using StardewModdingAPI;
using StardewValley;

namespace LivingWaters;

internal sealed class EcosystemLogger
{
    private const long MaxLogBytes = 2L * 1024 * 1024;

    private readonly IMonitor Monitor;
    private readonly string ModVersion;
    private readonly object WriteLock = new();
    private readonly List<string> Buffer = new();

    private string CurrentLogPath = string.Empty;
    private bool DirtySinceLastSave;

    public EcosystemLogger(IMonitor monitor, string modVersion)
    {
        this.Monitor = monitor;
        this.ModVersion = modVersion;
    }

    public void ResetForSave(string? saveFolderName)
    {
        lock (this.WriteLock)
        {
            this.Buffer.Clear();
            this.DirtySinceLastSave = false;

            if (string.IsNullOrWhiteSpace(saveFolderName))
            {
                this.CurrentLogPath = string.Empty;
                return;
            }

            try
            {
                string baseDir = !string.IsNullOrWhiteSpace(Constants.DataPath)
                    ? Constants.DataPath
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StardewValley");
                string logDir = Path.Combine(baseDir, "LivingWatersLogs");
                Directory.CreateDirectory(logDir);

                foreach (char c in Path.GetInvalidFileNameChars())
                    saveFolderName = saveFolderName.Replace(c, '_');

                this.CurrentLogPath = Path.Combine(logDir, $"{saveFolderName}.log");
                this.RotateIfNeeded_Locked();
            }
            catch (Exception ex)
            {
                this.CurrentLogPath = string.Empty;
                this.Monitor.Log($"LivingWaters audit log init skipped: {ex.Message}", LogLevel.Trace);
            }
        }
    }

    public void Record(string category, string message, bool markDirty = true)
    {
        string line = FormatLine(category, message);
        lock (this.WriteLock)
        {
            if (markDirty)
                this.DirtySinceLastSave = true;

            if (!string.IsNullOrEmpty(this.CurrentLogPath))
                this.Buffer.Add(line);
        }
    }

    public void RecordImmediate(string category, string message, bool markDirty = true)
    {
        this.Record(category, message, markDirty);
        this.Flush();
    }

    public void Flush()
    {
        List<string>? toWrite = null;
        string path;
        lock (this.WriteLock)
        {
            if (this.Buffer.Count == 0 || string.IsNullOrEmpty(this.CurrentLogPath))
                return;

            toWrite = new List<string>(this.Buffer);
            this.Buffer.Clear();
            path = this.CurrentLogPath;
        }

        try
        {
            lock (this.WriteLock)
            {
                this.RotateIfNeeded_Locked();
                File.AppendAllLines(path, toWrite, Encoding.UTF8);
            }
        }
        catch
        {
            // Never impact gameplay or cause frame hitches on disk issues
        }
    }

    public bool ConsumeDirtyFlag()
    {
        lock (this.WriteLock)
        {
            bool dirty = this.DirtySinceLastSave;
            this.DirtySinceLastSave = false;
            return dirty;
        }
    }

    public string GetHeaderTag() => $"v{this.ModVersion}";

    private void RotateIfNeeded_Locked()
    {
        if (string.IsNullOrEmpty(this.CurrentLogPath) || !File.Exists(this.CurrentLogPath))
            return;

        var info = new FileInfo(this.CurrentLogPath);
        if (info.Length < MaxLogBytes)
            return;

        string prevPath = Path.ChangeExtension(this.CurrentLogPath, ".prev.log");
        if (File.Exists(prevPath))
            File.Delete(prevPath);
        File.Move(this.CurrentLogPath, prevPath);
    }

    private static string FormatLine(string category, string message)
    {
        string wall = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        string gameDate = Context.IsWorldReady
            ? $"Y{Game1.year} {Game1.currentSeason} {Game1.dayOfMonth} (D{Game1.Date.TotalDays})"
            : "NoWorld";
        return $"[{wall}][{gameDate}][{category}] {message}";
    }
}
