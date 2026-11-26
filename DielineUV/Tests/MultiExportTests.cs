using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DielineUV;

internal static class MultiExportTests
{
    private static int assertions;
    private static void Require(bool condition, string message)
    {
        assertions++;
        if (!condition) throw new Exception(message);
    }

    private static void WriteTemplate(string folder, string frame)
    {
        // Different frame content stands in for geometry, image, and JSON files.
        foreach (string file in new[] { "mapping.3dm", "template.svg", "template.png", "calibration.png", "manifest.json" })
            File.WriteAllText(Path.Combine(folder, file), frame + ":" + file);
    }

    public static void Run()
    {
        string token = "DielineUV-multi-export-tests-" + Guid.NewGuid().ToString("N");
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), token));
        Directory.CreateDirectory(root);
        try
        {
            string unrelated = Path.Combine(root, "existing-project");
            Directory.CreateDirectory(unrelated);
            File.WriteAllText(Path.Combine(unrelated, "manifest.json"), "keep this project");
            var completed = new List<string>();
            for (int i = 0; i < 24; i++)
            {
                string frame = "frame-" + i;
                string parent = i % 2 == 0 ? root : root + Path.DirectorySeparatorChar;
                string output = ProjectExportFiles.Write(parent, "同名刀模框", folder => WriteTemplate(folder, frame));
                Require(Directory.Exists(output), "completed frame is missing");
                Require(!completed.Contains(output), "export overwrote an earlier frame");
                Require(Path.GetDirectoryName(output) == root, "output escaped its selected parent");
                Require(Path.GetFileName(output).StartsWith("DielineUV_同名刀模框_"), "frame name was lost");
                completed.Add(output);
                foreach (string file in Directory.GetFiles(output))
                    Require(File.ReadAllText(file) == frame + ":" + Path.GetFileName(file), "mixed content between frames");
                if (i % 4 != 0) continue;
                int before = Directory.GetDirectories(root).Length;
                try
                {
                    ProjectExportFiles.Write(root, "broken", folder =>
                    {
                        File.WriteAllText(Path.Combine(folder, "template.png"), "unfinished");
                        throw new ObjectDisposedException("Eto.Drawing.Font");
                    });
                    throw new Exception("failed write was reported as successful");
                }
                catch (ObjectDisposedException e)
                {
                    Require(e.ObjectName == "Eto.Drawing.Font", "export exception was hidden");
                }
                Require(Directory.GetDirectories(root).Length == before, "failed frame left a partial project");
                Require(!Directory.GetDirectories(root, ".DielineUV-exporting-*").Any(), "staging folder not cleaned");
            }
            int count = Directory.GetDirectories(root).Length;
            try
            {
                ProjectExportFiles.Write(root, null, folder => File.WriteAllText(Path.Combine(folder, "manifest.json"), "incomplete"));
                throw new Exception("incomplete template was accepted");
            }
            catch (IOException) { assertions++; }
            Require(Directory.GetDirectories(root).Length == count, "incomplete template was published");
            foreach (string label in new[] { "", "../wood:color*?", "密度板 / 木板\\透明件", ". . ", new string('x', 200) })
            {
                string output = ProjectExportFiles.Write(root, label, folder => WriteTemplate(folder, "new-frame"));
                Require(Path.GetDirectoryName(output) == root, "unsafe label escaped the parent");
                Require(Path.GetFileName(output).IndexOfAny("<>:\"/\\|?*".ToCharArray()) < 0, "invalid filename generated");
                Require(Path.GetFileName(output).Length <= 62, "frame label was not bounded");
            }
            for (int i = 0; i < completed.Count; i++)
                Require(File.ReadAllText(Path.Combine(completed[i], "manifest.json")) == "frame-" + i + ":manifest.json",
                    "later exports changed a completed frame");
            Require(File.ReadAllText(Path.Combine(unrelated, "manifest.json")) == "keep this project", "unrelated directory changed");
            Require(!Directory.GetDirectories(root, ".DielineUV-exporting-*").Any(), "staging folders remain after completion");
            Console.WriteLine("Multi-frame export regression checks passed: " + assertions);
        }
        finally
        {
            if (Path.GetFileName(root) != token || Path.GetDirectoryName(root) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar))
                throw new Exception("refusing cleanup outside this test's temporary directory");
            Directory.Delete(root, true);
        }
    }
}

