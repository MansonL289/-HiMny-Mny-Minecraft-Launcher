using System.IO.Compression;

namespace WinUI3Shell.Services.Mc;

/// <summary>存档备份 / 导出 / 导入。zip 内保持「存档文件夹/文件」的单层结构。</summary>
public static class SaveBackup
{
    /// <summary>本地备份目录里的新备份路径。</summary>
    public static string NewBackupPath(string saveFolder)
    {
        Directory.CreateDirectory(McPaths.BackupsDir);
        string baseName = $"{saveFolder}-{DateTime.Now:yyyyMMdd-HHmmss}";
        string path = Path.Combine(McPaths.BackupsDir, baseName + ".zip");
        for (int i = 2; File.Exists(path); i++)
        {
            path = Path.Combine(McPaths.BackupsDir, $"{baseName}-{i}.zip");
        }
        return path;
    }

    /// <summary>备份到 <see cref="McPaths.BackupsDir"/>，返回 zip 路径。已存在同名文件时自动加序号。</summary>
    public static Task<string> BackupAsync(string savePath)
    {
        string folder = Path.GetFileName(savePath);
        string dest = NewBackupPath(folder);
        return Task.Run(() =>
        {
            ZipFile.CreateFromDirectory(savePath, dest, CompressionLevel.Optimal, includeBaseDirectory: true);
            return dest;
        });
    }

    /// <summary>导出到用户指定位置（覆盖）。</summary>
    public static Task ExportAsync(string savePath, string destZip)
    {
        return Task.Run(() =>
        {
            if (File.Exists(destZip)) File.Delete(destZip);
            ZipFile.CreateFromDirectory(savePath, destZip, CompressionLevel.Optimal, includeBaseDirectory: true);
        });
    }

    /// <summary>导入 zip 到存档目录，自动避开重名。返回新存档路径。</summary>
    public static Task<string> ImportAsync(string zipPath, string savesDir)
    {
        return Task.Run(() =>
        {
            string temp = Path.Combine(Path.GetTempPath(), "mnymc-save-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(temp);
                ZipFile.ExtractToDirectory(zipPath, temp, overwriteFiles: true);

                // 找到包含 level.dat 的那一层（zip 顶层文件夹，或直接就是根）
                string? root = null;
                if (File.Exists(Path.Combine(temp, "level.dat")))
                {
                    root = temp;
                }
                else
                {
                    foreach (var dir in Directory.GetDirectories(temp))
                    {
                        if (File.Exists(Path.Combine(dir, "level.dat")))
                        {
                            root = dir;
                            break;
                        }
                    }
                }
                if (root == null)
                {
                    throw new InvalidDataException("压缩包里没有找到 level.dat，不是有效的存档备份。");
                }

                string name = Path.GetFileName(root);
                if (root == temp) name = Path.GetFileNameWithoutExtension(zipPath);
                name = Sanitize(name);
                Directory.CreateDirectory(savesDir);
                string dest = Path.Combine(savesDir, name);
                for (int i = 2; Directory.Exists(dest); i++)
                {
                    dest = Path.Combine(savesDir, $"{name} ({i})");
                }
                Directory.Move(root, dest);
                return dest;
            }
            finally
            {
                try { if (Directory.Exists(temp)) Directory.Delete(temp, true); } catch { /* 残留不影响 */ }
            }
        });
    }

    /// <summary>列出本地备份（按时间倒序）。</summary>
    public static List<(string Path, long Size, DateTime Time)> ListBackups()
    {
        var list = new List<(string, long, DateTime)>();
        try
        {
            if (!Directory.Exists(McPaths.BackupsDir)) return list;
            foreach (var file in new DirectoryInfo(McPaths.BackupsDir).EnumerateFiles("*.zip"))
            {
                list.Add((file.FullName, file.Length, file.LastWriteTime));
            }
        }
        catch { /* 读不到就返回空 */ }
        list.Sort((a, b) => b.Item3.CompareTo(a.Item3));
        return list;
    }

    private static string Sanitize(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        name = name.Trim();
        return string.IsNullOrEmpty(name) ? "导入的存档" : name;
    }
}
