using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Newtonsoft.Json.Linq;

namespace GTWM.ModelConverter
{
    /// <summary>
    /// The game's character as exported by the Model Changer mod (GTWM/templates/character_template.glb):
    /// its skeleton in bind pose and its body mesh, in glTF space.
    /// </summary>
    internal sealed class Template
    {
        public string[] Names;
        public int[] Parent;
        public Matrix4x4[] Global;
        public JArray NodeJson;
        /// <summary>Nodes that are bones (skin joints), sorted.</summary>
        public int[] Joints;
        public HumanoidMap Humanoid;
        public List<Vector3> BodyVertices = new List<Vector3>();
        public List<int> BodyTriangles = new List<int>();
        public Vector3 Min;
        public Vector3 Max;
        /// <summary>Separate skeletons found in the file; only the body's is used.</summary>
        public int RigCount;

        public Vector3 Position(int node) => Global[node].Translation;

        public float Height => Max.Y - Min.Y;

        public static Template Load(string path)
        {
            GlbDocument doc = GlbDocument.Load(File.ReadAllBytes(path));
            var t = new Template();
            JArray nodes = doc.Nodes;
            int n = nodes.Count;
            t.Names = nodes.Select((x, i) => (string)x["name"] ?? "node" + i).ToArray();
            t.Parent = Enumerable.Repeat(-1, n).ToArray();
            for (int i = 0; i < n; i++)
            {
                foreach (JToken c in nodes[i]["children"] as JArray ?? new JArray())
                {
                    t.Parent[(int)c] = i;
                }
            }
            t.Global = new Matrix4x4[n];
            var done = new bool[n];
            for (int i = 0; i < n; i++)
            {
                Resolve(i, nodes, t.Parent, t.Global, done);
            }
            t.NodeJson = nodes;

            // Skin each mesh into model space (bind pose: global × inverse bind).
            var skins = doc.Json["skins"] as JArray ?? new JArray();
            if (skins.Count == 0)
            {
                throw new InvalidDataException("The template has no skeleton.");
            }
            var parts = new List<(int skin, List<Vector3> verts, List<int> tris)>();
            for (int i = 0; i < n; i++)
            {
                JToken node = nodes[i];
                if (node["mesh"] == null)
                {
                    continue;
                }
                int skinIndex = node["skin"] != null ? (int)node["skin"] : -1;
                JToken skin = skinIndex >= 0 ? skins[skinIndex] : null;
                int[] skinJoints = skin != null ? ((JArray)skin["joints"]).Select(x => (int)x).ToArray() : null;
                Matrix4x4[] ibm = skin?["inverseBindMatrices"] != null ? doc.ReadMatrices((int)skin["inverseBindMatrices"]) : null;
                foreach (JToken prim in (JArray)doc.Json["meshes"][(int)node["mesh"]]["primitives"])
                {
                    JToken attrs = prim["attributes"];
                    float[] p = doc.ReadFloats((int)attrs["POSITION"], out int count, out _);
                    float[] jn = skinJoints != null && attrs["JOINTS_0"] != null ? doc.ReadFloats((int)attrs["JOINTS_0"], out _, out _) : null;
                    float[] wt = skinJoints != null && attrs["WEIGHTS_0"] != null ? doc.ReadFloats((int)attrs["WEIGHTS_0"], out _, out _) : null;
                    var verts = new List<Vector3>(count);
                    for (int v = 0; v < count; v++)
                    {
                        var local = new Vector3(p[v * 3], p[v * 3 + 1], p[v * 3 + 2]);
                        Vector3 world = Vector3.Zero;
                        if (jn != null && wt != null && ibm != null)
                        {
                            for (int k = 0; k < 4; k++)
                            {
                                float w = wt[v * 4 + k];
                                int ji = (int)jn[v * 4 + k];
                                if (w > 0f && ji < skinJoints.Length)
                                {
                                    world += w * Vector3.Transform(local, ibm[ji] * t.Global[skinJoints[ji]]);
                                }
                            }
                        }
                        else
                        {
                            world = Vector3.Transform(local, t.Global[i]);
                        }
                        verts.Add(world);
                    }
                    var tris = new List<int>();
                    if (prim["indices"] != null)
                    {
                        float[] idx = doc.ReadFloats((int)prim["indices"], out int ic, out _);
                        for (int k = 0; k + 2 < ic; k += 3)
                        {
                            tris.Add((int)idx[k]);
                            tris.Add((int)idx[k + 1]);
                            tris.Add((int)idx[k + 2]);
                        }
                    }
                    parts.Add((skinIndex, verts, tris));
                }
            }

            // Templates from older exporters can hold several characters with separate rigs (the
            // game keeps hidden alternates). Skins that share joints are one rig; use the rig of the
            // tallest mesh, which is the body.
            int[][] skinJointSets = skins.Select(sk => ((JArray)sk["joints"]).Select(x => (int)x).ToArray()).ToArray();
            int[] group = Enumerable.Range(0, skins.Count).ToArray();
            Func<int, int> find = null;
            find = x => group[x] == x ? x : (group[x] = find(group[x]));
            for (int a = 0; a < skins.Count; a++)
            {
                for (int b = a + 1; b < skins.Count; b++)
                {
                    if (skinJointSets[a].Intersect(skinJointSets[b]).Any())
                    {
                        group[find(a)] = find(b);
                    }
                }
            }
            var skinned = parts.Where(x => x.skin >= 0 && x.verts.Count > 0).ToList();
            int mainGroup = skinned.Count > 0
                ? find(skinned.OrderByDescending(x => x.verts.Max(v => v.Y) - x.verts.Min(v => v.Y)).First().skin)
                : find(0);

            var joints = new SortedSet<int>();
            for (int k = 0; k < skins.Count; k++)
            {
                if (find(k) == mainGroup)
                {
                    joints.UnionWith(skinJointSets[k]);
                }
            }
            t.Joints = joints.ToArray();
            t.RigCount = Enumerable.Range(0, skins.Count).Select(find).Distinct().Count();
            t.Humanoid = ModelConverter.Humanoid.Classify(t.Names, t.Parent, new HashSet<int>(t.Joints));

            foreach (var part in parts.Where(x => x.skin >= 0 && find(x.skin) == mainGroup))
            {
                int baseVertex = t.BodyVertices.Count;
                t.BodyVertices.AddRange(part.verts);
                t.BodyTriangles.AddRange(part.tris.Select(x => baseVertex + x));
            }
            IEnumerable<Vector3> extent = t.BodyVertices.Count > 0 ? t.BodyVertices : t.Joints.Select(t.Position);
            t.Min = extent.Aggregate(new Vector3(float.MaxValue), Vector3.Min);
            t.Max = extent.Aggregate(new Vector3(float.MinValue), Vector3.Max);
            return t;
        }

        private static void Resolve(int i, JArray nodes, int[] parent, Matrix4x4[] global, bool[] done)
        {
            if (done[i])
            {
                return;
            }
            Matrix4x4 local = GlbDocument.LocalMatrix(nodes[i]);
            if (parent[i] >= 0)
            {
                Resolve(parent[i], nodes, parent, global, done);
                global[i] = local * global[parent[i]];
            }
            else
            {
                global[i] = local;
            }
            done[i] = true;
        }

        /// <summary>Nearest joint at or above a node (non-joint helpers can't carry weights).</summary>
        public int JointAtOrAbove(int node)
        {
            for (int i = node; i >= 0; i = Parent[i])
            {
                if (Array.BinarySearch(Joints, i) >= 0)
                {
                    return i;
                }
            }
            return Joints[0];
        }

        /// <summary>Segment from a joint to the mean of its child joints, for proximity weighting.</summary>
        public (Vector3 head, Vector3 tail) Segment(int joint)
        {
            Vector3 head = Position(joint);
            int[] kids = Joints.Where(j => Parent[j] == joint).ToArray();
            if (kids.Length == 0)
            {
                return (head, head);
            }
            Vector3 tail = kids.Aggregate(Vector3.Zero, (a, k) => a + Position(k)) / kids.Length;
            return (head, tail);
        }
    }
}
