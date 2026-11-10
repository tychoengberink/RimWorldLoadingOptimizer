using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;
using RimWorld;
using Unity.Collections;
using UnityEngine;

namespace RimWorldLoadingOptimizer
{
    public static partial class AtlasCache
    {
        private static string MetaPath(string key) => Path.Combine(Dir, key + ".meta");

        private static string RawPath(string key, int slot) => Path.Combine(Dir, key + (slot == 0 ? ".color" : ".mask"));

        private static void WriteMeta(Meta m)
        {
            using (var w = new BinaryWriter(new FileStream(MetaPath(m.key) + ".tmp", FileMode.Create, FileAccess.Write)))
            {
                w.Write(Magic);
                w.Write(m.key);
                w.Write(m.rects.Length);
                foreach (var r in m.rects)
                {
                    w.Write(r.x); w.Write(r.y); w.Write(r.width); w.Write(r.height);
                }
                m.color.Write(w);
                w.Write(m.mask != null);
                m.mask?.Write(w);
            }
        }

        private static Meta ReadMeta(string key)
        {
            var path = MetaPath(key);
            if (!File.Exists(path))
            {
                return null;
            }
            try
            {
                using (var r = new BinaryReader(new FileStream(path, FileMode.Open, FileAccess.Read)))
                {
                    if (r.ReadString() != Magic || r.ReadString() != key)
                    {
                        return null;
                    }
                    var m = new Meta { key = key, rects = new Rect[r.ReadInt32()] };
                    for (int i = 0; i < m.rects.Length; i++)
                    {
                        m.rects[i] = new Rect(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                    }
                    m.color = TexMeta.Read(r);
                    if (r.ReadBoolean())
                    {
                        m.mask = TexMeta.Read(r);
                    }
                    return m;
                }
            }
            catch (Exception e)
            {
                RLOLog.Warning($"atlas cache: unreadable cache entry {key}: {e.Message}");
                return null;
            }
        }

        private static void MoveOver(string path)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            File.Move(path + ".tmp", path);
        }

        private static void Delete(string key)
        {
            foreach (var path in new[] { MetaPath(key), RawPath(key, 0), RawPath(key, 1) })
            {
                foreach (var p in new[] { path, path + ".tmp" })
                {
                    try
                    {
                        if (File.Exists(p))
                        {
                            File.Delete(p);
                        }
                    }
                    catch
                    {
                    }
                }
            }
        }

        private static void Cleanup()
        {
            try
            {
                if (!Directory.Exists(Dir) || usedKeys.Count == 0)
                {
                    return;
                }
                foreach (var file in Directory.GetFiles(Dir))
                {
                    var name = Path.GetFileName(file);
                    var dot = name.IndexOf('.');
                    if (dot > 0 && file != MarkerPath && !usedKeys.Contains(name.Substring(0, dot)))
                    {
                        File.Delete(file);
                    }
                }
            }
            catch (Exception e)
            {
                RLOLog.Warning("atlas cache: cleanup failed: " + e.Message);
            }
        }
    }
}
