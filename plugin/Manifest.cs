using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace URAH_Loader
{
    /// <summary>
    /// plugins/manifest.txt of a mod, read only after manifest.sig verified against a trusted key:
    /// <code>
    /// format 1
    /// mod URAH_HRE
    /// dll URAH_HRE.dll urah.koh2.hre 0.26.0 sha256:&lt;hex&gt;
    /// file hover_shader.bundle sha256:&lt;hex&gt;
    /// </code>
    /// Written and signed by tools/sign_plugins.py. Any line the loader does not know rejects the whole
    /// manifest: a newer format needs a newer loader, never a partial load.
    /// </summary>
    internal sealed class Manifest
    {
        public const string FileName = "manifest.txt";
        public const string SigName = "manifest.sig";
        private const int Format = 1;

        public sealed class Dll
        {
            public string File;
            public string Guid;
            public string Version;
            public string Sha256;
            public Manifest Owner;
        }

        public string Dir;
        public string Mod;
        public readonly List<Dll> Dlls = new List<Dll>();
        public readonly Dictionary<string, string> Files = new Dictionary<string, string>();

        /// <summary>The verified manifest of a plugins/ folder, or null with the reason.</summary>
        public static Manifest Read(string dir, Signature keys, out string error)
        {
            byte[] text = File.ReadAllBytes(Path.Combine(dir, FileName));
            string sigPath = Path.Combine(dir, SigName);
            if (!File.Exists(sigPath))
            {
                error = "no " + SigName;
                return null;
            }
            if (!keys.Verify(text, File.ReadAllBytes(sigPath)))
            {
                error = "signature does not match any trusted key";
                return null;
            }
            return Parse(dir, Encoding.UTF8.GetString(text), out error);
        }

        private static Manifest Parse(string dir, string text, out string error)
        {
            var m = new Manifest { Dir = dir };
            string[] lines = text.Split('\n').Where(l => l.Length > 0).ToArray();
            if (lines.Length == 0 || lines[0] != "format " + Format)
            {
                error = $"unknown format '{(lines.Length > 0 ? lines[0] : "")}', needs a newer URAH_Loader";
                return null;
            }
            foreach (string line in lines.Skip(1))
            {
                string[] f = line.Split(' ');
                switch (f[0])
                {
                    case "mod" when f.Length == 2:
                        m.Mod = f[1];
                        break;
                    case "dll" when f.Length == 5 && SafeName(f[1], false) && IsHash(f[4]):
                        m.Dlls.Add(new Dll { File = f[1], Guid = f[2], Version = f[3], Sha256 = f[4], Owner = m });
                        break;
                    case "file" when f.Length == 3 && SafeName(f[1], true) && IsHash(f[2]):
                        m.Files[f[1]] = f[2];
                        break;
                    default:
                        error = $"bad line '{line}'";
                        return null;
                }
            }
            error = null;
            return m;
        }

        /// <summary>A plain name inside plugins/ (subfolders only for files), never out of it.</summary>
        private static bool SafeName(string name, bool subfolders) =>
            name.Length > 0 && !name.Contains("..") && !name.Contains('\\') && !name.Contains(':') &&
            !name.StartsWith("/", StringComparison.Ordinal) && (subfolders || !name.Contains('/'));

        private static bool IsHash(string s) => s.StartsWith("sha256:", StringComparison.Ordinal) && s.Length == 7 + 64;

        /// <summary>The bytes of a listed file if they match its hash, else null. Check and use share the bytes.</summary>
        public byte[] ReadVerified(string name, string sha256)
        {
            byte[] bytes = File.ReadAllBytes(Path.Combine(Dir, name));
            return Sha256(bytes) == sha256 ? bytes : null;
        }

        public static string Sha256(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create())
            {
                return "sha256:" + BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
            }
        }
    }
}
