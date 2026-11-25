using System.IO;
using StormConnect.Protocol;

namespace StormConnect.Desktop.Engine;

public static class FileSystemHandler
{
    public static FileListResponsePayload ListDirectory(string? requestedPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(requestedPath))
            {
                var drives = DriveInfo.GetDrives()
                    .Where(d => d.IsReady)
                    .Select(d => new FileEntryInfo(
                        $"{d.Name} ({d.VolumeLabel})",
                        d.RootDirectory.FullName,
                        true,
                        d.TotalSize,
                        DateTime.UtcNow))
                    .ToList();
                return new FileListResponsePayload("", null, drives, null);
            }

            var dir = new DirectoryInfo(requestedPath);
            if (!dir.Exists)
                return new FileListResponsePayload(requestedPath, dir.Parent?.FullName, [], "Directory does not exist.");

            var entries = new List<FileEntryInfo>();
            foreach (var sub in dir.EnumerateDirectories().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    entries.Add(new FileEntryInfo(sub.Name, sub.FullName, true, 0, sub.LastWriteTimeUtc));
                }
                catch { }
            }
            foreach (var file in dir.EnumerateFiles().OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    entries.Add(new FileEntryInfo(file.Name, file.FullName, false, file.Length, file.LastWriteTimeUtc));
                }
                catch { }
            }

            return new FileListResponsePayload(dir.FullName, dir.Parent?.FullName ?? "", entries, null);
        }
        catch (Exception ex)
        {
            return new FileListResponsePayload(requestedPath ?? "", "", [], ex.Message);
        }
    }

    public static FileOperationResponsePayload ExecuteOperation(FileOperationRequestPayload req)
    {
        try
        {
            switch (req.Kind)
            {
                case FileOpKind.CreateDirectory:
                    Directory.CreateDirectory(req.Path);
                    return new FileOperationResponsePayload(true, "OK");

                case FileOpKind.DeletePath:
                    if (Directory.Exists(req.Path))
                        Directory.Delete(req.Path, recursive: true);
                    else if (File.Exists(req.Path))
                        File.Delete(req.Path);
                    return new FileOperationResponsePayload(true, "OK");

                case FileOpKind.RenamePath:
                    if (string.IsNullOrWhiteSpace(req.TargetPath))
                        return new FileOperationResponsePayload(false, "Target path empty.");
                    if (Directory.Exists(req.Path))
                        Directory.Move(req.Path, req.TargetPath);
                    else if (File.Exists(req.Path))
                        File.Move(req.Path, req.TargetPath, overwrite: true);
                    return new FileOperationResponsePayload(true, "OK");

                default:
                    return new FileOperationResponsePayload(false, "Unknown operation.");
            }
        }
        catch (Exception ex)
        {
            return new FileOperationResponsePayload(false, ex.Message);
        }
    }
}
