using System;
using System.IO;

namespace ScdDesktop
{
    internal sealed class WorkbookSnapshot
    {
        public string Path { get; private set; }
        public long Length { get; private set; }
        public DateTime LastWrite { get; private set; }
        public byte[] Content { get; set; }
        public int Revision { get; set; }

        public static WorkbookSnapshot Read(string path, WorkbookSnapshot previous)
        {
            var before = new FileInfo(path);
            if (!before.Exists) throw new FileNotFoundException("找不到工作簿。", path);
            var snapshot = new WorkbookSnapshot { Path = path, Length = before.Length, LastWrite = before.LastWriteTimeUtc };
            if (previous != null && previous.Matches(snapshot)) return null;
            if (snapshot.Length > int.MaxValue) throw new IOException("工作簿超过可读取的文件大小。");

            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (stream.Length > int.MaxValue) throw new IOException("工作簿超过可读取的文件大小。");
                var data = new byte[(int)stream.Length];
                int offset = 0;
                while (offset < data.Length)
                {
                    int count = stream.Read(data, offset, data.Length - offset);
                    if (count == 0) throw new IOException("工作簿正在保存。");
                    offset += count;
                }
                snapshot.Content = data;
            }
            var after = new FileInfo(path);
            if (!after.Exists || snapshot.Length != snapshot.Content.Length ||
                after.Length != snapshot.Length || after.LastWriteTimeUtc != snapshot.LastWrite)
                throw new IOException("工作簿正在保存。");
            return snapshot;
        }

        private bool Matches(WorkbookSnapshot other)
        {
            return string.Equals(Path, other.Path, StringComparison.OrdinalIgnoreCase) &&
                Length == other.Length && LastWrite == other.LastWrite;
        }
    }
}
