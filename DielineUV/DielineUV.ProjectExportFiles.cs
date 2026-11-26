using System;
using System.IO;
using System.Text;

namespace DielineUV
{
    internal static class ProjectExportFiles
    {
        // Each export owns a new sibling directory. A failed export never becomes a template.
        public static string Write(string parent, string frameName, Action<string> writeFiles)
        {
            if (writeFiles == null) throw new ArgumentNullException(nameof(writeFiles));
            string root = new DirectoryInfo(Path.GetFullPath(parent)).FullName;
            int rootLength = Path.GetPathRoot(root).Length;
            while (root.Length > rootLength && (root.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal) ||
                root.EndsWith(Path.AltDirectorySeparatorChar.ToString(), StringComparison.Ordinal)))
                root = root.Substring(0, root.Length - 1);
            if (!Directory.Exists(root)) throw new DirectoryNotFoundException("模板保存位置不存在。");
            string label = SafeLabel(frameName);
            string token, staging, destination;
            do
            {
                token = Guid.NewGuid().ToString("N");
                staging = Path.Combine(root, ".DielineUV-exporting-" + token);
                destination = Path.Combine(root, "DielineUV_" + label + token.Substring(0, 8));
            }
            while (Directory.Exists(staging) || File.Exists(staging) ||
                Directory.Exists(destination) || File.Exists(destination));
            VerifyOwnedPath(root, staging, ".DielineUV-exporting-" + token);
            VerifyOwnedPath(root, destination, "DielineUV_" + label + token.Substring(0, 8));
            Directory.CreateDirectory(staging);
            try
            {
                writeFiles(staging);
                foreach (string required in new[] { "mapping.3dm", "template.svg", "template.png", "calibration.png", "manifest.json" })
                {
                    var file = new FileInfo(Path.Combine(staging, required));
                    if (!file.Exists || file.Length == 0)
                        throw new IOException("导出未完成，缺少文件：" + required);
                }
                Directory.Move(staging, destination);
                return destination;
            }
            catch
            {
                try
                {
                    if (Directory.Exists(staging))
                    {
                        VerifyOwnedPath(root, staging, ".DielineUV-exporting-" + token);
                        Directory.Delete(staging, true);
                    }
                }
                // Keep the original export error if the OS has a temporary file open.
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                throw;
            }
        }

        private static void VerifyOwnedPath(string root, string path, string name)
        {
            string full = Path.GetFullPath(path);
            if (!string.Equals(Path.GetDirectoryName(full), root, StringComparison.Ordinal) ||
                !string.Equals(Path.GetFileName(full), name, StringComparison.Ordinal))
                throw new IOException("模板目录超出了所选保存位置。");
        }

        private static string SafeLabel(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            const string forbidden = "<>:\"/\\|?*";
            var result = new StringBuilder();
            foreach (char c in name.Trim())
            {
                if (char.IsControl(c) || forbidden.IndexOf(c) >= 0) continue;
                result.Append(c);
                if (result.Length == 40) break;
            }
            string label = result.ToString().Trim(' ', '.');
            return label.Length == 0 ? "" : label + "_";
        }
    }
}

