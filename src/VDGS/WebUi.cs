namespace VDGS
{
    /// <summary>
    /// Fallback page when &lt;game&gt;/vdgs/ui/ is missing. The real UI is a Vite build
    /// copied there by tools/deploy.sh; this string is only so /api still has a host
    /// page that explains how to install it.
    /// </summary>
    internal static class WebUi
    {
        internal const string Html = @"<!doctype html>
<html lang=""en"">
<head>
<meta charset=""utf-8"">
<title>VDGS Control</title>
</head>
<body>
<p>UI is not installed. Run tools/deploy.sh --ui.</p>
</body>
</html>
";
    }
}
