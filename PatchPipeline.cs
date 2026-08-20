using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;

namespace DreamClubKoreanPatcher
{
    internal sealed class PatchPipeline
    {
        private readonly string applicationRoot;
        private readonly string assetsRoot;
        private readonly string runtimeRoot;
        private readonly Action<string> log;
        private readonly Action<int> progress;
        private readonly Action repackStarted;
        private readonly JavaScriptSerializer serializer;
        private readonly Encoding shiftJis;

        public PatchPipeline(
            string applicationRoot, Action<string> log,
            Action<int> progress, Action repackStarted)
        {
            this.applicationRoot = applicationRoot;
            assetsRoot = Path.Combine(applicationRoot, "Assets");
            runtimeRoot = Path.Combine(applicationRoot, "Runtime");
            this.log = log;
            this.progress = progress;
            this.repackStarted = repackStarted;
            serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = Int32.MaxValue;
            shiftJis = Encoding.GetEncoding(
                932, EncoderFallback.ExceptionFallback,
                DecoderFallback.ExceptionFallback);
        }

        public void Run(
            string gameRoot, string xexTool, string outputIso,
            string workRoot)
        {
            string inputRoot = MakeDirectory(workRoot, "input");
            string metadataRoot = MakeDirectory(workRoot, "metadata");
            string dialogueMetadata = MakeDirectory(metadataRoot, "dialogue");
            string songMetadata = MakeDirectory(metadataRoot, "songs");
            string baseRoot = MakeDirectory(workRoot, "base");
            string xexRoot = MakeDirectory(workRoot, "xex");
            string buildAssets = MakeDirectory(workRoot, "assets");
            string patchedRoot = MakeDirectory(workRoot, "patched");
            string stagingRoot = MakeDirectory(workRoot, "staging");

            Log("C# 패치 파이프라인을 준비합니다.");
            CopyAndNormalizeInputs(inputRoot);
            Progress(49);

            string flatDefault = Path.Combine(baseRoot, "default.exe");
            string unencryptedDefault = Path.Combine(baseRoot, "default_unencrypted.xex");
            RunProcess(xexTool, Quote("-b") + " " + Quote(flatDefault) + " " +
                Quote(Path.Combine(gameRoot, "default.xex")), workRoot, false);
            RunProcess(xexTool, Quote("-c") + " " + Quote("u") + " " +
                Quote("-e") + " " + Quote("u") + " " + Quote("-o") + " " +
                Quote(unencryptedDefault) + " " + Quote(Path.Combine(gameRoot, "default.xex")),
                workRoot, false);

            string defaultManifest = Path.Combine(workRoot, "default_manifest.json");
            RuntimeMetadataBuilder.BuildDialogueMetadata(
                gameRoot, dialogueMetadata, serializer, shiftJis);
            string supplementalNodes = Path.Combine(
                metadataRoot, "supplemental", "psw_missing_unique.nodes.jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(supplementalNodes));
            RuntimeMetadataBuilder.BuildSupplementalMetadata(
                gameRoot, dialogueMetadata, supplementalNodes, serializer, shiftJis);
            NormalizeSupplementalControls(
                supplementalNodes,
                Path.Combine(inputRoot, "psw_missing_all.jsonl"));
            RuntimeMetadataBuilder.BuildSongMetadata(
                gameRoot, songMetadata, serializer, shiftJis);
            RuntimeMetadataBuilder.BuildDefaultManifest(
                flatDefault, defaultManifest, serializer, shiftJis);
            ApplyDefaultTranslations(defaultManifest);
            RehydrateMailInputs(flatDefault, inputRoot);
            Progress(53);

            string s00Input = Path.Combine(inputRoot, "s00_amane.jsonl");
            InvokeHelper("S00DialoguePatcher.Program", new[]
            {
                gameRoot,
                Path.Combine(dialogueMetadata, "s00.nodes.jsonl"),
                s00Input,
                defaultManifest,
                Path.Combine(baseRoot, "s00.can"),
                Path.Combine(baseRoot, "glyph_map.json"),
                Path.Combine(baseRoot, "s00_patch_report.json")
            });
            InvokeHelper("DefaultExeRelocator.Program", new[]
            {
                flatDefault, defaultManifest, gameRoot, dialogueMetadata,
                s00Input, Path.Combine(baseRoot, "glyph_map.json"), xexRoot
            });

            string globalMap = Path.Combine(buildAssets, "glyph_map.json");
            List<string> extend = new List<string>();
            extend.Add("--extend-map");
            extend.Add(gameRoot);
            extend.Add(Path.Combine(xexRoot, "glyph_map.json"));
            extend.Add(globalMap);
            foreach (string file in ScenarioInputFiles(inputRoot).Skip(1)) extend.Add(file);
            extend.Add(Path.Combine(inputRoot, "songs_all.jsonl"));
            extend.Add(Path.Combine(inputRoot, "psw_missing_all.jsonl"));
            extend.AddRange(MailInputFiles(inputRoot));
            InvokeHelper("AllTranslatedContentPatcher.Program", extend.ToArray());
            Progress(59);

            string[] scenarios = { "00", "01", "02", "03", "04", "05", "06", "07", "08", "09", "99" };
            string[] scenarioInputs = ScenarioInputFiles(inputRoot).ToArray();
            for (int index = 0; index < scenarios.Length; ++index)
            {
                string id = scenarios[index];
                InvokeHelper("S00DialoguePatcher.Program", new[]
                {
                    "--explicit-map", id, gameRoot,
                    Path.Combine(dialogueMetadata, "s" + id + ".nodes.jsonl"),
                    scenarioInputs[index], globalMap,
                    Path.Combine(patchedRoot, "s" + id + ".can"),
                    Path.Combine(patchedRoot, "s" + id + "_patch_report.json")
                });
                Progress(60 + index);
            }

            InvokeHelper("AllTranslatedContentPatcher.Program", new[]
            {
                "--songs-only", gameRoot, globalMap, songMetadata,
                Path.Combine(inputRoot, "songs_all.jsonl"), patchedRoot
            });

            string supplementalInput = Path.Combine(inputRoot, "psw_missing_all.jsonl");
            foreach (string id in scenarios)
            {
                string patchedCan = Path.Combine(patchedRoot, "s" + id + ".can");
                string phaseA = Path.Combine(patchedRoot, "s" + id + ".phase_a.can");
                InvokeHelper("SafeSupplementalPswPatcher.Program", new[]
                {
                    patchedCan, id, supplementalNodes, supplementalInput,
                    globalMap, phaseA,
                    Path.Combine(patchedRoot, "s" + id + "_supplemental_report.json")
                });
                File.Copy(phaseA, patchedCan, true);
                File.Delete(phaseA);

                string phaseB = Path.Combine(patchedRoot, "s" + id + ".phase_b.can");
                InvokeHelper("SafeSupplementalPswRelocator.Program", new[]
                {
                    patchedCan, id, supplementalNodes, supplementalInput,
                    globalMap, phaseB,
                    Path.Combine(patchedRoot, "s" + id + "_phase_b_report.json")
                });
                File.Copy(phaseB, patchedCan, true);
                File.Delete(phaseB);
            }
            Progress(74);

            string patchedFlat = PatchMailChain(xexRoot, inputRoot, globalMap);
            BuildRelocatedXex(
                flatDefault, patchedFlat, unencryptedDefault,
                Path.Combine(patchedRoot, "default.xex"));

            InvokeHelper("DreamClubFontPatcher.Program", new[]
            {
                unencryptedDefault,
                Path.Combine(gameRoot, "font00.xpr"),
                Path.Combine(gameRoot, "font01.xpr"),
                Path.Combine(runtimeRoot, "Fonts", "title_Medium.ttf"),
                Path.Combine(runtimeRoot, "Fonts", "title_Bold.ttf"),
                patchedRoot, "font_reference.xex", "--font-map-only", globalMap
            });
            foreach (string fontName in new[] { "font00", "font01" })
            {
                string fontPath = Path.Combine(patchedRoot, fontName + ".xpr");
                PatchGlyphAliases(fontPath, globalMap);
            }
            InvokeHelper("DreamClubUiTexturePatcher.Program", new[]
            {
                gameRoot,
                Path.Combine(runtimeRoot, "Fonts", "title_Bold.ttf"),
                patchedRoot,
                Path.Combine(buildAssets, "ui_previews"),
                Path.Combine(assetsRoot, "ui_resources.dat")
            });
            Progress(84);

            StageGame(gameRoot, patchedRoot, stagingRoot, scenarios);
            Repack(stagingRoot, outputIso);
            Progress(99);
        }

        private string PatchMailChain(string xexRoot, string inputRoot, string globalMap)
        {
            string current = Path.Combine(xexRoot, "default_non_mail_ko.exe");
            foreach (string mail in MailInputFiles(inputRoot))
            {
                string name = Path.GetFileNameWithoutExtension(mail);
                string next = Path.Combine(xexRoot, "default_" + name + ".exe");
                InvokeHelper("AmaneMailPatcher.Program", new[]
                {
                    current, mail, globalMap, next,
                    Path.Combine(xexRoot, name + "_report.json")
                });
                current = next;
            }
            return current;
        }

        private void StageGame(
            string gameRoot, string patchedRoot, string stagingRoot,
            string[] scenarios)
        {
            HashSet<string> patched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in new[]
            {
                "default.xex", "font00.xpr", "font01.xpr", "title_tex.xpr",
                "common_tex.xpr", "gameui_tex.xpr", "menu01_tex.xpr",
                "menu02_tex.xpr", "mx000000.xpr", "m0100000.xpr",
                "m0200000.xpr", "m0300000.xpr"
            }) patched.Add(name);
            foreach (string id in scenarios) patched.Add("s" + id + ".can");
            for (int index = 0; index <= 10; ++index)
                patched.Add("song" + index.ToString("00") + ".data");

            foreach (string directory in Directory.GetDirectories(gameRoot, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(Path.Combine(
                    stagingRoot, directory.Substring(gameRoot.Length).TrimStart('\\')));
            }
            foreach (string source in Directory.GetFiles(gameRoot, "*", SearchOption.AllDirectories))
            {
                string relative = source.Substring(gameRoot.Length).TrimStart('\\');
                if (patched.Contains(relative)) continue;
                string destination = Path.Combine(stagingRoot, relative);
                File.Copy(source, destination);
                File.SetAttributes(destination, FileAttributes.Normal);
            }
            foreach (string relative in patched)
            {
                string source = Path.Combine(patchedRoot, relative);
                if (!File.Exists(source)) throw new FileNotFoundException("패치 결과가 없습니다.", source);
                File.Copy(source, Path.Combine(stagingRoot, relative), true);
            }
        }

        private void Repack(string stagingRoot, string outputIso)
        {
            if (repackStarted != null) repackStarted();
            string exiso = Path.Combine(runtimeRoot, "exiso.exe");
            RunProcess(exiso,
                Quote("-q") + " " + Quote("-c") + " " + Quote(stagingRoot) + " " +
                Quote(outputIso), runtimeRoot, true);
            string listing = RunProcess(exiso,
                Quote("-l") + " " + Quote(outputIso),
                runtimeRoot, true);
            if (listing.IndexOf("default.xex", StringComparison.OrdinalIgnoreCase) < 0)
                throw new InvalidDataException("완성된 ISO 검증에 실패했습니다.");
        }

        private void CopyAndNormalizeInputs(string inputRoot)
        {
            foreach (string source in Directory.GetFiles(assetsRoot, "*.jsonl", SearchOption.TopDirectoryOnly))
            {
                List<Dictionary<string, object>> rows = ReadJsonl(source);
                foreach (Dictionary<string, object> row in rows)
                {
                    NormalizeField(row, "translation");
                    if (row.ContainsKey("translationSubject")) NormalizeField(row, "translationSubject");
                    if (row.ContainsKey("translationBody")) NormalizeField(row, "translationBody");
                    if (row.ContainsKey("translationSubject") && row.ContainsKey("translationBody"))
                    {
                        string translation = Convert.ToString(row["translation"]);
                        int separator = translation.IndexOf('\n');
                        if (separator < 0) throw new InvalidDataException("메일 번역 형식이 잘못되었습니다.");
                        row["translationSubject"] = translation.Substring(0, separator);
                        row["translationBody"] = translation.Substring(separator + 1);
                    }
                }
                WriteJsonl(Path.Combine(inputRoot, Path.GetFileName(source)), rows);
            }
        }

        private static void NormalizeField(Dictionary<string, object> row, string field)
        {
            object value;
            if (!row.TryGetValue(field, out value) || !(value is string)) return;
            row[field] = ((string)value).Replace(", ", ",").Replace(". ", ".");
        }

        private void ApplyDefaultTranslations(string outputPath)
        {
            Dictionary<string, object> manifest = serializer.Deserialize<Dictionary<string, object>>(
                File.ReadAllText(outputPath, Encoding.UTF8));
            Dictionary<string, string> translations = ReadJsonl(
                Path.Combine(assetsRoot, "default_xex_codex_direct_ko.jsonl"))
                .ToDictionary(row => Convert.ToString(row["id"]), row => Convert.ToString(row["translation"]));
            foreach (Dictionary<string, object> entry in Objects(manifest["entries"]))
            {
                string id = Convert.ToString(entry["id"]);
                int entryNumber = ParseEntryNumber(id);
                string translation;
                if (translations.TryGetValue(id, out translation))
                {
                    entry["translation"] = translation.Replace(", ", ",").Replace(". ", ".");
                    entry["status"] = "translated";
                }
                else
                {
                    entry["translation"] = "";
                    entry["status"] = entryNumber < 1170 ? "source_preserved" : "excluded";
                }
            }
            File.WriteAllText(outputPath, serializer.Serialize(manifest), new UTF8Encoding(false));
        }

        private void NormalizeSupplementalControls(
            string nodesPath, string translationsPath)
        {
            Dictionary<string, string> sources = ReadJsonl(nodesPath)
                .ToDictionary(
                    row => Convert.ToString(row["id"]),
                    row => Convert.ToString(row["sourceText"]));
            List<Dictionary<string, object>> rows = ReadJsonl(translationsPath);
            foreach (Dictionary<string, object> row in rows)
            {
                string id = Convert.ToString(row["id"]);
                string translation = Convert.ToString(row["translation"]);
                string source;
                if (!sources.TryGetValue(id, out source))
                    throw new InvalidDataException("보조 번역 메타데이터가 없습니다: " + id);
                int sourceMarkers = source.Count(character => character == 'n');
                int translationMarkers = translation.Count(character => character == 'n');
                if (sourceMarkers != 0 && sourceMarkers == translationMarkers)
                    row["translation"] = translation.Replace(' ', '\u3000');
            }
            WriteJsonl(translationsPath, rows);
        }

        private void RehydrateMailInputs(string flatDefault, string inputRoot)
        {
            byte[] flat = File.ReadAllBytes(flatDefault);
            foreach (string file in MailInputFiles(inputRoot))
            {
                List<Dictionary<string, object>> rows = ReadJsonl(file);
                int subjectBase = MailSubjectBase(Path.GetFileName(file));
                foreach (Dictionary<string, object> row in rows)
                {
                    string id = Convert.ToString(row["id"]);
                    int separator = id.LastIndexOf('_');
                    int slot;
                    if (separator < 0 || !Int32.TryParse(id.Substring(separator + 1), out slot))
                        throw new InvalidDataException("메일 번역 ID 형식이 잘못되었습니다: " + id);
                    int subjectOffset = checked(subjectBase + slot * 0x858);
                    int bodyOffset = checked(subjectOffset + 0x20);
                    string translation = Convert.ToString(row["translation"]);
                    int translationSeparator = translation.IndexOf('\n');
                    if (translationSeparator < 0)
                        throw new InvalidDataException("메일 번역 형식이 잘못되었습니다: " + id);
                    string translationSubject = translation.Substring(0, translationSeparator);
                    string translationBody = translation.Substring(translationSeparator + 1);
                    string subject = ReadNullString(flat, subjectOffset, 0x20);
                    string body = ReadNullString(flat, bodyOffset, 0x800);
                    row["subjectOffset"] = subjectOffset;
                    row["bodyOffset"] = bodyOffset;
                    row["sourceSubject"] = subject;
                    row["sourceBody"] = body;
                    row["sourceText"] = subject + "\n" + body;
                    row["translationSubject"] = translationSubject;
                    row["translationBody"] = translationBody;
                    string[] sourceLines = NormalizeLines(body);
                    string[] translatedLines = NormalizeLines(translationBody);
                    row["lineBreakPolicy"] = "translation-source-of-truth";
                    row["sourceLineCount"] = sourceLines.Length;
                    row["translationLineCount"] = translatedLines.Length;
                    row["lineStructureMatchesSource"] = LineStructure(sourceLines) == LineStructure(translatedLines);
                }
                WriteJsonl(file, rows);
            }
        }

        private static int MailSubjectBase(string fileName)
        {
            switch (fileName.ToLowerInvariant())
            {
                case "amane_mail_ko.jsonl": return 7789232;
                case "mio_mail_ko.jsonl": return 7863992;
                case "setsu_mail_ko.jsonl": return 7940888;
                case "reika_mail_ko.jsonl": return 8011376;
                case "mian_mail_ko.jsonl": return 8071184;
                case "rui_mail_ko.jsonl": return 8139536;
                case "riho_mail_ko.jsonl": return 8212160;
                case "nao_mail_ko.jsonl": return 8284784;
                case "mari_mail_ko.jsonl": return 8357408;
                case "airi_mail_ko.jsonl": return 8421488;
                case "non_character_mail_ko.jsonl": return 8485568;
                default: throw new InvalidDataException("지원하지 않는 메일 번역 파일입니다: " + fileName);
            }
        }

        private string ReadNullString(byte[] data, int offset, int capacity)
        {
            int end = offset;
            while (end < offset + capacity && data[end] != 0) ++end;
            if (end == offset + capacity) throw new InvalidDataException("문자열 종결자를 찾지 못했습니다.");
            return shiftJis.GetString(data, offset, end - offset);
        }

        private static string[] NormalizeLines(string text)
        {
            return (text ?? String.Empty).Replace("\r\n", "\n").Split('\n');
        }

        private static string LineStructure(string[] lines)
        {
            return String.Join("|", lines.Select(line =>
                String.IsNullOrWhiteSpace(line) ? "blank" : "text").ToArray());
        }

        private void InvokeHelper(string typeName, string[] arguments)
        {
            Type type = Assembly.GetExecutingAssembly().GetType(typeName, true);
            MethodInfo method = type.GetMethod(
                "Main", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            TextWriter originalError = Console.Error;
            StringWriter capturedError = new StringWriter();
            try
            {
                Console.SetError(capturedError);
                int result = Convert.ToInt32(method.Invoke(null, new object[] { arguments }));
                if (result != 0)
                {
                    string detail = capturedError.ToString().Trim();
                    throw new InvalidOperationException(
                        typeName + " 실패: " + result +
                        (detail.Length == 0 ? String.Empty : Environment.NewLine + detail));
                }
            }
            catch (TargetInvocationException error)
            {
                throw error.InnerException ?? error;
            }
            finally
            {
                Console.SetError(originalError);
                capturedError.Dispose();
            }
        }

        private string RunProcess(
            string fileName, string arguments, string workingDirectory,
            bool suppressOutput)
        {
            ProcessStartInfo info = new ProcessStartInfo(fileName, arguments);
            info.WorkingDirectory = workingDirectory;
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
            info.StandardOutputEncoding = Encoding.UTF8;
            info.StandardErrorEncoding = Encoding.UTF8;
            StringBuilder output = new StringBuilder();
            using (Process process = new Process())
            {
                process.StartInfo = info;
                process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e)
                {
                    if (e.Data == null) return;
                    lock (output) output.AppendLine(e.Data);
                    if (!suppressOutput) Log(e.Data);
                };
                process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
                {
                    if (e.Data == null) return;
                    lock (output) output.AppendLine(e.Data);
                    if (!suppressOutput) Log(e.Data);
                };
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                process.WaitForExit();
                process.WaitForExit();
                if (process.ExitCode != 0)
                {
                    if (suppressOutput && output.Length != 0)
                        Log(output.ToString().TrimEnd());
                    throw new InvalidOperationException(Path.GetFileName(fileName) + " 실패: " + process.ExitCode);
                }
                lock (output) return output.ToString();
            }
        }

        private List<Dictionary<string, object>> ReadJsonl(string path)
        {
            List<Dictionary<string, object>> rows = new List<Dictionary<string, object>>();
            foreach (string line in File.ReadLines(path, Encoding.UTF8))
            {
                if (!String.IsNullOrWhiteSpace(line))
                    rows.Add(serializer.Deserialize<Dictionary<string, object>>(line));
            }
            return rows;
        }

        private void WriteJsonl(string path, IEnumerable<Dictionary<string, object>> rows)
        {
            File.WriteAllLines(path, rows.Select(serializer.Serialize).ToArray(), new UTF8Encoding(false));
        }

        private static IEnumerable<Dictionary<string, object>> Objects(object value)
        {
            object[] array = value as object[];
            if (array == null)
            {
                System.Collections.ArrayList list = value as System.Collections.ArrayList;
                if (list != null) array = list.ToArray();
            }
            if (array == null) throw new InvalidDataException("JSON 배열이 필요합니다.");
            return array.Cast<Dictionary<string, object>>();
        }

        private static int FindSegmentData(byte[] data, string name)
        {
            byte[] marker = Encoding.ASCII.GetBytes(name + "\0");
            for (int offset = 0; offset <= data.Length - marker.Length; ++offset)
            {
                bool match = true;
                for (int index = 0; index < marker.Length; ++index)
                    if (data[offset + index] != marker[index]) { match = false; break; }
                if (match) return (offset + marker.Length + 15) & ~15;
            }
            throw new InvalidDataException("CAN 세그먼트를 찾지 못했습니다: " + name);
        }

        private static int ParseHex(string value)
        {
            return Convert.ToInt32(value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? value.Substring(2) : value, 16);
        }

        private static int ParseEntryNumber(string id)
        {
            return Int32.Parse(id.Substring(4));
        }

        private static string MakeDirectory(string parent, string child)
        {
            string path = Path.Combine(parent, child);
            Directory.CreateDirectory(path);
            return path;
        }

        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (string file in Directory.GetFiles(source))
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
            foreach (string directory in Directory.GetDirectories(source))
                CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }

        private static IEnumerable<string> ScenarioInputFiles(string inputRoot)
        {
            string[] names =
            {
                "s00_amane.jsonl", "s01_mio.jsonl", "s02_setsu.jsonl",
                "s03_reika.jsonl", "s04_mian.jsonl", "s05_rui.jsonl",
                "s06_riho.jsonl", "s07_nao.jsonl", "s08_mari.jsonl",
                "s09_airi.jsonl", "s99_common.jsonl"
            };
            return names.Select(name => Path.Combine(inputRoot, name));
        }

        private static IEnumerable<string> MailInputFiles(string inputRoot)
        {
            string[] names =
            {
                "amane_mail_ko.jsonl", "mio_mail_ko.jsonl", "setsu_mail_ko.jsonl",
                "reika_mail_ko.jsonl", "mian_mail_ko.jsonl", "rui_mail_ko.jsonl",
                "riho_mail_ko.jsonl", "nao_mail_ko.jsonl", "mari_mail_ko.jsonl",
                "airi_mail_ko.jsonl", "non_character_mail_ko.jsonl"
            };
            return names.Select(name => Path.Combine(inputRoot, name));
        }

        private void Log(string value)
        {
            if (log != null) log(value);
        }

        private void Progress(int value)
        {
            if (progress != null) progress(value);
        }

        private static string Quote(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        private sealed class PeSection
        {
            public string Name;
            public int VirtualSize;
            public int VirtualAddress;
            public int RawSize;
            public int RawOffset;
        }

        private sealed class BasicBlock
        {
            public int DescriptorOffset;
            public int DataSize;
            public int ZeroSize;
            public int VirtualStart;
            public int VirtualDataEnd;
            public int VirtualEnd;
            public int FileStart;
        }

        private void BuildRelocatedXex(
            string originalPePath, string patchedPePath,
            string originalXexPath, string outputPath)
        {
            byte[] originalPe = File.ReadAllBytes(originalPePath);
            byte[] patchedPe = File.ReadAllBytes(patchedPePath);
            byte[] originalXex = File.ReadAllBytes(originalXexPath);
            List<PeSection> originalSections = ParsePeSections(originalPe);
            PeSection newSection = ParsePeSections(patchedPe).First(section => section.Name == ".kotext");
            int headerSize;
            List<BasicBlock> blocks = ParseBasicBlocks(originalXex, out headerSize);
            int virtualSize = blocks[blocks.Count - 1].VirtualEnd;
            if (newSection.VirtualAddress != virtualSize)
                throw new InvalidDataException(".kotext 위치가 XEX 이미지와 일치하지 않습니다.");

            int pointerCount = ReadReportInteger(
                Path.Combine(Path.GetDirectoryName(patchedPePath), "relocation_report.json"),
                "pointerPatchCount");
            int appended = pointerCount == 0 ? 0 : newSection.RawSize;
            byte[] output = new byte[originalXex.Length + appended];
            Buffer.BlockCopy(originalXex, 0, output, 0, originalXex.Length);
            foreach (PeSection section in originalSections)
            {
                int size = Math.Min(section.RawSize,
                    Math.Min(originalPe.Length - section.RawOffset, patchedPe.Length - section.RawOffset));
                for (int index = 0; index < size; ++index)
                {
                    int raw = section.RawOffset + index;
                    if (originalPe[raw] == patchedPe[raw]) continue;
                    int xex = RvaToXexOffset(raw, blocks);
                    int wordRaw = section.RawOffset + (index & ~3);
                    int wordXex = RvaToXexOffset(wordRaw, blocks);
                    bool matches = wordXex >= 0;
                    for (int part = 0; matches && part < 4; ++part)
                        matches = originalXex[wordXex + part] == originalPe[wordRaw + part];
                    if (matches) output[xex] = patchedPe[raw];
                }
            }
            if (appended != 0)
            {
                Buffer.BlockCopy(patchedPe, newSection.RawOffset, output,
                    originalXex.Length, newSection.RawSize);
                BasicBlock last = blocks[blocks.Count - 1];
                WriteBe32(output, last.DescriptorOffset, last.DataSize + newSection.RawSize);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
            File.WriteAllBytes(outputPath, output);
        }

        private static List<PeSection> ParsePeSections(byte[] data)
        {
            int pe = BitConverter.ToInt32(data, 0x3C);
            int count = BitConverter.ToUInt16(data, pe + 6);
            int optional = BitConverter.ToUInt16(data, pe + 20);
            int table = pe + 24 + optional;
            List<PeSection> result = new List<PeSection>();
            for (int index = 0; index < count; ++index)
            {
                int offset = table + index * 40;
                result.Add(new PeSection
                {
                    Name = Encoding.ASCII.GetString(data, offset, 8).TrimEnd('\0'),
                    VirtualSize = BitConverter.ToInt32(data, offset + 8),
                    VirtualAddress = BitConverter.ToInt32(data, offset + 12),
                    RawSize = BitConverter.ToInt32(data, offset + 16),
                    RawOffset = BitConverter.ToInt32(data, offset + 20)
                });
            }
            return result;
        }

        private static List<BasicBlock> ParseBasicBlocks(byte[] data, out int headerSize)
        {
            headerSize = ReadBe32(data, 8);
            int optionalCount = ReadBe32(data, 20);
            int format = -1;
            for (int index = 0; index < optionalCount; ++index)
            {
                int entry = 24 + index * 8;
                if (ReadBe32(data, entry) == 0x3FF) { format = ReadBe32(data, entry + 4); break; }
            }
            if (format < 0 || ReadBe16(data, format + 4) != 0 || ReadBe16(data, format + 6) != 1)
                throw new InvalidDataException("지원하지 않는 XEX 형식입니다.");
            int size = ReadBe32(data, format);
            List<BasicBlock> blocks = new List<BasicBlock>();
            int virtualCursor = 0;
            int fileCursor = headerSize;
            for (int offset = format + 8; offset + 8 <= format + size; offset += 8)
            {
                BasicBlock block = new BasicBlock();
                block.DescriptorOffset = offset;
                block.DataSize = ReadBe32(data, offset);
                block.ZeroSize = ReadBe32(data, offset + 4);
                block.VirtualStart = virtualCursor;
                block.FileStart = fileCursor;
                virtualCursor += block.DataSize;
                fileCursor += block.DataSize;
                block.VirtualDataEnd = virtualCursor;
                virtualCursor += block.ZeroSize;
                block.VirtualEnd = virtualCursor;
                blocks.Add(block);
            }
            return blocks;
        }

        private static int RvaToXexOffset(int rva, IEnumerable<BasicBlock> blocks)
        {
            foreach (BasicBlock block in blocks)
            {
                if (rva >= block.VirtualStart && rva < block.VirtualDataEnd)
                    return block.FileStart + rva - block.VirtualStart;
                if (rva >= block.VirtualDataEnd && rva < block.VirtualEnd)
                    throw new InvalidDataException("변경 위치가 XEX 생략 영역에 있습니다.");
            }
            return -1;
        }

        private int ReadReportInteger(string path, string name)
        {
            Dictionary<string, object> report = serializer.Deserialize<Dictionary<string, object>>(
                File.ReadAllText(path, Encoding.UTF8));
            return Convert.ToInt32(report[name]);
        }

        private void PatchGlyphAliases(string fontPath, string glyphMapPath)
        {
            byte[] xpr = File.ReadAllBytes(fontPath);
            Dictionary<string, object> map = serializer.Deserialize<Dictionary<string, object>>(
                File.ReadAllText(glyphMapPath, Encoding.UTF8));
            Dictionary<string, int> mappings = new Dictionary<string, int>();
            foreach (Dictionary<string, object> item in Objects(map["mappings"]))
                mappings[Convert.ToString(item["character"])] =
                    (Convert.ToInt32(item["lead"]) << 8) | Convert.ToInt32(item["trail"]);
            string[] characters = { "확", "인", "취", "소" };
            int[] targets = { 0x8C88, 0x92E8, 0x96DF, 0x82E9 };
            for (int index = 0; index < characters.Length; ++index)
                CopyGlyph(xpr, mappings[characters[index]], targets[index]);
            File.WriteAllBytes(fontPath, xpr);
        }

        private static readonly byte[] FontLeads =
        {
            0x81,0x82,0x83,0x84,0x87,0x88,0x89,0x8A,0x8B,0x8C,0x8D,0x8E,0x8F,
            0x90,0x91,0x92,0x93,0x94,0x95,0x96,0x97,0x98,0x99,0x9A,0x9B,0x9C,
            0x9D,0x9E,0x9F,0xE0,0xE1,0xE2,0xE3,0xE4,0xE5,0xE6,0xE7,0xE8,0xE9,0xEA
        };

        private static void CopyGlyph(byte[] xpr, int sourceCode, int targetCode)
        {
            int sourceBase = PageBase(xpr, sourceCode >> 8);
            int targetBase = PageBase(xpr, targetCode >> 8);
            int sourceCell = (sourceCode & 0xFF) - 0x40;
            int targetCell = (targetCode & 0xFF) - 0x40;
            for (int y = 0; y < 8; ++y)
            for (int x = 0; x < 8; ++x)
            {
                int source = sourceBase + ((((sourceCell >> 4) * 8 + y) * 128) +
                    ((sourceCell & 15) * 8) + x) * 16;
                int target = targetBase + ((((targetCell >> 4) * 8 + y) * 128) +
                    ((targetCell & 15) * 8) + x) * 16;
                Buffer.BlockCopy(xpr, source, xpr, target, 16);
            }
        }

        private static int PageBase(byte[] xpr, int lead)
        {
            int index = Array.IndexOf(FontLeads, (byte)lead);
            if (index < 0) throw new InvalidDataException("글꼴 페이지를 찾지 못했습니다.");
            int metadata = ReadBe32(xpr, 0x10 + index * 0x10 + 4);
            int fetch1 = ReadBe32(xpr, metadata + 0x2C);
            return 0x0C + ReadBe32(xpr, 4) + (fetch1 >> 12) * 4096;
        }

        private static int ReadBe16(byte[] data, int offset)
        {
            return (data[offset] << 8) | data[offset + 1];
        }

        private static int ReadBe32(byte[] data, int offset)
        {
            return (data[offset] << 24) | (data[offset + 1] << 16) |
                (data[offset + 2] << 8) | data[offset + 3];
        }

        private static void WriteBe32(byte[] data, int offset, int value)
        {
            data[offset] = (byte)(value >> 24);
            data[offset + 1] = (byte)(value >> 16);
            data[offset + 2] = (byte)(value >> 8);
            data[offset + 3] = (byte)value;
        }
    }
}
