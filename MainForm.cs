using System;
using System.Collections.Generic;
using System.Linq;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace DreamClubKoreanPatcher
{
    internal sealed class MainForm : Form
    {
        private readonly CheckBox karaokeAlwaysApproveCheckBox;
        private readonly CheckBox karaokeNoScoreLossCheckBox;
        private readonly Panel dropPanel;
        private readonly Label isoStatusLabel;
        private readonly Label xexStatusLabel;
        private readonly Label tuStatusLabel;
        private readonly Button startButton;
        private readonly ProgressBar progressBar;
        private readonly TextBox logBox;
        private readonly BackgroundWorker worker;

        private readonly List<string> dlcPaths = new List<string>();
        private readonly ListBox dlcList = new ListBox();
        private Button clearSelectionButton;
        private Button selectDlcFolderButton;
        private string isoPath;
        private string xexToolPath;
        private string titleUpdatePath;

        public MainForm()
        {
            Font = new Font("맑은 고딕", 9F, FontStyle.Regular, GraphicsUnit.Point);
            var buildVersion = (System.Reflection.AssemblyInformationalVersionAttribute)
                Attribute.GetCustomAttribute(typeof(MainForm).Assembly,
                    typeof(System.Reflection.AssemblyInformationalVersionAttribute));
            Text = "DreamClubKoreanPatcher " + buildVersion.InformationalVersion;
            BackColor = Color.FromArgb(248, 249, 252);
            ClientSize = new Size(1040, 690);
            MinimumSize = new Size(940, 710);
            StartPosition = FormStartPosition.CenterScreen;
            AllowDrop = true;

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.Padding = new Padding(18, 16, 18, 16);
            root.ColumnCount = 2;
            root.RowCount = 3;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40F));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60F));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 68F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 24F));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 32F));
            Controls.Add(root);

            dropPanel = BuildDropPanel(out isoStatusLabel, out xexStatusLabel, out tuStatusLabel);
            root.Controls.Add(dropPanel, 0, 0);

            Panel progressPanel = new Panel();
            progressPanel.Dock = DockStyle.Fill;
            progressPanel.Padding = new Padding(28, 5, 4, 0);
            root.Controls.Add(progressPanel, 1, 0);

            Label heading = new Label();
            heading.AutoSize = true;
            heading.Font = new Font(Font, FontStyle.Regular);
            heading.Text = "치트 옵션";
            heading.Location = new Point(26, 9);
            progressPanel.Controls.Add(heading);

            karaokeAlwaysApproveCheckBox = new CheckBox();
            karaokeAlwaysApproveCheckBox.Text = "가라오케 항상 승인";
            karaokeAlwaysApproveCheckBox.AutoSize = true;
            karaokeAlwaysApproveCheckBox.Location = new Point(26, 52);
            karaokeAlwaysApproveCheckBox.Checked = false;
            progressPanel.Controls.Add(karaokeAlwaysApproveCheckBox);
            karaokeNoScoreLossCheckBox = new CheckBox();
            karaokeNoScoreLossCheckBox.Text = "가라오케 일치율 감소 방지";
            karaokeNoScoreLossCheckBox.AutoSize = true;
            karaokeNoScoreLossCheckBox.Location = new Point(26, 86);
            karaokeNoScoreLossCheckBox.Checked = false;
            progressPanel.Controls.Add(karaokeNoScoreLossCheckBox);

            Label dlcHeading = new Label();
            dlcHeading.Text = "DLC 폴더 목록 (선택 후 Delete로 제거)";
            dlcHeading.SetBounds(26, 132, 340, 25);
            progressPanel.Controls.Add(dlcHeading);
            dlcList.SetBounds(26, 162, 450, 115);
            dlcList.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            dlcList.HorizontalScrollbar = true;
            dlcList.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Delete && !worker.IsBusy && dlcList.SelectedIndex >= 0)
                { dlcPaths.RemoveAt(dlcList.SelectedIndex); RefreshFileState(); }
            };
            progressPanel.Controls.Add(dlcList);
            Label note = new Label();
            note.Text = "DLC 한글 표시에는 이 버전으로 패치한 본편 ISO가 필요합니다.";
            note.SetBounds(26, 285, 460, 42);
            note.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            progressPanel.Controls.Add(note);
            clearSelectionButton = new Button();
            clearSelectionButton.Text = "선택 비우기";
            clearSelectionButton.SetBounds(26, 340, 118, 38);
            clearSelectionButton.Click += delegate
            {
                if(worker.IsBusy)return;
                isoPath=null; titleUpdatePath=null; dlcPaths.Clear(); RefreshFileState();
            };
            progressPanel.Controls.Add(clearSelectionButton);
            selectDlcFolderButton = new Button();
            selectDlcFolderButton.Text = "DLC 폴더 선택";
            selectDlcFolderButton.SetBounds(154, 340, 118, 38);
            selectDlcFolderButton.Click += delegate
            {
                if (worker.IsBusy) return;
                using (var dialog = new FolderBrowserDialog())
                {
                    dialog.Description = "원본 DLC 파일이 들어 있는 폴더를 선택해 주세요.";
                    dialog.ShowNewFolderButton = false;
                    if (dialog.ShowDialog(this) == DialogResult.OK)
                        AcceptFiles(new[] { dialog.SelectedPath }, true);
                }
            };
            progressPanel.Controls.Add(selectDlcFolderButton);

            startButton = new Button();
            startButton.Text = "패치 시작";
            startButton.Size = new Size(138, 56);
            startButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            startButton.Location = new Point(progressPanel.ClientSize.Width - 154, 340);
            startButton.FlatStyle = FlatStyle.System;
            startButton.Enabled = false;
            startButton.Click += StartButtonClick;
            progressPanel.Controls.Add(startButton);
            startButton.BringToFront();
            progressPanel.Resize += delegate
            {
                startButton.Location = new Point(
                    Math.Max(26, progressPanel.ClientSize.Width - 154), 340);
            };

            progressBar = new ProgressBar();
            progressBar.Dock = DockStyle.Fill;
            progressBar.Style = ProgressBarStyle.Continuous;
            root.SetColumnSpan(progressBar, 2);
            root.Controls.Add(progressBar, 0, 1);

            logBox = new TextBox();
            logBox.Dock = DockStyle.Fill;
            logBox.Multiline = true;
            logBox.ReadOnly = true;
            logBox.ScrollBars = ScrollBars.Vertical;
            logBox.BackColor = Color.White;
            logBox.BorderStyle = BorderStyle.FixedSingle;
            logBox.Text = "대기 중" + Environment.NewLine;
            root.SetColumnSpan(logBox, 2);
            root.Controls.Add(logBox, 0, 2);

            DragEnter += FilesDragEnter;
            DragDrop += FilesDragDrop;
            dropPanel.DragEnter += FilesDragEnter;
            dropPanel.DragDrop += FilesDragDrop;
            AttachDropEvents(dropPanel);

            worker = new BackgroundWorker();
            worker.DoWork += WorkerDoWork;
            worker.RunWorkerCompleted += WorkerCompleted;
        }

        private Panel BuildDropPanel(out Label isoLabel, out Label xexLabel, out Label tuLabel)
        {
            Panel panel = new Panel();
            panel.Dock = DockStyle.Fill;
            panel.Margin = new Padding(0, 0, 0, 10);
            panel.BackColor = Color.White;
            panel.BorderStyle = BorderStyle.FixedSingle;
            panel.AllowDrop = true;
            panel.Cursor = Cursors.Hand;
            panel.Click += SelectFilesClick;

            Label instruction = new Label();
            instruction.AutoSize = false;
            instruction.TextAlign = ContentAlignment.MiddleCenter;
            instruction.Font = new Font(Font, FontStyle.Regular);
            instruction.ForeColor = Color.FromArgb(29, 61, 122);
            instruction.Text = "ISO 파일 / DLC 폴더를 여기에 드래그 드롭" + Environment.NewLine +
                "클릭: ISO / TU / xextool 파일 선택";
            instruction.Dock = DockStyle.Top;
            instruction.Height = 112;
            instruction.Padding = new Padding(0, 48, 0, 0);
            instruction.Click += SelectFilesClick;
            panel.Controls.Add(instruction);

            isoLabel = new Label();
            isoLabel.AutoEllipsis = true;
            isoLabel.ForeColor = Color.FromArgb(221, 57, 47);
            isoLabel.Text = "(ISO 패치 시) 정품 게임 ISO";
            isoLabel.SetBounds(34, 156, 300, 28);
            isoLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            isoLabel.Click += SelectFilesClick;
            panel.Controls.Add(isoLabel);

            xexLabel = new Label();
            xexLabel.AutoEllipsis = true;
            xexLabel.ForeColor = Color.FromArgb(221, 57, 47);
            xexLabel.Text = "(ISO 패치 시) xextool.exe 6.3";
            xexLabel.SetBounds(34, 198, 300, 28);
            xexLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            xexLabel.Click += SelectFilesClick;
            panel.Controls.Add(xexLabel);
            tuLabel = new Label();
            tuLabel.AutoEllipsis = true;
            tuLabel.SetBounds(34, 240, 300, 28);
            tuLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            tuLabel.ForeColor = Color.DimGray;
            tuLabel.Text = "(옵션) TU 파일을 끌어 놓으면 적용";
            panel.Controls.Add(tuLabel);
            return panel;
        }

        private void AttachDropEvents(Control parent)
        {
            foreach (Control control in parent.Controls)
            {
                control.AllowDrop = true;
                control.DragEnter += FilesDragEnter;
                control.DragDrop += FilesDragDrop;
                if (control.HasChildren) AttachDropEvents(control);
            }
        }

        private void SelectFilesClick(object sender, EventArgs e)
        {
            if (worker.IsBusy) return;
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Title = "게임 ISO / DLC / TU / xextool.exe 선택";
                dialog.Filter = "모든 패치 입력 파일 (*.*)|*.*";
                dialog.Multiselect = true;
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    AcceptFiles(dialog.FileNames, true);
                }
            }
        }

        private void FilesDragEnter(object sender, DragEventArgs e)
        {
            e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop)
                ? DragDropEffects.Copy : DragDropEffects.None;
        }

        private void FilesDragDrop(object sender, DragEventArgs e)
        {
            if (worker.IsBusy) return;
            string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files != null) AcceptFiles(files, true);
        }

        private void AcceptFiles(string[] paths, bool acceptTitleUpdate)
        {
            if (acceptTitleUpdate)
            {
                int count = 0;
                foreach (string candidate in paths)
                    if (File.Exists(candidate) && TitleUpdatePackage.IsPackage(candidate) && !DlcPatcher.IsDlc(candidate)) ++count;
                if (count > 1)
                {
                    MessageBox.Show(this, "TU 파일은 한 번에 하나만 넣어 주십시오.", "TU 선택");
                    return;
                }
            }
            foreach (string path in paths)
            {
                if (Directory.Exists(path))
                {
                    string full = new DirectoryInfo(path).FullName.TrimEnd(Path.DirectorySeparatorChar);
                    if (!dlcPaths.Contains(full, StringComparer.OrdinalIgnoreCase)) dlcPaths.Add(full);
                    continue;
                }
                if (!File.Exists(path)) continue;
                string extension = Path.GetExtension(path);
                if (String.Equals(extension, ".iso", StringComparison.OrdinalIgnoreCase))
                {
                    isoPath = Path.GetFullPath(path);
                }
                else if (String.Equals(Path.GetFileName(path), "xextool.exe", StringComparison.OrdinalIgnoreCase))
                {
                    xexToolPath = Path.GetFullPath(path);
                }
                else if (DlcPatcher.IsDlc(path))
                {
                    MessageBox.Show(this, "개별 DLC 파일 대신 DLC가 들어 있는 폴더를 선택해 주세요.", "DLC 폴더 선택");
                }
                else if (acceptTitleUpdate)
                {
                    try
                    {
                        TitleUpdatePackage.Read(path);
                        titleUpdatePath = Path.GetFullPath(path);
                    }
                    catch (Exception error)
                    {
                        MessageBox.Show(this, error.Message, "TU 파일 확인", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }
            }
            RefreshFileState();
        }

        private void RefreshFileState()
        {
            bool hasIso = !String.IsNullOrEmpty(isoPath) && File.Exists(isoPath);
            bool hasXex = !String.IsNullOrEmpty(xexToolPath) && File.Exists(xexToolPath);
            isoStatusLabel.ForeColor = hasIso ? Color.FromArgb(31, 139, 76) : Color.FromArgb(221, 57, 47);
            xexStatusLabel.ForeColor = hasXex ? Color.FromArgb(31, 139, 76) : Color.FromArgb(221, 57, 47);
            isoStatusLabel.Text = hasIso ? "✓ ISO: " + Path.GetFileName(isoPath) : "(ISO 패치 시) 정품 게임 ISO";
            xexStatusLabel.Text = hasXex ? "✓ XEX: " + Path.GetFileName(xexToolPath) : "(ISO 패치 시) xextool.exe 6.3";
            bool hasTu = !String.IsNullOrEmpty(titleUpdatePath);
            tuStatusLabel.Text = hasTu ? "✓ TU: " + Path.GetFileName(titleUpdatePath) : "(옵션) TU 파일을 끌어 놓으면 적용";
            tuStatusLabel.ForeColor = hasTu ? Color.FromArgb(31, 139, 76) : Color.DimGray;
            dlcList.Items.Clear();
            foreach(string dlc in dlcPaths) dlcList.Items.Add(dlc);
            startButton.Enabled = !worker.IsBusy && (hasIso ? hasXex : dlcPaths.Count > 0 && !hasTu);
            karaokeAlwaysApproveCheckBox.Enabled = !worker.IsBusy && hasIso;
            karaokeNoScoreLossCheckBox.Enabled = !worker.IsBusy && hasIso;
            clearSelectionButton.Enabled = !worker.IsBusy;
            selectDlcFolderButton.Enabled = !worker.IsBusy;
            dlcList.Enabled = !worker.IsBusy;
        }

        private void StartButtonClick(object sender, EventArgs e)
        {
            startButton.Enabled = false;
            dropPanel.Enabled = false;
            clearSelectionButton.Enabled = false;
            selectDlcFolderButton.Enabled = false;
            dlcList.Enabled = false;
            progressBar.Value = 0;
            logBox.Clear();
            karaokeAlwaysApproveCheckBox.Enabled = false;
            karaokeNoScoreLossCheckBox.Enabled = false;
            worker.RunWorkerAsync(new object[] { isoPath, xexToolPath, titleUpdatePath, karaokeAlwaysApproveCheckBox.Checked, karaokeNoScoreLossCheckBox.Checked, dlcPaths.ToArray() });
        }

        private void WorkerDoWork(object sender, DoWorkEventArgs e)
        {
            object[] arguments = (object[])e.Argument;
            PatchRunner runner = new PatchRunner(AppDomain.CurrentDomain.BaseDirectory);
            runner.LogReceived += delegate(string line)
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    logBox.AppendText(line + Environment.NewLine);
                });
            };
            string[] dlcs = (string[])arguments[5];
            bool hasIso = !String.IsNullOrEmpty((string)arguments[0]);
            int totalJobs = dlcs.Length + (hasIso ? 1 : 0);
            runner.ProgressChanged += delegate(int value)
            {
                BeginInvoke((MethodInvoker)delegate { progressBar.Value = Math.Min(99, value / totalJobs); });
            };
            var results = new List<string>();
            if (!String.IsNullOrEmpty((string)arguments[0]))
            {
                try { results.Add("ISO 완료: " + runner.RunWithTitleUpdate((string)arguments[0], (string)arguments[1], (string)arguments[2], (bool)arguments[3], (bool)arguments[4])); }
                catch(Exception error) { results.Add("ISO 실패: " + error.Message); }
            }
            if(dlcs.Length>0)
            {
                var patcher = new DlcPatcher(AppDomain.CurrentDomain.BaseDirectory, delegate(string line)
                { BeginInvoke((MethodInvoker)delegate { logBox.AppendText(line + Environment.NewLine); }); });
                for(int i=0;i<dlcs.Length;++i)
                {
                    string result;
                    try { result="DLC 완료: "+patcher.RunFolder(dlcs[i], delegate(int value)
                    {
                        int overall = Math.Min(99, ((hasIso ? 100 : 0) + i * 100 + value) / totalJobs);
                        BeginInvoke((MethodInvoker)delegate { progressBar.Value = overall; });
                    }); }
                    catch(Exception error) { result="DLC 실패: "+Path.GetFileName(dlcs[i])+" - "+error.Message; }
                    results.Add(result);
                    int percentage = Math.Min(99, ((hasIso ? 100 : 0) + (i+1)*100) / totalJobs);
                    string message = result;
                    BeginInvoke((MethodInvoker)delegate {progressBar.Value=percentage;logBox.AppendText(message+Environment.NewLine);});
                }
            }
            e.Result = String.Join(Environment.NewLine, results);
        }

        private void WorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            dropPanel.Enabled = true;
            karaokeAlwaysApproveCheckBox.Enabled = true;
            karaokeNoScoreLossCheckBox.Enabled = true;
            RefreshFileState();
            if (e.Error != null)
            {
                logBox.AppendText("실패: " + e.Error.Message + Environment.NewLine);
                MessageBox.Show(this, e.Error.Message, "패치 실패",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            string outputPath = Convert.ToString(e.Result);
            progressBar.Value = 100;
            logBox.AppendText(outputPath + Environment.NewLine);
            MessageBox.Show(this, outputPath,
                "패치 결과", MessageBoxButtons.OK, outputPath.Contains("실패:") ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        }
    }
}
