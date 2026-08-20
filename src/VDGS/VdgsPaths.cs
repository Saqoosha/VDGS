using System;
using System.Collections.Generic;
using System.IO;

namespace VDGS
{
    /// <summary>
    /// Layout under &lt;game&gt;/vdgs/ that is not a splat scene, and the mapping from
    /// a request path to a file under ui/.
    /// </summary>
    internal static class VdgsPaths
    {
        internal const string UiDirName = "ui";

        internal enum UiResult
        {
            MissingUi,
            Forbidden,
            NotFound,
            File,
            Spa,
        }

        private static readonly HashSet<string> AssetExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".js", ".css", ".map", ".svg", ".ico", ".png", ".woff2",
        };

        internal static bool IsReservedSceneName(string name)
        {
            return string.Equals(name, UiDirName, StringComparison.OrdinalIgnoreCase);
        }

        internal static UiResult ResolveUi(string uiRoot, string urlPath, out string filePath)
        {
            filePath = null;
            if (string.IsNullOrEmpty(uiRoot) || !Directory.Exists(uiRoot))
                return UiResult.MissingUi;

            var index = Path.Combine(uiRoot, "index.html");
            if (!File.Exists(index))
                return UiResult.MissingUi;

            var rel = string.IsNullOrEmpty(urlPath) ? "/" : urlPath;
            if (rel.IndexOf('\\') >= 0)
                return UiResult.Forbidden;

            try { rel = Uri.UnescapeDataString(rel); }
            catch (UriFormatException) { return UiResult.Forbidden; }

            if (rel.IndexOf('\0') >= 0)
                return UiResult.Forbidden;

            if (!rel.StartsWith("/"))
                rel = "/" + rel;

            if (rel == "/" || rel == "/index.html")
            {
                filePath = index;
                return UiResult.File;
            }

            var trimmed = rel.TrimStart('/');
            foreach (var seg in trimmed.Split('/'))
            {
                if (seg == ".." || seg == "." || seg.IndexOf(':') >= 0)
                    return UiResult.Forbidden;
            }

            var rootFull = Path.GetFullPath(uiRoot);
            var candidate = Path.GetFullPath(Path.Combine(uiRoot, trimmed.Replace('/', Path.DirectorySeparatorChar)));
            var prefix = rootFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                         + Path.DirectorySeparatorChar;
            if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(candidate, rootFull, StringComparison.OrdinalIgnoreCase))
                return UiResult.Forbidden;

            if (File.Exists(candidate))
            {
                filePath = candidate;
                return UiResult.File;
            }

            if (AssetExtensions.Contains(Path.GetExtension(candidate)))
                return UiResult.NotFound;

            filePath = index;
            return UiResult.Spa;
        }

        internal static string MimeType(string filePath)
        {
            switch (Path.GetExtension(filePath).ToLowerInvariant())
            {
                case ".html": return "text/html; charset=utf-8";
                case ".js": return "text/javascript; charset=utf-8";
                case ".css": return "text/css; charset=utf-8";
                case ".svg": return "image/svg+xml";
                case ".png": return "image/png";
                case ".ico": return "image/x-icon";
                case ".woff2": return "font/woff2";
                case ".map": return "application/json";
                default: return "application/octet-stream";
            }
        }

        internal static string CacheControl(string urlPath)
        {
            if (string.IsNullOrEmpty(urlPath) || urlPath == "/" || urlPath == "/index.html")
                return "no-store";
            if (urlPath.StartsWith("/assets/", StringComparison.OrdinalIgnoreCase))
                return "public, max-age=31536000, immutable";
            return "no-store";
        }
    }
}
