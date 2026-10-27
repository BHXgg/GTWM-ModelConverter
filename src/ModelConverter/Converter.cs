using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Newtonsoft.Json.Linq;

namespace GTWM.ModelConverter
{
    internal sealed class ConvertOptions
    {
        public string TemplatePath;
        public bool Turn180;
        public string Name;
        public int MaxTextureSize = 2048;
        public int MaxFileMB = 20;
    }

    internal sealed class ConvertResult
    {
        public byte[] Glb;
        public List<string> Log = new List<string>();
        public string Mode;
        public Template Template;
        public List<Vector3> Vertices = new List<Vector3>();
        public List<int> Triangles = new List<int>();
        public int VertexCount;
        public int TriangleCount;
    }

    /// <summary>A model as imported: everything in one scene space, at its bind pose.</summary>
    internal sealed class SourceModel
    {
        public sealed class Part
        {
            public string Name;
            public List<Vector3> Positions = new List<Vector3>();
            public List<Vector3> Normals = new List<Vector3>();
            public List<Vector2> Uvs = new List<Vector2>();
            public List<int> Triangles = new List<int>();
            /// <summary>Per vertex: (node index, weight).</summary>
            public List<List<(int node, float weight)>> Weights = new List<List<(int, float)>>();
            public int Material;
            public bool Skinned;
        }

        public sealed class MaterialData
        {
            public string Name;
            public Vector4 Color = Vector4.One;
            public byte[] Texture;
        }

        public List<string> Names = new List<string>();
        public List<int> Parent = new List<int>();
        public List<Vector3> Rest = new List<Vector3>();
        public HashSet<int> Bones = new HashSet<int>();
        public List<Part> Parts = new List<Part>();
        public List<MaterialData> Materials = new List<MaterialData>();
    }

    internal static class Converter
    {
        public const int MaxVertices = 300_000;

        public static ConvertResult Convert(string modelPath, ConvertOptions options)
        {
            var result = new ConvertResult();
            Template tpl = Template.Load(options.TemplatePath);
            result.Template = tpl;
            result.Log.Add($"Game skeleton: {tpl.Joints.Length} bones, {tpl.Humanoid}.");
            if (tpl.RigCount > 1)
            {
                // Old exporters included the game's hidden alternate characters; binding to the
                // wrong one makes the model invisible in game.
                throw new InvalidDataException($"The game skeleton file is outdated (it holds {tpl.RigCount} characters). Start the game and load into a level once; Model Changer refreshes it automatically. Then convert again.");
            }
            if (!tpl.Humanoid.IsHumanoid)
            {
                result.Log.Add("Note: couldn't identify the game skeleton's limbs; using proximity rigging.");
            }

            SourceModel src = Importer.Import(modelPath, options, result.Log);
            int vertexCount = src.Parts.Sum(p => p.Positions.Count);
            if (vertexCount == 0)
            {
                throw new InvalidDataException("The file has no meshes.");
            }
            if (vertexCount > MaxVertices)
            {
                throw new InvalidDataException($"The model has {vertexCount:N0} vertices; the game allows {MaxVertices:N0}. Reduce it (Blender: Decimate modifier) and try again.");
            }

            HumanoidMap srcMap = Humanoid.Classify(src.Names, src.Parent, src.Bones);
            bool rigged = srcMap.IsHumanoid && tpl.Humanoid.IsHumanoid;
            result.Mode = rigged ? "Rigged humanoid: retargeted to the game skeleton" : src.Bones.Count > 0 && src.Parts.Any(p => p.Skinned) ? "Unrecognised rig: auto-rigged" : "Static model: auto-rigged";
            result.Log.Add(rigged ? $"Model skeleton: {srcMap}." : "Model has no recognisable humanoid skeleton; rigging it to the game skeleton automatically.");

            Fit(src, srcMap, tpl, rigged, options.Turn180, result.Log);

            var srcToTpl = rigged ? MapBones(src, srcMap, tpl) : new Dictionary<int, int>();
            Vector3[] P;
            Quaternion[] Q;
            if (rigged)
            {
                Repose(src, srcMap, tpl, srcToTpl, out P, out Q);
                result.Log.Add($"Matched {srcToTpl.Count} bones and posed the model like the game character.");
            }
            else
            {
                P = src.Rest.ToArray();
                Q = Enumerable.Repeat(Quaternion.Identity, src.Rest.Count).ToArray();
            }

            // Final vertices and template weights.
            int[] jointSlot = new int[tpl.Global.Length];
            for (int i = 0; i < tpl.Joints.Length; i++)
            {
                jointSlot[tpl.Joints[i]] = i;
            }
            var segments = tpl.Joints.Select(j => tpl.Segment(j)).ToArray();
            var parts = new List<(SourceModel.Part part, List<Vector3> pos, List<Vector3> nrm, List<ushort[]> joints, List<Vector4> weights)>();
            int proximityVertices = 0;
            foreach (SourceModel.Part part in src.Parts)
            {
                var pos = new List<Vector3>(part.Positions.Count);
                var nrm = new List<Vector3>(part.Positions.Count);
                var js = new List<ushort[]>(part.Positions.Count);
                var ws = new List<Vector4>(part.Positions.Count);
                for (int v = 0; v < part.Positions.Count; v++)
                {
                    Vector3 p = part.Positions[v];
                    Vector3 n = part.Normals[v];
                    List<(int node, float weight)> wl = part.Weights[v];
                    Vector3 posed = Vector3.Zero;
                    Vector3 normal = Vector3.Zero;
                    float total = 0f;
                    var target = new Dictionary<int, float>();
                    bool unmapped = false;
                    foreach ((int node, float w) in wl)
                    {
                        posed += w * (P[node] + Vector3.Transform(p - src.Rest[node], Q[node]));
                        normal += w * Vector3.Transform(n, Q[node]);
                        total += w;
                        int? mapped = rigged ? MapUp(node, src, srcToTpl) : null;
                        if (mapped == null)
                        {
                            unmapped = true;
                            continue;
                        }
                        int joint = tpl.JointAtOrAbove(mapped.Value);
                        target[joint] = (target.TryGetValue(joint, out float acc) ? acc : 0f) + w;
                    }
                    if (total > 1e-6f)
                    {
                        posed /= total;
                    }
                    else
                    {
                        posed = p;
                        normal = n;
                    }
                    if (unmapped || target.Count == 0)
                    {
                        target = Proximity(posed, tpl, segments);
                        proximityVertices++;
                    }
                    var top = target.OrderByDescending(kv => kv.Value).Take(4).ToList();
                    float sum = top.Sum(kv => kv.Value);
                    var j = new ushort[4];
                    var wv = new float[4];
                    for (int k = 0; k < top.Count; k++)
                    {
                        j[k] = (ushort)jointSlot[top[k].Key];
                        wv[k] = top[k].Value / sum;
                    }
                    pos.Add(posed);
                    nrm.Add(normal.LengthSquared() > 1e-12f ? Vector3.Normalize(normal) : Vector3.UnitY);
                    js.Add(j);
                    ws.Add(new Vector4(wv[0], wv[1], wv[2], wv[3]));
                }
                parts.Add((part, pos, nrm, js, ws));
            }
            if (rigged && proximityVertices > 0)
            {
                result.Log.Add($"{proximityVertices:N0} vertices weren't on a recognised bone and were weighted to the nearest bones.");
            }

            result.VertexCount = vertexCount;
            result.TriangleCount = src.Parts.Sum(p => p.Triangles.Count) / 3;
            foreach (var x in parts)
            {
                int b = result.Vertices.Count;
                result.Vertices.AddRange(x.pos);
                result.Triangles.AddRange(x.part.Triangles.Select(t => t + b));
            }

            int maxTex = options.MaxTextureSize;
            for (int attempt = 0; ; attempt++)
            {
                result.Glb = Write(tpl, src, parts, options.Name ?? Path.GetFileNameWithoutExtension(modelPath), maxTex);
                double mb = result.Glb.Length / (1024.0 * 1024.0);
                if (mb <= options.MaxFileMB || maxTex <= 256 || src.Materials.All(m => m.Texture == null))
                {
                    result.Log.Add($"Output: {mb:0.0} MB, {vertexCount:N0} vertices, {result.TriangleCount:N0} triangles.");
                    if (mb > options.MaxFileMB)
                    {
                        result.Log.Add($"Warning: larger than {options.MaxFileMB} MB; hosts with a lower limit will show you as the default character.");
                    }
                    break;
                }
                maxTex /= 2;
                result.Log.Add($"File was {mb:0.0} MB; shrinking textures to {maxTex}px.");
            }
            return result;
        }

        // ───────────────────────────── fitting ─────────────────────────────

        /// <summary>Orients, scales and places the model onto the game character (in place).</summary>
        private static void Fit(SourceModel src, HumanoidMap map, Template tpl, bool rigged, bool turn, List<string> log)
        {
            Quaternion rot = Quaternion.Identity;
            if (rigged)
            {
                Vector3 upS = Up(map, src.Rest), upT = Up(tpl.Humanoid, tpl.Joints.Length > 0 ? AllPositions(tpl) : null);
                Vector3? latS = Lateral(map, src.Rest), latT = Lateral(tpl.Humanoid, AllPositions(tpl));
                Quaternion q1 = FromTo(upS, upT);
                rot = q1;
                if (latS != null && latT != null)
                {
                    Vector3 a = Flatten(Vector3.Transform(latS.Value, q1), upT);
                    Vector3 b = Flatten(latT.Value, upT);
                    if (a.LengthSquared() > 1e-8f && b.LengthSquared() > 1e-8f)
                    {
                        rot = Quaternion.Concatenate(q1, FromTo(a, b));
                    }
                }
            }
            else
            {
                Vector3 min = new Vector3(float.MaxValue), max = new Vector3(float.MinValue);
                foreach (Vector3 p in src.Parts.SelectMany(x => x.Positions))
                {
                    min = Vector3.Min(min, p);
                    max = Vector3.Max(max, p);
                }
                Vector3 size = max - min;
                if (size.Z > size.Y * 1.25f && size.Z >= size.X)
                {
                    rot = Quaternion.CreateFromAxisAngle(Vector3.UnitX, -(float)Math.PI / 2f);
                    log.Add("Model was Z-up; stood it up.");
                }
                else if (size.X > size.Y * 1.6f && size.X > size.Z * 1.6f)
                {
                    log.Add("Model is wider than it is tall; if it's lying down, re-export it standing up.");
                }
            }
            if (turn)
            {
                rot = Quaternion.Concatenate(rot, Quaternion.CreateFromAxisAngle(Vector3.UnitY, (float)Math.PI));
            }

            Transform(src, p => Vector3.Transform(p, rot), rot);

            Vector3 lo = new Vector3(float.MaxValue), hi = new Vector3(float.MinValue);
            foreach (Vector3 p in src.Parts.SelectMany(x => x.Positions))
            {
                lo = Vector3.Min(lo, p);
                hi = Vector3.Max(hi, p);
            }
            float height = hi.Y - lo.Y;
            if (height < 1e-6f)
            {
                throw new InvalidDataException("The model is flat.");
            }
            float scale = tpl.Height / height;
            Vector3 centre;
            if (rigged && map.Get(Slot.Hips) is int hips)
            {
                centre = src.Rest[hips];
            }
            else
            {
                centre = (lo + hi) / 2f;
            }
            Vector3 tplCentre = tpl.Humanoid.Get(Slot.Hips) is int th ? tpl.Position(th) : (tpl.Min + tpl.Max) / 2f;
            Vector3 offset = new Vector3(tplCentre.X - centre.X * scale, tpl.Min.Y - lo.Y * scale, tplCentre.Z - centre.Z * scale);
            Transform(src, p => p * scale + offset, Quaternion.Identity);
            log.Add($"Scaled ×{scale:0.###} to the character's height.");
        }

        private static void Transform(SourceModel src, Func<Vector3, Vector3> point, Quaternion rot)
        {
            for (int i = 0; i < src.Rest.Count; i++)
            {
                src.Rest[i] = point(src.Rest[i]);
            }
            foreach (SourceModel.Part part in src.Parts)
            {
                for (int v = 0; v < part.Positions.Count; v++)
                {
                    part.Positions[v] = point(part.Positions[v]);
                    part.Normals[v] = Vector3.Transform(part.Normals[v], rot);
                }
            }
        }

        private static Vector3[] AllPositions(Template t) => t.Global.Select(m => m.Translation).ToArray();

        private static Vector3 Up(HumanoidMap m, IList<Vector3> pos)
        {
            int? hips = m.Get(Slot.Hips);
            int? top = m.Get(Slot.Head) ?? m.Get(Slot.Neck) ?? (m.Spine.Count > 0 ? m.Spine[m.Spine.Count - 1] : (int?)null);
            if (hips != null && top != null)
            {
                Vector3 d = pos[top.Value] - pos[hips.Value];
                if (d.LengthSquared() > 1e-10f)
                {
                    return Vector3.Normalize(d);
                }
            }
            return Vector3.UnitY;
        }

        private static Vector3? Lateral(HumanoidMap m, IList<Vector3> pos)
        {
            foreach ((Slot l, Slot r) in new[] { (Slot.LUpperArm, Slot.RUpperArm), (Slot.LUpperLeg, Slot.RUpperLeg), (Slot.LShoulder, Slot.RShoulder) })
            {
                if (m.Get(l) is int a && m.Get(r) is int b)
                {
                    Vector3 d = pos[a] - pos[b];
                    if (d.LengthSquared() > 1e-10f)
                    {
                        return d;
                    }
                }
            }
            return null;
        }

        private static Vector3 Flatten(Vector3 v, Vector3 up) => v - Vector3.Dot(v, up) * up;

        // ───────────────────────────── retargeting ─────────────────────────────

        private static Dictionary<int, int> MapBones(SourceModel src, HumanoidMap s, Template tpl)
        {
            var map = new Dictionary<int, int>();
            foreach (var kv in s.Slots)
            {
                if (tpl.Humanoid.Slots.TryGetValue(kv.Key, out int t))
                {
                    map[kv.Value] = t;
                }
            }
            List<int> ts = tpl.Humanoid.Spine;
            for (int i = 0; i < s.Spine.Count && ts.Count > 0; i++)
            {
                float f = s.Spine.Count == 1 ? 0f : (float)i / (s.Spine.Count - 1);
                map[s.Spine[i]] = ts[(int)Math.Round(f * (ts.Count - 1))];
            }
            return map;
        }

        /// <summary>Template bone for a source node: its own mapping or its nearest mapped ancestor.</summary>
        private static int? MapUp(int node, SourceModel src, Dictionary<int, int> map)
        {
            for (int i = node; i >= 0; i = src.Parent[i])
            {
                if (map.TryGetValue(i, out int t))
                {
                    return t;
                }
            }
            return null;
        }

        /// <summary>
        /// Forward kinematics over the model's skeleton, rotating each mapped bone so it points the
        /// same way as its game counterpart. P/Q are each node's new position and world rotation delta.
        /// </summary>
        private static void Repose(SourceModel src, HumanoidMap s, Template tpl, Dictionary<int, int> map, out Vector3[] P, out Quaternion[] Q)
        {
            int n = src.Rest.Count;
            P = new Vector3[n];
            Q = new Quaternion[n];
            var slotOf = s.Slots.ToDictionary(kv => kv.Value, kv => kv.Key);
            var aims = new Dictionary<int, (int srcTarget, Vector3 tplDir)>();
            foreach (var kv in s.Slots)
            {
                Slot? aim = Humanoid.AimSlot(kv.Key);
                if (aim != null && s.Get(aim.Value) is int sa && tpl.Humanoid.Get(kv.Key) is int t0 && tpl.Humanoid.Get(aim.Value) is int t1)
                {
                    aims[kv.Value] = (sa, tpl.Position(t1) - tpl.Position(t0));
                }
            }
            // Spine: each element points at the next one (or the neck/head).
            int? sTop = s.Get(Slot.Neck) ?? s.Get(Slot.Head);
            for (int i = 0; i < s.Spine.Count; i++)
            {
                int cur = s.Spine[i];
                int? next = i + 1 < s.Spine.Count ? s.Spine[i + 1] : sTop;
                if (next != null && map.TryGetValue(cur, out int tc) && map.TryGetValue(next.Value, out int tn) && tc != tn)
                {
                    aims[cur] = (next.Value, tpl.Position(tn) - tpl.Position(tc));
                }
            }

            int[] order = Enumerable.Range(0, n).OrderBy(i => Depth(i, src.Parent)).ToArray();
            foreach (int b in order)
            {
                int p = src.Parent[b];
                Q[b] = p >= 0 ? Q[p] : Quaternion.Identity;
                P[b] = p >= 0 ? P[p] + Vector3.Transform(src.Rest[b] - src.Rest[p], Q[p]) : src.Rest[b];
                if (aims.TryGetValue(b, out var aim) && aim.tplDir.LengthSquared() > 1e-10f)
                {
                    Vector3 dir = Vector3.Transform(src.Rest[aim.srcTarget] - src.Rest[b], Q[b]);
                    if (dir.LengthSquared() > 1e-10f)
                    {
                        Q[b] = Quaternion.Concatenate(Q[b], FromTo(dir, aim.tplDir));
                    }
                }
            }
        }

        private static int Depth(int i, IList<int> parent)
        {
            int d = 0;
            for (; i >= 0 && d < 1024; i = parent[i])
            {
                d++;
            }
            return d;
        }

        /// <summary>Weights for a vertex by distance to the nearest bone segments.</summary>
        private static Dictionary<int, float> Proximity(Vector3 p, Template tpl, (Vector3 head, Vector3 tail)[] segments)
        {
            var d = new (int joint, float dist)[tpl.Joints.Length];
            for (int i = 0; i < tpl.Joints.Length; i++)
            {
                d[i] = (tpl.Joints[i], SegmentDistance(p, segments[i].head, segments[i].tail));
            }
            Array.Sort(d, (a, b) => a.dist.CompareTo(b.dist));
            float nearest = Math.Max(d[0].dist, 1e-4f);
            var result = new Dictionary<int, float>();
            for (int i = 0; i < Math.Min(4, d.Length); i++)
            {
                if (i > 0 && d[i].dist > nearest * 1.6f)
                {
                    break;
                }
                float w = 1f / (float)Math.Pow(Math.Max(d[i].dist, 1e-4f), 4);
                result[d[i].joint] = (result.TryGetValue(d[i].joint, out float acc) ? acc : 0f) + w;
            }
            return result;
        }

        private static float SegmentDistance(Vector3 p, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a;
            float len = ab.LengthSquared();
            float t = len < 1e-10f ? 0f : Math.Max(0f, Math.Min(1f, Vector3.Dot(p - a, ab) / len));
            return Vector3.Distance(a + ab * t, p);
        }

        public static Quaternion FromTo(Vector3 from, Vector3 to)
        {
            from = Vector3.Normalize(from);
            to = Vector3.Normalize(to);
            float dot = Vector3.Dot(from, to);
            if (dot > 0.999999f)
            {
                return Quaternion.Identity;
            }
            if (dot < -0.999999f)
            {
                Vector3 axis = Vector3.Cross(Vector3.UnitX, from);
                if (axis.LengthSquared() < 1e-6f)
                {
                    axis = Vector3.Cross(Vector3.UnitY, from);
                }
                return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), (float)Math.PI);
            }
            Vector3 c = Vector3.Cross(from, to);
            return Quaternion.Normalize(new Quaternion(c.X, c.Y, c.Z, 1f + dot));
        }

        // ───────────────────────────── output ─────────────────────────────

        private static byte[] Write(Template tpl, SourceModel src, List<(SourceModel.Part part, List<Vector3> pos, List<Vector3> nrm, List<ushort[]> joints, List<Vector4> weights)> parts, string name, int maxTex)
        {
            var w = new GlbWriter();
            // Skeleton: the template's nodes minus its meshes, reindexed.
            var keep = Enumerable.Range(0, tpl.NodeJson.Count).Where(i => tpl.NodeJson[i]["mesh"] == null).ToList();
            var remap = new Dictionary<int, int>();
            for (int i = 0; i < keep.Count; i++)
            {
                remap[keep[i]] = i;
            }
            var nodes = new JArray();
            foreach (int i in keep)
            {
                var node = (JObject)tpl.NodeJson[i].DeepClone();
                node.Remove("mesh");
                node.Remove("skin");
                if (node["children"] is JArray children)
                {
                    var kept = new JArray(children.Select(c => (int)c).Where(remap.ContainsKey).Select(c => remap[c]));
                    if (kept.Count > 0)
                    {
                        node["children"] = kept;
                    }
                    else
                    {
                        node.Remove("children");
                    }
                }
                nodes.Add(node);
            }
            var roots = keep.Where(i => tpl.Parent[i] < 0 || !remap.ContainsKey(tpl.Parent[i])).Select(i => remap[i]).ToList();

            var materials = new JArray();
            var textures = new JArray();
            var images = new JArray();
            foreach (SourceModel.MaterialData m in src.Materials)
            {
                var pbr = new JObject
                {
                    ["baseColorFactor"] = new JArray(m.Color.X, m.Color.Y, m.Color.Z, m.Color.W),
                    ["metallicFactor"] = 0f,
                    ["roughnessFactor"] = 0.9f,
                };
                if (m.Texture != null)
                {
                    byte[] data = Textures.Prepare(m.Texture, maxTex, out string mime);
                    if (data != null)
                    {
                        images.Add(new JObject { ["bufferView"] = w.AddView(data), ["mimeType"] = mime });
                        textures.Add(new JObject { ["source"] = images.Count - 1, ["sampler"] = 0 });
                        pbr["baseColorTexture"] = new JObject { ["index"] = textures.Count - 1 };
                    }
                }
                materials.Add(new JObject { ["name"] = m.Name ?? "material", ["pbrMetallicRoughness"] = pbr });
            }

            var prims = new JArray();
            foreach (var x in parts)
            {
                if (x.part.Triangles.Count == 0)
                {
                    continue;
                }
                var attributes = new JObject
                {
                    ["POSITION"] = w.Vec3(x.pos, minMax: true),
                    ["NORMAL"] = w.Vec3(x.nrm),
                    ["TEXCOORD_0"] = w.Vec2(x.part.Uvs),
                    ["JOINTS_0"] = w.Joints(x.joints),
                    ["WEIGHTS_0"] = w.Vec4(x.weights),
                };
                var prim = new JObject { ["attributes"] = attributes, ["indices"] = w.Indices(x.part.Triangles) };
                if (x.part.Material >= 0 && x.part.Material < materials.Count)
                {
                    prim["material"] = x.part.Material;
                }
                prims.Add(prim);
            }

            int[] jointNodes = tpl.Joints.Select(j => remap.TryGetValue(j, out int r) ? r : -1).ToArray();
            if (jointNodes.Any(j => j < 0))
            {
                throw new InvalidDataException("Template skeleton is inconsistent (a bone is also a mesh node).");
            }
            // Vertices are in the template's model space at bind pose, so inverse bind = inverse global.
            var ibm = tpl.Joints.Select(j => Matrix4x4.Invert(tpl.Global[j], out Matrix4x4 inv) ? inv : Matrix4x4.Identity).ToList();
            var skin = new JObject { ["joints"] = new JArray(jointNodes), ["inverseBindMatrices"] = w.Matrices(ibm) };
            nodes.Add(new JObject { ["name"] = name, ["mesh"] = 0, ["skin"] = 0 });
            roots.Add(nodes.Count - 1);

            var root = new JObject
            {
                ["asset"] = new JObject { ["version"] = "2.0", ["generator"] = "GTWM Model Converter" },
                ["scene"] = 0,
                ["scenes"] = new JArray(new JObject { ["name"] = name, ["nodes"] = new JArray(roots) }),
                ["nodes"] = nodes,
                ["meshes"] = new JArray(new JObject { ["name"] = name, ["primitives"] = prims }),
                ["skins"] = new JArray(skin),
            };
            if (materials.Count > 0)
            {
                root["materials"] = materials;
            }
            if (textures.Count > 0)
            {
                root["textures"] = textures;
                root["images"] = images;
                root["samplers"] = new JArray(new JObject { ["magFilter"] = 9729, ["minFilter"] = 9987, ["wrapS"] = 10497, ["wrapT"] = 10497 });
            }
            return w.Build(root);
        }
    }
}
