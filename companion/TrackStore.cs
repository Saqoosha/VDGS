using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;

namespace VDGSCompanion
{
    /// <summary>
    /// Reads and writes VelociDrone's track table.
    ///
    /// Tracks have no file form - they live only as rows in user11.db, one row per track,
    /// with the gates and barriers as JSON in `value`. A course is small: the field capture
    /// we ship with is 2.6 KB against a 118 MB scene.
    ///
    /// The row also records where a track came from. Anything pulled off the official
    /// server has a non-zero online_id and protected_track set; a course built locally has
    /// both at zero. Only the second kind is ever written here, and never over an existing
    /// row - a track the player edited is theirs.
    ///
    /// Writing happens while the game is closed. That is the whole reason this is a
    /// launcher: the game holds the database open, and importing before it starts means
    /// never contending for the lock.
    /// </summary>
    internal static class TrackStore
    {
        internal sealed class Track
        {
            public long Id;
            public long SceneId;
            public string Name;
            public string Value;      // JSON: gates, barriers
            public long Type;
            public bool FromServer;   // online_id != 0 || protected_track != 0
        }

        internal static string DatabasePath()
        {
            var low = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "AppData", "LocalLow", "velocidrone", "velocidrone");
            return Path.Combine(low, "user11.db");
        }

        private static SqliteConnection Open(string path, bool writable)
        {
            var cs = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = writable ? SqliteOpenMode.ReadWrite : SqliteOpenMode.ReadOnly,
                // Pooling keeps the handle open after the connection is disposed, and the
                // very next thing this tool does is start a game that needs to open the
                // same file. Nothing here is hot enough for a pool to be worth that.
                Pooling = false,
            }.ToString();
            var c = new SqliteConnection(cs);
            c.Open();
            return c;
        }

        internal static List<Track> List(string dbPath)
        {
            var found = new List<Track>();
            using (var c = Open(dbPath, writable: false))
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText =
                    "select id, scene_id, name, value, type, online_id, protected_track from tracks";
                using (var r = cmd.ExecuteReader())
                    while (r.Read())
                        found.Add(new Track
                        {
                            Id = r.GetInt64(0),
                            SceneId = r.GetInt64(1),
                            Name = r.GetString(2),
                            Value = r.IsDBNull(3) ? "" : r.GetString(3),
                            Type = r.IsDBNull(4) ? 0 : r.GetInt64(4),
                            FromServer = (!r.IsDBNull(5) && r.GetInt64(5) != 0)
                                      || (!r.IsDBNull(6) && r.GetInt64(6) != 0),
                        });
            }
            return found;
        }

        internal static Track Find(string dbPath, string name)
        {
            foreach (var t in List(dbPath))
                if (string.Equals(t.Name, name, StringComparison.Ordinal))
                    return t;
            return null;
        }

        internal enum ImportResult { Added, AlreadyPresent, WouldOverwrite }

        /// <summary>
        /// Adds a track, unless one of that name is already there.
        ///
        /// A same-named track is left alone even when its contents differ: the player may
        /// have moved a gate, and silently replacing their course to install ours would be
        /// the worse failure. The caller decides what to do about it.
        /// </summary>
        internal static ImportResult Import(string dbPath, string name, long sceneId,
                                            long type, string valueJson, out string backup)
        {
            backup = null;

            var existing = Find(dbPath, name);
            if (existing != null)
                return existing.Value == valueJson
                    ? ImportResult.AlreadyPresent
                    : ImportResult.WouldOverwrite;

            // Everything about this file is the player's own flying: their courses, their
            // lap times, their quad setups. It is copied before it is touched.
            backup = dbPath + ".vdgs-backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            File.Copy(dbPath, backup, overwrite: false);

            using (var c = Open(dbPath, writable: true))
            using (var tx = c.BeginTransaction())
            {
                using (var cmd = c.CreateCommand())
                {
                    cmd.Transaction = tx;
                    // online_id and protected_track stay 0: this did not come from the
                    // official server, and the game uses those to tell the two apart.
                    cmd.CommandText =
                        "insert into tracks (scene_id, name, value, protected_track, online_id," +
                        " rating, favourite, date, type)" +
                        " values ($scene, $name, $value, 0, 0, 0, 0, $date, $type)";
                    cmd.Parameters.AddWithValue("$scene", sceneId);
                    cmd.Parameters.AddWithValue("$name", name);
                    cmd.Parameters.AddWithValue("$value", valueJson);
                    cmd.Parameters.AddWithValue("$date",
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                    cmd.Parameters.AddWithValue("$type", type);
                    cmd.ExecuteNonQuery();
                }
                tx.Commit();
            }
            return ImportResult.Added;
        }

        /// <summary>
        /// Removes a track this tool added. Refuses to touch anything from the server:
        /// its author put it there, and it is not ours to delete off someone's machine.
        ///
        /// Backed up first, for the same reason importing is - the file holds every lap
        /// time the player has ever set, and none of it is recoverable.
        /// </summary>
        internal static bool Remove(string dbPath, string name, out string backup)
        {
            backup = null;
            var t = Find(dbPath, name);
            if (t == null || t.FromServer) return false;

            backup = dbPath + ".vdgs-backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            if (!File.Exists(backup)) File.Copy(dbPath, backup);

            using (var c = Open(dbPath, writable: true))
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "delete from tracks where id = $id and online_id = 0" +
                                  " and protected_track = 0";
                cmd.Parameters.AddWithValue("$id", t.Id);
                return cmd.ExecuteNonQuery() == 1;
            }
        }
    }
}
