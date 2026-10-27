using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Windows.Forms;

namespace GTWM.ModelConverter
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            // Libraries ship inside the exe; hook this up before anything touches them.
            AppDomain.CurrentDomain.AssemblyResolve += ResolveEmbedded;
            return Run(args);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int Run(string[] args)
        {
            LoadNativeAssimp();
            if (args.Length > 0 && args[0] == "--selftest")
            {
                return SelfTest.Run(args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "gtwm-convert-test"));
            }
            if (args.Length > 0 && args[0] == "--convert")
            {
                // --convert <model> <template.glb> <out.glb> [--turn]
                var options = new ConvertOptions { TemplatePath = args[2], Turn180 = args.Contains("--turn") };
                ConvertResult r = Converter.Convert(args[1], options);
                File.WriteAllBytes(args[3], r.Glb);
                Console.WriteLine(string.Join(Environment.NewLine, r.Log));
                return 0;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            HighDpi.Enable();
            Application.Run(new MainForm(args.FirstOrDefault(File.Exists)));
            return 0;
        }

        private static Assembly ResolveEmbedded(object sender, ResolveEventArgs e)
        {
            string name = new AssemblyName(e.Name).Name;
            using (Stream s = typeof(Program).Assembly.GetManifestResourceStream("lib." + name + ".dll"))
            {
                if (s == null)
                {
                    return null;
                }
                var bytes = new byte[s.Length];
                s.Read(bytes, 0, bytes.Length);
                return Assembly.Load(bytes);
            }
        }

        /// <summary>Extracts assimp.dll once to %LOCALAPPDATA% (keyed by its hash) and loads it from there.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void LoadNativeAssimp()
        {
            byte[] bytes;
            using (Stream s = typeof(Program).Assembly.GetManifestResourceStream("native.assimp.dll"))
            {
                bytes = new byte[s.Length];
                s.Read(bytes, 0, bytes.Length);
            }
            string hash;
            using (SHA256 sha = SHA256.Create())
            {
                hash = string.Concat(sha.ComputeHash(bytes).Take(6).Select(b => b.ToString("x2")));
            }
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GTWM", "ModelConverter", hash);
            string path = Path.Combine(dir, "assimp.dll");
            if (!File.Exists(path) || new FileInfo(path).Length != bytes.Length)
            {
                Directory.CreateDirectory(dir);
                File.WriteAllBytes(path, bytes);
            }
            Assimp.Unmanaged.AssimpLibrary.Instance.LoadLibrary(path);
        }
    }

    internal static class HighDpi
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        public static void Enable()
        {
            try
            {
                SetProcessDPIAware();
            }
            catch
            {
            }
        }
    }
}
