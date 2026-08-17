using System;
using System.IO;
using UnityEngine;

namespace VDGS
{
    /// <summary>
    /// Rolling frame-time log. The whole point of putting splats in a flight sim is that
    /// it stays fast enough to fly, and that number cannot be read off a screenshot - so
    /// it is sampled continuously and written where an SSH session can read it.
    /// </summary>
    internal class PerfLog
    {
        private const float kIntervalSeconds = 5f;

        private readonly string m_Path;
        private float m_Accum;
        private int m_Frames;
        private float m_Worst;

        internal PerfLog(string path)
        {
            m_Path = path;
            try { File.WriteAllText(m_Path, "time     fps    avg_ms  worst_ms  splats  scenes\n"); }
            catch { }
        }

        internal void Tick(int splatCount, int sceneCount)
        {
            var dt = Time.unscaledDeltaTime;
            m_Accum += dt;
            m_Frames++;
            if (dt > m_Worst) m_Worst = dt;

            if (m_Accum < kIntervalSeconds || m_Frames == 0)
                return;

            var avgMs = m_Accum / m_Frames * 1000f;
            var fps = m_Frames / m_Accum;

            try
            {
                File.AppendAllText(m_Path, string.Format(
                    "{0} {1,6:0.0} {2,7:0.00} {3,9:0.00} {4,7} {5,7}\n",
                    DateTime.Now.ToString("HH:mm:ss"), fps, avgMs, m_Worst * 1000f,
                    splatCount, sceneCount));
            }
            catch { }

            m_Accum = 0f;
            m_Frames = 0;
            m_Worst = 0f;
        }
    }
}
