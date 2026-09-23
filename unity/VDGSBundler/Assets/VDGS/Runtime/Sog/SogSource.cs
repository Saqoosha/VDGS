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
                return File.ReadAllBytes(Resolve(m_Root, relativePath));
            }

            public void Dispose() { }
        }

        /// <summary>
        /// Path of a file inside a capture directory, or <see cref="SogException"/>.
        ///
        /// Every name reaching here was written by whoever made the capture:
        /// lod-meta.json names its chunks, and each chunk's meta.json names its images.
        /// A capture is something a player downloads from a public site, so a name like
        /// "../../../../.ssh/id_ed25519" is a file the author chose to have read. The check is
        /// textual, like the web UI's (VdgsPaths.ResolveUi): it does not follow symbolic links.
        /// </summary>
        internal static string Resolve(string root, string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath))
                throw new SogException("empty file name in the capture");
            if (relativePath.IndexOf('\\') >= 0 || relativePath.IndexOf('\0') >= 0
                || Path.IsPathRooted(relativePath))
                throw new SogException("file name leaves the capture: " + relativePath);
            relativePath = StripDotSlash(relativePath);
            foreach (var seg in relativePath.Split('/'))
            {
                if (seg.Length == 0 || seg == ".." || seg == "." || seg.IndexOf(':') >= 0)
                    throw new SogException("file name leaves the capture: " + relativePath);
            }

            var rootFull = Path.GetFullPath(root);
            var candidate = Path.GetFullPath(
                Path.Combine(rootFull, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            var prefix = rootFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                         + Path.DirectorySeparatorChar;
            if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new SogException("file name leaves the capture: " + relativePath);
            if (!File.Exists(candidate))
                throw new SogException("missing file in the capture: " + relativePath);
            return candidate;
        }

        /// <summary>Drops a leading "./", which some writers emit and neither reader means as a segment.</summary>
        private static string StripDotSlash(string name)
        {
            return name.StartsWith("./", StringComparison.Ordinal) ? name.Substring(2) : name;
        }

        /// <summary>A zip entry name as every reader compares it: forward slashes, no leading "./".</summary>
        public static string ZipEntryName(string name) => StripDotSlash(name.Replace('\\', '/'));

        /// <summary>Largest file taken out of a .sog; a legitimate chunk image is far smaller.</summary>
        public const int MaxEntryBytes = 512 * 1024 * 1024;

        /// <summary>Reads a stream to its end, throwing once it passes <paramref name="max"/> bytes.</summary>
        public static byte[] ReadCapped(Stream s, int max, string what)
        {
            using (var ms = new MemoryStream())
            {
                var buf = new byte[81920];
                int n;
                while ((n = s.Read(buf, 0, buf.Length)) > 0)
                {
                    if (ms.Length + n > max) throw new SogException(what + " exceeds " + max + " bytes");
                    ms.Write(buf, 0, n);
                }
                return ms.ToArray();
            }
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
                string key = ZipEntryName(relativePath);
                ZipArchiveEntry entry = m_Zip.GetEntry(key);
                if (entry == null)
                {
                    // Some writers prefix "./" or use backslashes; try a linear scan.
                    foreach (ZipArchiveEntry e in m_Zip.Entries)
                    {
                        if (string.Equals(ZipEntryName(e.FullName), key, StringComparison.Ordinal))
                        {
                            entry = e;
                            break;
                        }
                    }
                }
                if (entry == null) throw new SogException("missing file in the capture: " + relativePath);
                if (entry.Length > MaxEntryBytes) throw new SogException("zip entry too large: " + relativePath);
                // Counted as it inflates: the declared length is the archive's claim, not a limit.
                using (Stream s = entry.Open())
                    return ReadCapped(s, MaxEntryBytes, relativePath);
            }

            public void Dispose()
            {
                m_Zip.Dispose();
                m_Stream.Dispose();
            }
        }
    }
}
