using System.Diagnostics;
using System.Globalization;
using System.Text;

class Program
{
    const string FfmpegPath = "/opt/homebrew/opt/ffmpeg-full/bin/ffmpeg";
    const string FfprobePath = "/opt/homebrew/opt/ffmpeg-full/bin/ffprobe";

    const string FontName = "Geeza Pro";
    const int FontSize = 18;

    static readonly (string Name, int Height, int Crf)[] Qualities =
    {
        ("1080p", 1080, 20),
        ("720p", 720, 21),
        ("480p", 480, 22),
    };

    static async Task<int> Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("Usage: HardSubEncoder <video-path> <subtitle-path> [output-dir]");
            return 1;
        }

        string video = Path.GetFullPath(args[0]);
        string sub = Path.GetFullPath(args[1]);
        string outDir = args.Length > 2 ? Path.GetFullPath(args[2]) : Path.GetDirectoryName(video)!;

        if (!File.Exists(video))
        {
            Console.WriteLine("Video not found.");
            return 1;
        }

        if (!File.Exists(sub))
        {
            Console.WriteLine("Subtitle not found.");
            return 1;
        }

        Directory.CreateDirectory(outDir);

        double totalSeconds = await GetDurationAsync(video);
        if (totalSeconds <= 0)
        {
            Console.WriteLine("Could not read video duration.");
            return 1;
        }

        string tempDir = Path.Combine(Path.GetTempPath(), "hardsub_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string tempSub = "sub" + Path.GetExtension(sub).ToLowerInvariant();
        File.Copy(sub, Path.Combine(tempDir, tempSub));

        try
        {
            string baseName = Path.GetFileNameWithoutExtension(video);

            string logPath = Path.Combine(outDir, $"{baseName}_log.txt");
            using var log = new StreamWriter(logPath, append: false, Encoding.UTF8) { AutoFlush = true };

            foreach (var q in Qualities)
            {
                string output = Path.Combine(outDir, $"{baseName}_{q.Name}_hardsub.mp4");

                string vf = $"scale=-2:{q.Height}," +
                            $"subtitles={tempSub}:charenc=UTF-8:" +
                            $"force_style='FontName={FontName},FontSize={FontSize},Outline=1,Shadow=0'";

                var psi = new ProcessStartInfo(FfmpegPath)
                {
                    WorkingDirectory = tempDir,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                foreach (var a in new[]
                         {
                             "-y", "-nostdin", "-loglevel", "info",
                             "-progress", "pipe:1", "-nostats",
                             "-i", video,
                             "-vf", vf,
                             "-c:v", "libx264", "-preset", "medium", "-crf", q.Crf.ToString(),
                             "-pix_fmt", "yuv420p",
                             "-c:a", "copy",
                             "-movflags", "+faststart",
                             output
                         })
                    psi.ArgumentList.Add(a);
                lock (log)
                {
                    log.WriteLine($"===== {q.Name} =====");
                    log.WriteLine(FfmpegPath + " " + string.Join(" ", psi.ArgumentList.Select(a => $"\"{a}\"")));
                    log.WriteLine();
                }

                var tail = new Queue<string>();
                DrawBar(q.Name, 0, done: false);
                using var proc = Process.Start(psi)!;

                proc.OutputDataReceived += (_, e) =>
                {
                    if (e.Data == null) return;
                    if (e.Data.StartsWith("out_time_us=") &&
                        long.TryParse(e.Data.AsSpan("out_time_us=".Length), out long us))
                    {
                        double percent = Math.Clamp(us / 1_000_000.0 / totalSeconds * 100, 0, 100);
                        DrawBar(q.Name, percent, done: false);
                    }
                };
                proc.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data == null) return;
                    lock (log) log.WriteLine(e.Data);
                    lock (tail)
                    {
                        tail.Enqueue(e.Data);
                        if (tail.Count > 15) tail.Dequeue();
                    }
                };

                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                await proc.WaitForExitAsync();

                if (proc.ExitCode != 0)
                {
                    Console.WriteLine();
                    Console.WriteLine($"Encoding {q.Name} FAILED (exit code {proc.ExitCode}).");
                    lock (tail) Console.WriteLine(string.Join(Environment.NewLine, tail));
                    Console.WriteLine($"Log saved to: {logPath}");
                    return proc.ExitCode;
                }

                DrawBar(q.Name, 100, done: true);
                Console.WriteLine();
            }
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, true);
            }
            catch
            {
            }
        }

        return 0;
    }

    static void DrawBar(string name, double percent, bool done)
    {
        const int width = 30;
        int filled = (int)(percent / 100 * width);
        string bar = new string('█', filled) + new string('░', width - filled);
        string line = $"Encoding {name} : [{bar}] {percent,5:0.0}%";
        if (done) line += " Encoded Successfully.";
        Console.Write("\r" + line);
    }

    static async Task<double> GetDurationAsync(string file)
    {
        var psi = new ProcessStartInfo(FfprobePath)
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in new[]
                 {
                     "-v", "error",
                     "-show_entries", "format=duration",
                     "-of", "default=noprint_wrappers=1:nokey=1",
                     file
                 })
            psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)!;
        string text = (await p.StandardOutput.ReadToEndAsync()).Trim();
        await p.WaitForExitAsync();

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : 0;
    }
}