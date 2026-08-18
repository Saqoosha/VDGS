using System;
using System.IO;
using UnityEngine;

namespace VDGS
{
    /// <summary>
    /// Rolling frame-time log. The whole point of putting splats in a flight sim is that
    /// it stays fast enough to fly, and that number cannot be read off a screenshot - so
    /// it is sampled continuously and written where an SSH session can read it.
    ///
    /// It appends across launches, and that is load-bearing. It used to truncate on
    /// startup, which quietly destroyed the thing the log exists for: comparing a change
    /// against the run before it. Measuring the giant-splat cull on utlida meant flying
    /// the original, quitting, and flying the pruned version - and quitting is exactly
    /// what erased the baseline. The comparison survived only because the numbers had
    /// already been read out by hand.
    ///
    /// Growth is not a concern worth trading that for: one sample per 5 seconds is about
    /// 45 KB per hour of flying.
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
            try
            {
                // Column header once, on the first ever run; a session banner every time.
                // The per-sample stamp is HH:mm:ss, so without the date on the banner two
                // runs a day apart interleave into one indistinguishable column of times.
                if (!File.Exists(m_Path))
                    File.AppendAllText(m_Path, "time     fps    avg_ms  worst_ms  splats  scenes\n");
                File.AppendAllText(m_Path, string.Format(
                    "=== session {0} ==={1}",
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), Environment.NewLine));
            }
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
