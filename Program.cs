using System;
using System.Linq;
using System.Windows.Forms;

namespace DreamClubKoreanPatcher
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            if(args.Length>=2 && args[0]=="--patch-dlc")
            {
                int failures=0;
                var patcher=new DlcPatcher(AppDomain.CurrentDomain.BaseDirectory,Console.WriteLine);
                foreach(string input in args.Skip(1))
                {
                    try {patcher.RunFolder(input);}catch(Exception error){Console.Error.WriteLine(input+": "+error.Message);++failures;}
                }
                Environment.ExitCode=failures==0?0:1;return;
            }
            if ((args.Length == 5 || args.Length == 6) && String.Equals(
                    args[0], "--run-pipeline",
                    StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    PatchPipeline pipeline = new PatchPipeline(
                        System.IO.Path.GetFullPath(args[1]), null, null, null);
                    string outputPath = System.IO.Path.GetFullPath(args[4]);
                    pipeline.Run(
                        System.IO.Path.GetFullPath(args[2]),
                        System.IO.Path.GetFullPath(args[3]),
                        outputPath,
                        args.Length == 6 ? System.IO.Path.GetFullPath(args[5]) : PathForTestWork(outputPath));
                    Environment.ExitCode = 0;
                }
                catch (Exception error)
                {
                    System.IO.File.WriteAllText(
                        args[4] + ".error.txt", error.ToString());
                    Environment.ExitCode = 1;
                }
                return;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }

        private static string PathForTestWork(string outputPath)
        {
            return System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(
                    System.IO.Path.GetFullPath(outputPath)),
                "integration-work");
        }
    }
}
