using System;
using System.IO;
using System.IO.Compression;

namespace VDGS.Sog
{
    public sealed class SogException : Exception
    {
        public SogException(string message) : base(message) { }
    }

    /// <summary>One place to read a SOG's files from, whatever shape it arrived in.</summary>
    public interface ISogFiles : IDisposable
    {
        /// <summary>Path relative to the SOG root, forward slashes ("0_0/meta.json").</summary>
        byte[] Read(string relativePath);
    }

    public static class SogSource
    {
        public static ISogFiles OpenDirectory(string dir)
        {
            if (string.IsNullOrEmpty(dir)) throw new SogException("empty directory");
            if (!Directory.Exists(dir)) throw new SogException("directory not found: " + dir);
            return new DirectoryFiles(Path.GetFullPath(dir));
        }

        public static ISogFiles OpenZip(string sogPath)
        {
            if (string.IsNullOrEmpty(sogPath)) throw new SogException("empty sog path");
            if (!File.Exists(sogPath)) throw new SogException("sog not found: " + sogPath);
            return new ZipFiles(sogPath);
        }

        private sealed class DirectoryFiles : ISogFiles
        {
            private readonly string m_Root;
            public DirectoryFiles(string root) { m_Root = root; }

            public byte[] Read(string relativePath)
            {
                string full = Path.Combine(m_Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(full)) throw new FileNotFoundException(relativePath, full);
                return File.ReadAllBytes(full);
            }

            public void Dispose() { }
        }

        private sealed class ZipFiles : ISogFiles
        {
            private readonly ZipArchive m_Zip;
            private readonly FileStream m_Stream;

            public ZipFiles(string path)
            {
                m_Stream = File.OpenRead(path);
                m_Zip = new ZipArchive(m_Stream, ZipArchiveMode.Read, leaveOpen: false);
            }

            public byte[] Read(string relativePath)
            {
                string key = relativePath.Replace('\\', '/');
                ZipArchiveEntry entry = m_Zip.GetEntry(key);
                if (entry == null)
                {
                    // Some writers prefix "./" or use backslashes; try a linear scan.
                    foreach (ZipArchiveEntry e in m_Zip.Entries)
                    {
                        if (string.Equals(e.FullName.Replace('\\', '/'), key, StringComparison.Ordinal))
                        {
                            entry = e;
                            break;
                        }
                    }
                }
                if (entry == null) throw new FileNotFoundException(relativePath);
                using (Stream s = entry.Open())
                using (var ms = new MemoryStream((int)entry.Length))
                {
                    s.CopyTo(ms);
                    return ms.ToArray();
                }
            }

            public void Dispose()
            {
                m_Zip.Dispose();
                m_Stream.Dispose();
            }
        }
    }
}
