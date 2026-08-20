using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace DreamClubKoreanPatcher
{
    internal sealed class PatchRunner
    {
        public event Action<int, string> StepChanged;
        public event Action<string> LogReceived;
        public event Action<int> ProgressChanged;

        private readonly string applicationRoot;

        public PatchRunner(string applicationRoot)
        {
            this.applicationRoot = applicationRoot;
        }

        public string Run(string isoPath, string xexToolPath)
        {
            string assetsRoot = Path.Combine(applicationRoot, "Assets");
            string runtimeRoot = Path.Combine(applicationRoot, "Runtime");
            string extractXiso = Path.Combine(runtimeRoot, "exiso.exe");
            RequireFile(extractXiso, "XISO 도구");
            RequireFile(Path.Combine(runtimeRoot, "Fonts", "title_Medium.ttf"), "글꼴 에셋");
            RequireFile(Path.Combine(runtimeRoot, "Fonts", "title_Bold.ttf"), "글꼴 에셋");

            ChangeStep(0, "확인 중");
            string isoListing = RunProcess(
                extractXiso,
                Quote("-l") + " " + Quote(isoPath),
                applicationRoot,
                true);
            if (isoListing.IndexOf("default.xex", StringComparison.OrdinalIgnoreCase) < 0)
            {
                throw new InvalidDataException("선택한 ISO에서 default.xex를 찾지 못했습니다.");
            }
            ChangeStep(0, "완료");
            ReportProgress(12);

            string isoDirectory = Path.GetDirectoryName(isoPath);
            string cleanupToken = Guid.NewGuid().ToString("N");
            string workRoot = CreateWorkFolder(isoDirectory, cleanupToken);
            string gameRoot = Path.Combine(workRoot, "game");
            Directory.CreateDirectory(gameRoot);
            ChangeStep(1, "완료");
            ReportProgress(20);

            try
            {
                ChangeStep(2, "진행 중");
                RunProcess(
                    extractXiso,
                    Quote("-q") + " " + Quote("-d") + " " + Quote(gameRoot) + " " +
                    Quote("-x") + " " + Quote(isoPath),
                    applicationRoot,
                    true);
                RequireFile(Path.Combine(gameRoot, "default.xex"), "추출된 default.xex");
                ChangeStep(2, "완료");
                ReportProgress(38);

                ChangeStep(3, "확인 중");
                string xexInfo = RunProcess(
                    xexToolPath,
                    Quote("-l") + " " + Quote(Path.Combine(gameRoot, "default.xex")),
                    applicationRoot,
                    false);
                if (xexInfo.IndexOf("XexTool v6.3", StringComparison.OrdinalIgnoreCase) < 0 &&
                    xexInfo.IndexOf("XEX", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    throw new InvalidDataException("xextool.exe로 default.xex를 확인하지 못했습니다.");
                }
                ChangeStep(3, "완료");
                ReportProgress(45);

                ChangeStep(4, "진행 중");
                string outputPath = BuildOutputPath(isoPath);
                PatchPipeline pipeline = new PatchPipeline(
                    applicationRoot,
                    Log,
                    ReportProgress,
                    delegate
                    {
                        ChangeStep(5, "진행 중");
                        ReportProgress(88);
                    });
                pipeline.Run(
                    gameRoot, xexToolPath, outputPath,
                    Path.Combine(workRoot, "patch"));
                ChangeStep(4, "완료");
                ChangeStep(5, "완료");
                ReportProgress(100);
                CleanupWorkFolder(workRoot, isoDirectory, cleanupToken);
                return outputPath;
            }
            catch
            {
                Log("작업 폴더가 보존되었습니다: " + workRoot);
                throw;
            }
        }

        private string RunProcess(
            string fileName, string arguments, string workingDirectory,
            bool suppressOutput)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = fileName;
            startInfo.Arguments = arguments;
            startInfo.WorkingDirectory = workingDirectory;
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.StandardOutputEncoding = Encoding.UTF8;
            startInfo.StandardErrorEncoding = Encoding.UTF8;

            StringBuilder output = new StringBuilder();
            using (Process process = new Process())
            {
                process.StartInfo = startInfo;
                process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e)
                {
                    if (e.Data == null) return;
                    output.AppendLine(e.Data);
                    if (!suppressOutput) Log(e.Data);
                };
                process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
                {
                    if (e.Data == null) return;
                    output.AppendLine(e.Data);
                    if (!suppressOutput) Log(e.Data);
                };
                Log("실행: " + Path.GetFileName(fileName));
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                process.WaitForExit();
                if (process.ExitCode != 0)
                {
                    if (suppressOutput && output.Length != 0)
                        Log(output.ToString().TrimEnd());
                    throw new InvalidOperationException(
                        Path.GetFileName(fileName) + " 실행 실패 (종료 코드 " + process.ExitCode + ")");
                }
            }
            return output.ToString();
        }

        private static string BuildOutputPath(string isoPath)
        {
            string directory = Path.GetDirectoryName(isoPath);
            string name = Path.GetFileNameWithoutExtension(isoPath);
            string candidate = Path.Combine(directory, name + "_repacked.iso");
            if (!File.Exists(candidate)) return candidate;
            return Path.Combine(directory, name + "_repacked_" +
                DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".iso");
        }

        private static string Quote(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        private static string CreateWorkFolder(
            string parentDirectory, string cleanupToken)
        {
            for (int index = 1; index <= 9999; ++index)
            {
                string name = index == 1 ? "temp" : "temp" + index;
                string candidate = Path.Combine(parentDirectory, name);
                if (Directory.Exists(candidate) || File.Exists(candidate))
                    continue;
                Directory.CreateDirectory(candidate);
                try
                {
                    File.WriteAllText(
                        Path.Combine(candidate, ".dckp-temp"),
                        cleanupToken,
                        new UTF8Encoding(false));
                    return candidate;
                }
                catch
                {
                    try { Directory.Delete(candidate, false); }
                    catch { }
                    throw;
                }
            }
            throw new IOException("사용할 수 있는 임시 폴더 이름을 찾지 못했습니다.");
        }

        private void CleanupWorkFolder(
            string workRoot, string intendedParent, string cleanupToken)
        {
            string fullPath = Path.GetFullPath(workRoot);
            string parent = Path.GetFullPath(Path.GetDirectoryName(fullPath));
            string expectedParent = Path.GetFullPath(intendedParent);
            string leaf = Path.GetFileName(fullPath);
            if (!String.Equals(parent, expectedParent, StringComparison.OrdinalIgnoreCase) ||
                !IsWorkFolderName(leaf))
            {
                Log("안전 확인을 통과하지 못해 작업 폴더를 보존합니다: " + fullPath);
                return;
            }
            try
            {
                string marker = Path.Combine(fullPath, ".dckp-temp");
                if (!File.Exists(marker) || !String.Equals(
                    File.ReadAllText(marker, Encoding.UTF8), cleanupToken,
                    StringComparison.Ordinal))
                    throw new IOException("패처가 만든 임시 폴더인지 확인할 수 없습니다.");
                foreach (string directory in Directory.GetDirectories(
                    fullPath, "*", SearchOption.AllDirectories))
                {
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("임시 작업 폴더에서 재분석 지점을 발견했습니다.");
                }
                foreach (string file in Directory.GetFiles(
                    fullPath, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(fullPath, true);
                Log("임시 작업 폴더를 정리했습니다.");
            }
            catch (Exception error)
            {
                Log("임시 작업 폴더를 정리하지 못했습니다: " + error.Message);
                Log("작업 폴더가 보존되었습니다: " + fullPath);
            }
        }

        private static bool IsWorkFolderName(string name)
        {
            if (String.Equals(name, "temp", StringComparison.Ordinal))
                return true;
            if (!name.StartsWith("temp", StringComparison.Ordinal))
                return false;
            string suffix = name.Substring(4);
            int number;
            return Int32.TryParse(suffix, out number) && number >= 2 &&
                String.Equals(suffix, number.ToString(), StringComparison.Ordinal);
        }

        private static void RequireFile(string path, string description)
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException(description + " 파일이 없습니다.", path);
            }
        }

        private void ChangeStep(int index, string state)
        {
            Action<int, string> handler = StepChanged;
            if (handler != null) handler(index, state);
        }

        private void Log(string message)
        {
            Action<string> handler = LogReceived;
            if (handler != null) handler(message);
        }

        private void ReportProgress(int value)
        {
            Action<int> handler = ProgressChanged;
            if (handler != null) handler(Math.Max(0, Math.Min(100, value)));
        }
    }
}
