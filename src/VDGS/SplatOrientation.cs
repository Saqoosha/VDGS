namespace VDGS
{
    /// <summary>
    /// Turns the two authoring controls - which axis points at the sky, and how far the
    /// capture is turned about it - into the euler angles the transform takes.
    ///
    /// No UnityEngine types here on purpose: this is the part with arithmetic in it, and
    /// the test project can only compile files that do not reach into the engine.
    /// </summary>
    internal static class SplatOrientation
    {
        internal const string DefaultUp = "+y";

        /// <summary>
        /// Unity applies euler angles in the order Z, X, Y, so the Y component is the
        /// last rotation and acts in world space. Up therefore only ever needs X and Z,
        /// and Turn is free to occupy Y whatever Up chose.
        /// </summary>
        internal static void Compose(string up, float turn, out float x, out float y, out float z)
        {
            x = 0f;
            z = 0f;
            switch (up)
            {
                case "+y": break;
                case "-y": x = 180f; break;
                case "+z": x = -90f; break;
                case "-z": x = 90f; break;
                case "+x": z = 90f; break;
                case "-x": z = -90f; break;
                // Anything else is a file written by hand or by a newer build. Identity
                // leaves the capture exactly as it was authored, which is the only answer
                // that cannot make things worse.
                default: break;
            }
            y = turn % 360f;
            if (y < 0f) y += 360f;
        }
    }
}
